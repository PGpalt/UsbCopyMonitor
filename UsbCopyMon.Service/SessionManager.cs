using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using UsbCopyMon.Shared;

namespace UsbCopyMon.Service;

public sealed class SessionManager
{
    private readonly DeviceMap _devices;
    private readonly PipeServer _pipe;
    private readonly IOptionsMonitor<UsbCopyMonOptions> _opts;

    private readonly ConcurrentDictionary<(int pid, string devId), TransferSession> _open = new();

    // Read hints: only from PC (non-USB) to infer SourcePath for PC → USB copies.
    private readonly ConcurrentDictionary<int, Dictionary<string, ReadHint>> _readHints = new();
    private readonly TimeSpan _hintTtl = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _idle = TimeSpan.FromSeconds(10);
    private readonly string _logDir;
    private readonly ILogger<SessionManager> _log;

    public SessionManager(DeviceMap devices, PipeServer pipe, IOptionsMonitor<UsbCopyMonOptions> opts, ILogger<SessionManager> log)
    {
        _devices = devices;
        _pipe = pipe;
        _opts = opts;
        _log = log;

        _log.LogInformation("Config loaded from {Base}", AppContext.BaseDirectory);
        _log.LogInformation("UsbCopyMon options: {@opts}", _opts.CurrentValue);
        _opts.OnChange(o => _log.LogInformation("UsbCopyMon options reloaded: {@opts}", o));

        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "UsbCopyMon", "logs");
        Directory.CreateDirectory(_logDir);
    }

    // ---------------- API called by FileMonitor ----------------

    /// <summary>
    /// Keep a short-lived hint for a filename read from PC storage, so we can pair it
    /// with a subsequent write to USB and populate SourcePath.
    /// </summary>
    public void HintRead(int pid, DateTime when, string path, string user)
    {
        var basename = Path.GetFileName(path);
        if (string.IsNullOrEmpty(basename)) return;

        var hint = new ReadHint(
            new DateTimeOffset(when),
            path,
            user ?? string.Empty
        );

        var dict = _readHints.GetOrAdd(pid, _ => new Dictionary<string, ReadHint>(StringComparer.OrdinalIgnoreCase));
        lock (dict) { dict[basename] = hint; }
    }

    /// <summary>
    /// Records a write whose destination is on a USB device. This is the only write path we keep,
    /// effectively filtering to PC → USB transfers only. We try to pair with a prior PC read to fill SourcePath.
    /// </summary>
    public void RecordUsbDestinationWrite(int pid, DateTime when, string destPath, long bytes, string? writeUser)
    {
        var basename = Path.GetFileName(destPath);
        if (string.IsNullOrEmpty(basename)) return;

        // Resolve the USB device for the destination (must exist by the time we get here).
        var usbDev = _devices.ResolveForPath(destPath);
        if (usbDev is null) return; // defensive guard

        // Try to find a PC-side read hint for the same filename
        _readHints.TryGetValue(pid, out var samePidMap);
        ReadHint srcHint = default;
        var haveHint = samePidMap is not null && TryGetValidHint(samePidMap, basename, out srcHint);

        if (!haveHint)
            haveHint = TryFindHintAnyPid(basename, out srcHint, writeUser ?? "");

        var user = !string.IsNullOrEmpty(srcHint.User) ? srcHint.User
                 : !string.IsNullOrEmpty(writeUser) ? writeUser!
                 : "Unknown";

        var key = (pid, usbDev.PnpId);
        var s = _open.GetOrAdd(key, _ => new TransferSession(when, user, usbDev));
        s.Touch(when);

        // Respect config: only start attribution if prompts are enabled
        if (_opts.CurrentValue.PromptEnabled)
            s.EnsureAttributionStarted(_pipe, s.User);

        // Record the USB-destination write.
        s.AddUsbWrite(destPath, bytes);

        // If we found a PC source hint, remember the pairing so SourcePath can be set.
        if (haveHint) s.AddPair(destPath, srcHint.Path);
    }

    // ---------------- Background loop ----------------

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Sweep();
                EnforceRetention();
            }
            catch { /* ignore */ }

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
                CloseAndWrite(sess);
            }
        }
    }

    private void CloseAndWrite(TransferSession s)
    {
        var matchedDest = s.MatchedDestFiles();
        var matchedSrc = s.MatchedSourceFiles();

        // If we have pairings, use them to determine SourcePath (PC) and DestPath (USB).
        // Otherwise, we still log DestPath from USB writes and leave SourcePath empty (best-effort).
        var destList = s.UsbFiles;
        var srcList = matchedSrc.Count > 0 ? matchedSrc : new List<string>();

        var sourcePath = CommonDirectory(srcList);        // PC
        var destPath = CommonDirectory(destList);       // USB

        var deviceName = string.IsNullOrWhiteSpace(s.Device.Label) ? s.Device.DriveLetter : s.Device.Label;

        // Helper strips ADS like ":Zone.Identifier:$DATA"
        static string FileNameSansAds(string path)
        {
            var name = Path.GetFileName(path) ?? string.Empty;
            var idx = name.IndexOf(':');
            return idx >= 0 ? name[..idx] : name;
        }

        var fileNames = destList
            .Select(p => FileNameSansAds(p))
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var attributedTo = s.TryGetAttribution(out var who) && !string.IsNullOrWhiteSpace(who)
            ? who!
            : "Unknown";

        var rec = new CopyLog(
            Timestamp: s.Started,
            Computer: Environment.MachineName,
            User: s.User,
            SourcePath: sourcePath, // stays PC-side
            DestPath: destPath,     // stays USB-side
            DeviceName: deviceName,
            FileNames: fileNames,
            AttributedTo: attributedTo);

        var json = JsonSerializer.Serialize(rec);

        _log.LogInformation("Session closed (PC → USB): {User} → {Device} | {Count} files",
                    s.User, s.Device.Label ?? s.Device.DriveLetter, fileNames.Count);
        _log.LogDebug("  Details: {@rec}", rec);

        // Local file write (configurable)
        if (_opts.CurrentValue.LocalLoggingEnabled)
        {
            var path = Path.Combine(_logDir, $"{DateTime.UtcNow:yyyyMMdd}.jsonl");
            File.AppendAllText(path, json + Environment.NewLine);
        }

        // Optional UDP send (fire-and-forget)
        var udp = _opts.CurrentValue.Syslog;
        if (udp is not null && udp.Enabled)
        {
            _ = Task.Run(() => SendUdpSafe(json, udp.Ip, udp.Port));
        }
    }

    // ---------------- UDP helper ----------------

    private static void SendUdpSafe(string payload, string ip, int port)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ip) || port <= 0 || port > 65535) return;

            using var client = new UdpClient();
            client.Connect(IPAddress.Parse(ip), port);
            var bytes = Encoding.UTF8.GetBytes(payload);
            client.Send(bytes, bytes.Length);
        }
        catch
        {
            // swallow: logging over UDP is best-effort
        }
    }

    // ---------------- Retention ----------------

    private void EnforceRetention()
    {
        var minutes = _opts.CurrentValue.RetentionMinutes;
        var days = _opts.CurrentValue.RetentionDays;

        // Decide cutoff
        DateTime cutoffUtc;
        if (minutes > 0)
            cutoffUtc = DateTime.UtcNow.AddMinutes(-minutes);
        else if (days > 0)
            cutoffUtc = DateTime.UtcNow.Date.AddDays(-days);
        else
            return; // retention disabled

        try
        {
            foreach (var f in Directory.EnumerateFiles(_logDir, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var fi = new FileInfo(f);
                    var last = fi.LastWriteTimeUtc; // <-- use filesystem time only

                    if (last < cutoffUtc)
                    {
                        File.Delete(f);
                    }
                }
                catch { /* ignore per-file errors */ }
            }
        }
        catch { /* ignore */ }
    }

    // ---------------- Hint helpers ----------------

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

    private static bool TryGetValidHint(
        Dictionary<string, ReadHint> dict, string basename, out ReadHint hint)
    {
        hint = default;
        if (!dict.TryGetValue(basename, out var h)) return false;
        if (DateTimeOffset.Now - h.When > TimeSpan.FromSeconds(5)) return false; // recent only
        hint = h;
        return true;
    }

    private bool TryFindHintAnyPid(string basename, out ReadHint hint, string preferredUser = "")
    {
        hint = default;
        var bestWhen = DateTimeOffset.MinValue;

        foreach (var pidMap in _readHints.Values)
        {
            if (!pidMap.TryGetValue(basename, out var h)) continue;
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

    // ---------------- Types ----------------

    private readonly struct ReadHint
    {
        public DateTimeOffset When { get; }
        public string Path { get; }
        public string User { get; }

        public ReadHint(DateTimeOffset when, string path, string user)
        {
            When = when;
            Path = path;
            User = user;
        }
    }

    private sealed class TransferSession
    {
        public DateTimeOffset Started { get; }
        public DateTimeOffset LastActivity { get; private set; }
        public string User { get; }
        public DeviceInfo Device { get; }

        public long UsbWriteBytes { get; private set; }

        public List<string> UsbFiles { get; } = new();

        private readonly List<(string Dest, string Src)> _pairs = new();

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

        public void AddUsbWrite(string path, long bytes)
        {
            UsbFiles.Add(path);
            UsbWriteBytes += bytes;
        }

        public void AddPair(string dest, string src) => _pairs.Add((dest, src));
        public List<string> MatchedDestFiles() => _pairs.Select(p => p.Dest).ToList();
        public List<string> MatchedSourceFiles() => _pairs.Select(p => p.Src).ToList();

        public bool EnsureAttributionStarted(PipeServer pipe, string? suggestedName)
        {
            lock (_attrLock)
            {
                if (_attrTask != null) return false; // already started
                _attrTask = Task.Run(async () =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromHours(1));
                        var answer = await pipe.RequestAttributionAsync(suggestedName, cts.Token);
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
