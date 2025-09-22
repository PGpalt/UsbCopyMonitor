using System.Collections.Concurrent;
using System.Text.Json;
using UsbCopyMon.Shared;

namespace UsbCopyMon.Service;

public sealed class SessionManager
{
    private readonly DeviceMap _devices;
    private readonly PipeServer _pipe;

    // Active sessions keyed by (pid, devId)
    private readonly ConcurrentDictionary<(int pid, string devId), TransferSession> _open = new();

    // Quick index: pid -> devId
    private readonly ConcurrentDictionary<int, string> _activeByPid = new();

    // Recent READ hints to pair with WRITEs
    private readonly ConcurrentDictionary<int, Dictionary<string, ReadHint>> _readHints = new();
    private readonly TimeSpan _hintTtl = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _idle = TimeSpan.FromSeconds(10);
    private readonly string _logDir;

    public SessionManager(DeviceMap devices, PipeServer pipe)
    {
        _devices = devices;
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "UsbCopyMon", "logs");
        Directory.CreateDirectory(_logDir);
        _pipe = pipe;
    }

    // ---------------- API called by FileMonitor ----------------

    public void HintRead(int pid, DateTime when, string path, bool isUsb, string user)
    {
        var basename = Path.GetFileName(path);
        if (string.IsNullOrEmpty(basename)) return;

        var hint = new ReadHint(
            new DateTimeOffset(when),
            path,
            isUsb,
            user ?? string.Empty,
            isUsb ? _devices.ResolveForPath(path) : null);

        var dict = _readHints.GetOrAdd(pid, _ => new Dictionary<string, ReadHint>(StringComparer.OrdinalIgnoreCase));
        lock (dict)
        {
            dict[basename] = hint;
        }
    }

    public void RecordWrite(int pid, DateTime when, string destPath, long bytes, string? writeUser, bool isUsbDest)
    {
        var basename = Path.GetFileName(destPath);
        if (string.IsNullOrEmpty(basename)) return;

        _readHints.TryGetValue(pid, out var samePidMap);
        ReadHint srcHint = default;
        var haveHint = samePidMap is not null
                       && TryGetValidOppositeHint(samePidMap, basename, isUsbDest, out srcHint);

        if (!haveHint)
            haveHint = TryFindHintAnyPid(basename, !isUsbDest, out srcHint, writeUser ?? "");

        string? devId = null;
        DeviceInfo? usbDev = null;
        if (isUsbDest)
        {
            usbDev = _devices.ResolveForPath(destPath);
            devId = usbDev?.PnpId;
        }
        else if (haveHint && srcHint.IsUsb && srcHint.Device is not null)
        {
            usbDev = srcHint.Device;
            devId = usbDev.PnpId;
        }
        if (string.IsNullOrEmpty(devId)) return;

        var user = !string.IsNullOrEmpty(srcHint.User) ? srcHint.User
                 : !string.IsNullOrEmpty(writeUser) ? writeUser!
                 : "Unknown";

        var key = (pid, devId!);
        var s = _open.GetOrAdd(key, _ => new TransferSession(when, user, usbDev!));
        s.Touch(when);

        // Start the minimal attribution prompt immediately on first encounter
        s.EnsureAttributionStarted(_pipe, BuildMinimalCopyLog(s, when));

        // Keep accumulating activity and pairs
        s.AddWrite(destPath, bytes, isUsbDest);
        if (haveHint) s.AddPair(destPath, srcHint.Path);

        _activeByPid[pid] = devId!;
    }

    public void AddNonUsbIfPaired(int pid, DateTime when, string filePath, long bytes)
    {
        if (!_activeByPid.TryGetValue(pid, out var devId)) return;
        var key = (pid, devId);
        if (_open.TryGetValue(key, out var s))
        {
            s.Touch(when);
            s.AddNonUsb(filePath, bytes);
        }
    }

    // ---------------- Background loop ----------------

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Sweep(); } catch { /* ignore */ }
            await Task.Delay(2000, ct);
        }
    }

    private void Sweep()
    {
        PurgeOldHints();

        var now = DateTimeOffset.Now;
        foreach (var kv in _open.ToArray())
        {
            if (now - kv.Value.LastActivity > _idle &&
                _open.TryRemove(kv.Key, out var sess))
            {
                if (_activeByPid.TryGetValue(kv.Key.pid, out var cur) && cur == kv.Key.devId)
                    _activeByPid.TryRemove(kv.Key.pid, out _);

                CloseAndWrite(sess);
            }
        }
    }

    private void CloseAndWrite(TransferSession s)
    {
        var matchedDest = s.MatchedDestFiles();
        var matchedSrc = s.MatchedSourceFiles();

        bool usePairs = matchedDest.Count > 0 && matchedSrc.Count > 0;

        var destList = usePairs
            ? matchedDest
            : (s.UsbWriteBytes >= s.NonUsbWriteBytes ? s.UsbFiles : s.NonUsbFiles);

        var srcList = usePairs
            ? matchedSrc
            : (s.UsbWriteBytes >= s.NonUsbWriteBytes ? s.NonUsbFiles : s.UsbFiles);

        var sourcePath = CommonDirectory(srcList);
        var destPath = CommonDirectory(destList);

        var deviceName = string.IsNullOrWhiteSpace(s.Device.Label) ? s.Device.DriveLetter : s.Device.Label;

        var fileNames = destList.Select(Path.GetFileName)
                                .Where(n => !string.IsNullOrEmpty(n))
                                .Select(n => n!)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();

        // Use the previously captured attribution (if any). No prompting here.
        var attributedTo = s.TryGetAttribution(out var who) && !string.IsNullOrWhiteSpace(who)
            ? who!
            : "Unknown";

        var rec = new CopyLog(
            Timestamp: s.Started,
            Computer: Environment.MachineName,
            User: s.User,
            SourcePath: sourcePath,
            DestPath: destPath,
            DeviceName: deviceName,
            FileNames: fileNames,
            AttributedTo: attributedTo);

        var path = Path.Combine(_logDir, $"{DateTime.UtcNow:yyyyMMdd}.jsonl");
        File.AppendAllText(path, JsonSerializer.Serialize(rec) + Environment.NewLine);
    }

    // ---------------- Helpers ----------------

    private CopyLog BuildMinimalCopyLog(TransferSession s, DateTime when)
    {
        // Only send what's needed to pre-fill the prompt (no paths/file lists)
        var deviceName = string.IsNullOrWhiteSpace(s.Device.Label) ? s.Device.DriveLetter : s.Device.Label;
        return new CopyLog(
            Timestamp: new DateTimeOffset(when),
            Computer: Environment.MachineName,
            User: s.User,              // used to pre-fill textbox
            SourcePath: "",            // intentionally blank
            DestPath: "",              // intentionally blank
            DeviceName: deviceName,    // optional; tray may ignore
            FileNames: new List<string>(),
            AttributedTo: null
        );
    }

    private void PurgeOldHints()
    {
        foreach (var kv in _readHints.ToArray())
        {
            var pid = kv.Key;
            var dict = kv.Value;
            lock (dict)
            {
                foreach (var file in dict.Keys.ToList())
                {
                    if (DateTimeOffset.Now - dict[file].When > _hintTtl)
                        dict.Remove(file);
                }
                if (dict.Count == 0)
                    _readHints.TryRemove(pid, out _);
            }
        }
    }

    private static bool TryGetValidOppositeHint(
        Dictionary<string, ReadHint> dict, string basename, bool isUsbDest, out ReadHint hint)
    {
        hint = default;
        if (!dict.TryGetValue(basename, out var h)) return false;
        if (DateTimeOffset.Now - h.When > TimeSpan.FromSeconds(5)) return false;
        if (h.IsUsb == isUsbDest) return false;
        hint = h;
        return true;
    }

    private bool TryFindHintAnyPid(string basename, bool wantUsbSide, out ReadHint hint, string preferredUser = "")
    {
        hint = default;
        var bestWhen = DateTimeOffset.MinValue;

        foreach (var pidMap in _readHints.Values)
        {
            if (!pidMap.TryGetValue(basename, out var h)) continue;
            if (h.IsUsb != wantUsbSide) continue;
            if (DateTimeOffset.Now - h.When > _hintTtl) continue;

            var scoreBias = (!string.IsNullOrEmpty(preferredUser) &&
                             h.User.Equals(preferredUser, StringComparison.OrdinalIgnoreCase))
                            ? TimeSpan.FromSeconds(5) : TimeSpan.Zero;

            var effectiveWhen = h.When + scoreBias;
            if (effectiveWhen > bestWhen) { hint = h; bestWhen = effectiveWhen; }
        }

        return bestWhen != DateTimeOffset.MinValue;
    }

    private static string CommonDirectory(List<string> paths)
    {
        if (paths.Count == 0) return "";
        return Path.GetDirectoryName(paths[0]) ?? "";
    }

    // ---------------- Nested classes ----------------

    private readonly struct ReadHint
    {
        public DateTimeOffset When { get; }
        public string Path { get; }
        public bool IsUsb { get; }
        public string User { get; }
        public DeviceInfo? Device { get; }

        public ReadHint(DateTimeOffset when, string path, bool isUsb, string user, DeviceInfo? device)
        {
            When = when;
            Path = path;
            IsUsb = isUsb;
            User = user;
            Device = device;
        }
    }

    private sealed class TransferSession
    {
        public DateTimeOffset Started { get; }
        public DateTimeOffset LastActivity { get; private set; }
        public string User { get; }
        public DeviceInfo Device { get; }

        public long UsbWriteBytes { get; private set; }
        public long NonUsbWriteBytes { get; private set; }

        public List<string> UsbFiles { get; } = new();
        public List<string> NonUsbFiles { get; } = new();

        private readonly List<(string Dest, string Src)> _pairs = new();

        // attribution state
        private readonly object _attrLock = new();
        private Task? _attrTask;
        private string? _attributedTo;

        public TransferSession(DateTime started, string user, DeviceInfo device)
        {
            Started = new DateTimeOffset(started);
            LastActivity = Started;
            User = user;
            Device = device;
        }

        public void Touch(DateTime when) => LastActivity = new DateTimeOffset(when);

        public void AddWrite(string path, long bytes, bool isUsbDest)
        {
            if (isUsbDest) { UsbFiles.Add(path); UsbWriteBytes += bytes; }
            else { NonUsbFiles.Add(path); NonUsbWriteBytes += bytes; }
        }

        public void AddNonUsb(string path, long bytes) => NonUsbFiles.Add(path);

        public void AddPair(string dest, string src) => _pairs.Add((dest, src));

        public List<string> MatchedDestFiles() => _pairs.Select(p => p.Dest).ToList();
        public List<string> MatchedSourceFiles() => _pairs.Select(p => p.Src).ToList();

        public bool EnsureAttributionStarted(PipeServer pipe, CopyLog seed)
        {
            lock (_attrLock)
            {
                if (_attrTask != null) return false; // already started

                _attrTask = Task.Run(async () =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromHours(1));
                        var answer = await pipe.RequestAttributionAsync(seed, cts.Token);
                        _attributedTo = string.IsNullOrWhiteSpace(answer) ? "Unknown" : answer;
                    }
                    catch
                    {
                        _attributedTo = "Unknown";
                    }
                });

                return true;
            }
        }

        public bool TryGetAttribution(out string? who)
        {
            who = _attributedTo;
            return !string.IsNullOrWhiteSpace(who);
        }
    }
}
