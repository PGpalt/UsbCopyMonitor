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
    private readonly ILogger<SessionManager> _log;

    private readonly ConcurrentDictionary<(int pid, string devId), TransferSession> _open = new();

    // Read hints: only from PC (non-USB) to infer SourcePath for PC → USB copies.
    private readonly ConcurrentDictionary<int, Dictionary<string, ReadHint>> _readHints = new();
    private readonly TimeSpan _hintTtl = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _idle = TimeSpan.FromSeconds(10);
    private readonly string _logDir;

    // Syslog defaults (Wazuh-friendly)
    private const string SyslogAppName = "UsbCopyMon";
    private const string SyslogMsgId = "USB_COPY";
    private const int SyslogEnterpriseId = 32473; // any stable number you own/use
    private const int SyslogFacilityLocal0 = 16;  // LOCAL0
    private const int SyslogSeverityInfo = 6;     // Informational

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
        lock (dict)
        {
            dict[basename] = hint;
        }
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
        var s = _open.GetOrAdd(key, _ => new TransferSession(pid, when, user, usbDev));
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
            catch
            {
                // ignore
            }

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

        var sourcePath = CommonDirectory(srcList); // PC
        var destPath = CommonDirectory(destList);  // USB

        var deviceName = string.IsNullOrWhiteSpace(s.Device.Label) ? s.Device.DriveLetter : s.Device.Label;

        // Helper strips ADS like ":Zone.Identifier:$DATA"
        static string FileNameSansAds(string path)
        {
            var name = Path.GetFileName(path) ?? string.Empty;
            var idx = name.IndexOf(':');
            return idx >= 0 ? name[..idx] : name;
        }

        var fileNames = destList
            .Select(FileNameSansAds)
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
            SourcePath: sourcePath,
            DestPath: destPath,
            DeviceName: deviceName,
            FileNames: fileNames,
            AttributedTo: attributedTo);

        _log.LogInformation("Session closed (PC → USB): {User} → {Device} | {Count} files",
            s.User, s.Device.Label ?? s.Device.DriveLetter, fileNames.Count);

        _log.LogDebug("  Details: {@rec}", rec);

        // Local file write (JSONL) - optional per config
        if (_opts.CurrentValue.LocalLoggingEnabled)
        {
            var json = JsonSerializer.Serialize(rec);
            var path = Path.Combine(_logDir, $"{DateTime.UtcNow:yyyyMMdd}.jsonl");
            File.AppendAllText(path, json + Environment.NewLine);
        }

        // Syslog send to Wazuh (RFC5424) - instead of JSON payload
        var udp = _opts.CurrentValue.Syslog;
        if (udp is not null && udp.Enabled)
        {
            var syslog = Syslog.BuildRfc5424(
                rec: rec,
                pid: s.Pid,
                facility: SyslogFacilityLocal0,
                severity: SyslogSeverityInfo,
                appName: SyslogAppName,
                msgId: SyslogMsgId,
                enterpriseId: SyslogEnterpriseId);

            _ = Task.Run(() => SendSyslogUdpSafe(syslog, udp.Ip, udp.Port));
        }
    }

    // ---------------- Syslog UDP helper ----------------

    private static void SendSyslogUdpSafe(string payload, string ip, int port)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ip) || port <= 0 || port > 65535) return;

            using var client = new UdpClient();
            client.Connect(IPAddress.Parse(ip), port);

            // UDP syslog: one message per datagram (no TCP framing)
            var bytes = Encoding.UTF8.GetBytes(payload);
            client.Send(bytes, bytes.Length);
        }
        catch
        {
            // best-effort
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
                    var last = fi.LastWriteTimeUtc;

                    if (last < cutoffUtc)
                        File.Delete(f);
                }
                catch
                {
                    // ignore per-file errors
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    // ---------------- Hint helpers ----------------

    private void PurgeOldHints()
    {
        foreach (var kv in _readHints.ToArray())
        {
            var dict = kv.Value;
            lock (dict)
            {
                foreach (var file in dict.Keys.ToList())
                {
                    if (DateTimeOffset.Now - dict[file].When > _hintTtl)
                        dict.Remove(file);
                }

                if (dict.Count == 0)
                    _readHints.TryRemove(kv.Key, out _);
            }
        }
    }

    private static bool TryGetValidHint(
        Dictionary<string, ReadHint> dict, string basename, out ReadHint hint)
    {
        hint = default;
        lock (dict)
        {
            if (!dict.TryGetValue(basename, out var h)) return false;
            if (DateTimeOffset.Now - h.When > TimeSpan.FromSeconds(5)) return false; // recent only
            hint = h;
            return true;
        }
    }

    private bool TryFindHintAnyPid(string basename, out ReadHint hint, string preferredUser = "")
    {
        hint = default;
        var bestWhen = DateTimeOffset.MinValue;

        foreach (var kv in _readHints.ToArray())
        {
            var pidMap = kv.Value;

            ReadHint h;
            lock (pidMap)
            {
                if (!pidMap.TryGetValue(basename, out h))
                    continue;
            }

            if (DateTimeOffset.Now - h.When > _hintTtl) continue;

            var scoreBias = (!string.IsNullOrEmpty(preferredUser) &&
                             h.User.Equals(preferredUser, StringComparison.OrdinalIgnoreCase))
                ? TimeSpan.FromSeconds(5)
                : TimeSpan.Zero;

            var effectiveWhen = h.When + scoreBias;
            if (effectiveWhen > bestWhen)
            {
                hint = h;
                bestWhen = effectiveWhen;
            }
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
        public int Pid { get; }
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

        public TransferSession(int pid, DateTime started, string user, DeviceInfo device)
        {
            Pid = pid;
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

    // ---------------- Syslog formatter (RFC5424) ----------------

    private static class Syslog
    {
        public static string BuildRfc5424(
            CopyLog rec,
            int pid,
            int facility,
            int severity,
            string appName,
            string msgId,
            int enterpriseId)
        {
            // PRI = facility*8 + severity
            var pri = (facility * 8) + severity;

            // VERSION
            const int version = 1;

            // TIMESTAMP (RFC3339)
            var ts = rec.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");

            // HOSTNAME
            var hostname = string.IsNullOrWhiteSpace(rec.Computer) ? Environment.MachineName : rec.Computer;

            // PROCID
            var procId = pid > 0 ? pid.ToString() : "-";

            // Structured Data
            var sd = BuildStructuredData(rec, enterpriseId);

            // MSG (short human-readable)
            var msg = BuildMsg(rec);

            // <PRI>VERSION TIMESTAMP HOSTNAME APP-NAME PROCID MSGID STRUCTURED-DATA MSG
            return $"<{pri}>{version} {SanitizeToken(ts)} {SanitizeToken(hostname)} {SanitizeToken(appName)} {SanitizeToken(procId)} {SanitizeToken(msgId)} {sd} {msg}";
        }

        private static string BuildStructuredData(CopyLog rec, int enterpriseId)
        {
            var sdId = $"usbcopymon@{enterpriseId}";

            // Keep file list bounded
            var files = rec.FileNames is null ? "" : string.Join(",", rec.FileNames);
            files = Trunc(files, 512);

            var sb = new StringBuilder();
            sb.Append('[').Append(sdId);

            AppendSd(sb, "user", rec.User);
            AppendSd(sb, "attributedTo", rec.AttributedTo);
            AppendSd(sb, "device", rec.DeviceName);
            AppendSd(sb, "src", rec.SourcePath);
            AppendSd(sb, "dst", rec.DestPath);
            AppendSd(sb, "files", files);

            sb.Append(']');
            return sb.ToString();
        }

        private static string BuildMsg(CopyLog rec)
        {
            var count = rec.FileNames?.Count ?? 0;
            var src = string.IsNullOrWhiteSpace(rec.SourcePath) ? "-" : rec.SourcePath;
            var dst = string.IsNullOrWhiteSpace(rec.DestPath) ? "-" : rec.DestPath;

            var msg = $"PC->USB user={rec.User} attributedTo={rec.AttributedTo} device=\"{rec.DeviceName}\" src=\"{src}\" dst=\"{dst}\" files={count}";
            return SanitizeMsg(msg);
        }

        private static void AppendSd(StringBuilder sb, string key, string? value)
        {
            value ??= "";
            sb.Append(' ')
              .Append(key)
              .Append("=\"")
              .Append(EscapeSdParam(Trunc(value, 256)))
              .Append('"');
        }

        // RFC5424 SD-PARAM escaping: \, ", ] must be escaped with backslash
        private static string EscapeSdParam(string s) =>
            s.Replace(@"\", @"\\").Replace("\"", "\\\"").Replace("]", "\\]");

        // Header tokens must not contain spaces; "-" used for NILVALUE
        private static string SanitizeToken(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "-";
            var t = s.Trim();
            t = t.Replace(" ", "_");
            return t.Length == 0 ? "-" : t;
        }

        // Avoid newlines for receivers/parsers
        private static string SanitizeMsg(string s) =>
            s.Replace("\r", " ").Replace("\n", " ");

        private static string Trunc(string s, int max) =>
            s.Length <= max ? s : s[..max];
    }
}