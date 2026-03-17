using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace UsbCopyMon.Service;

public sealed class FileMonitor
{
    private TraceEventSession? _session;
    private readonly DeviceMap _devices;
    private readonly SessionManager _sessions;

    // Track ETW processing task so we can see if it died silently.
    private Task? _processingTask;

    // Map FileObject -> last known path (helps when FileIORead/FileIOWrite has empty FileName)
    private readonly ConcurrentDictionary<ulong, NameEntry> _nameByFileObject = new();
    private readonly TimeSpan _nameTtl = TimeSpan.FromMinutes(5);
    private long _lastNamePurgeTick = 0;

    public FileMonitor(DeviceMap devices, SessionManager sessions)
    {
        _devices = devices;
        _sessions = sessions;
    }

    public void Start()
    {
        if (TraceEventSession.IsElevated() != true)
            throw new InvalidOperationException("Run service elevated to enable kernel ETW.");

        _session = new TraceEventSession("UsbCopyMon-Session") { StopOnDispose = true };

        var kws = KernelTraceEventParser.Keywords.FileIOInit
                | KernelTraceEventParser.Keywords.FileIO;

        _session.EnableKernelProvider(kws);

        // Capture CREATE to learn names reliably (especially for network redirector).
        _session.Source.Kernel.FileIOCreate += d =>
        {
            try { OnCreate(d.FileName, (ulong)d.FileObject, d.ProcessID, d.TimeStamp); }
            catch { /* never let handler exceptions kill ETW processing */ }
        };

        // Cleanup map on CLOSE (best-effort).
        _session.Source.Kernel.FileIOClose += d =>
        {
            try { _nameByFileObject.TryRemove((ulong)d.FileObject, out _); }
            catch { }
        };

        // Read hints: non-USB reads used to infer SourcePath for PC→USB copies.
        _session.Source.Kernel.FileIORead += d =>
        {
            try { OnRead(d.FileName, (ulong)d.FileObject, d.ProcessID, d.TimeStamp); }
            catch { }
        };

        // Only writes that go TO USB will be recorded (PC → USB).
        _session.Source.Kernel.FileIOWrite += d =>
        {
            try { OnWrite(d.FileName, (ulong)d.FileObject, d.ProcessID, d.TimeStamp, (long)d.IoSize); }
            catch { }
        };

        // Run processing on background thread; keep a reference so we can detect termination.
        _processingTask = Task.Run(() =>
        {
            try { _session.Source.Process(); }
            catch
            {
                // If you have ILogger here, log. Without it, at least prevent silent swallow at the call site.
                throw;
            }
        });

        // Observe faults so they don't get dropped silently by the runtime.
        _processingTask.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    public void Stop()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;

        // Processing task will end when session is disposed.
        _processingTask = null;
    }

    private static bool IsServiceLikeUser(string? user)
    {
        if (string.IsNullOrWhiteSpace(user)) return true;

        return user.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase)
            || user.Equals(@"NT AUTHORITY\LOCAL SERVICE", StringComparison.OrdinalIgnoreCase)
            || user.Equals(@"NT AUTHORITY\NETWORK SERVICE", StringComparison.OrdinalIgnoreCase)
            || user.StartsWith(@"NT AUTHORITY\", StringComparison.OrdinalIgnoreCase)
            || user.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase);
    }

    // -------------------- Event handlers --------------------

    private void OnCreate(string? rawPath, ulong fileObject, int pid, DateTime when)
    {
        MaybePurgeNameCache();

        var path = NormalizeToFriendlyPath(rawPath);
        if (string.IsNullOrWhiteSpace(path)) return;
        if (fileObject == 0) return;

        _nameByFileObject[fileObject] = new NameEntry(new DateTimeOffset(when), path);
    }

    private void OnRead(string? rawPath, ulong fileObject, int pid, DateTime when)
    {
        MaybePurgeNameCache();

        var path = ResolvePath(rawPath, fileObject);
        if (string.IsNullOrWhiteSpace(path)) return;

        // Keep READ hints if they are from the PC side (i.e., NOT from USB).
        // Network shares count as PC-side for this purpose.
        var isUsb = _devices.ResolveForPath(path) is not null;
        if (isUsb) return;

        var user = GetProcessUser(pid) ?? string.Empty;
        if (IsServiceLikeUser(user)) return;

        _sessions.HintRead(pid, when, path, user);
    }

    private void OnWrite(string? rawPath, ulong fileObject, int pid, DateTime when, long bytes)
    {
        MaybePurgeNameCache();

        var path = ResolvePath(rawPath, fileObject);
        if (string.IsNullOrWhiteSpace(path)) return;

        // Only proceed if destination of the write is a USB drive (PC → USB).
        var isUsbDest = _devices.ResolveForPath(path) is not null;
        if (!isUsbDest) return;

        var user = GetProcessUser(pid);
        _sessions.RecordUsbDestinationWrite(pid, when, path, bytes, user);
    }

    // -------------------- Name cache helpers --------------------

    private string? ResolvePath(string? rawPath, ulong fileObject)
    {
        // Prefer the direct name if present
        var p = NormalizeToFriendlyPath(rawPath);
        if (!string.IsNullOrWhiteSpace(p)) return p;

        if (fileObject == 0) return null;

        if (_nameByFileObject.TryGetValue(fileObject, out var entry))
        {
            if (DateTimeOffset.Now - entry.When <= _nameTtl)
                return entry.Path;

            _nameByFileObject.TryRemove(fileObject, out _);
        }

        return null;
    }

    private void MaybePurgeNameCache()
    {
        // cheap throttle: purge at most every ~30 seconds
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastNamePurgeTick);
        if (now - last < 30_000) return;
        if (Interlocked.CompareExchange(ref _lastNamePurgeTick, now, last) != last) return;

        foreach (var kv in _nameByFileObject.ToArray())
        {
            if (DateTimeOffset.Now - kv.Value.When > _nameTtl)
                _nameByFileObject.TryRemove(kv.Key, out _);
        }
    }

    private readonly struct NameEntry
    {
        public DateTimeOffset When { get; }
        public string Path { get; }

        public NameEntry(DateTimeOffset when, string path)
        {
            When = when;
            Path = path;
        }
    }

    // -------------------- Helpers --------------------

    private static string? GetProcessUser(int pid)
    {
        IntPtr hProcess = IntPtr.Zero, hToken = IntPtr.Zero;
        try
        {
            hProcess = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)pid);
            if (hProcess == IntPtr.Zero)
                hProcess = OpenProcess(0x0400 /* PROCESS_QUERY_INFORMATION */, false, (uint)pid);
            if (hProcess == IntPtr.Zero) return null;

            if (!OpenProcessToken(hProcess, 0x0008 /* TOKEN_QUERY */, out hToken) || hToken == IntPtr.Zero)
                return null;

            using var wi = new WindowsIdentity(hToken);
            return wi.Name;
        }
        catch { return null; }
        finally
        {
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
        }
    }

    // NT/extended → friendly path normalization (DOS drive OR UNC)
    private static string NormalizeToFriendlyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        // Strip \\?\ and \\?\UNC\
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                path = @"\\" + path[8..];
            else
                path = path[4..];
        }

        // Strip \??\ prefix sometimes seen in NT paths
        if (path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
            path = path[4..];

        // Network redirector (most common)
        // \Device\Mup\server\share\dir\file -> \\server\share\dir\file
        if (path.StartsWith(@"\Device\Mup\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path.Substring(@"\Device\Mup\".Length).TrimStart('\\');

        // DFS client redirector (mapped drives on DFS namespaces can look like this):
        // \Device\DfsClient\;X:0000000000191a10\dir\file -> X:\dir\file
        // Sometimes the kernel path prefix can be trimmed and show up as: DfsClient\;X:<hex>\dir\file
        if (path.StartsWith(@"\Device\DfsClient\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"DfsClient\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\DfsClient\", StringComparison.OrdinalIgnoreCase))
        {
            // Expected: \Device\DfsClient\;X:<hex>\rest\of\path
            var semi = path.IndexOf(';');
            if (semi >= 0 && semi + 2 < path.Length)
            {
                var drive = path.Substring(semi + 1, 2); // "X:"
                if (char.IsLetter(drive[0]) && drive[1] == ':')
                {
                    // Skip the hex blob after ";X:" up to the next '\'
                    var afterDrive = semi + 3; // position after ";X:"
                    var nextSlash = path.IndexOf('\\', afterDrive);
                    if (nextSlash >= 0)
                    {
                        var rest = path.Substring(nextSlash); // starts with '\'
                        return drive + rest;                  // "X:\..."
                    }

                    // Fallback if no trailing path
                    return drive + @"\";
                }
            }

            // If parsing fails, fall through to return the original path.
        }

        // Another common redirector form:
        // \Device\LanmanRedirector\;Z:0000000000000000\server\share\dir\file -> \\server\share\dir\file
        if (path.StartsWith(@"\Device\LanmanRedirector\", StringComparison.OrdinalIgnoreCase))
        {
            var rest = path.Substring(@"\Device\LanmanRedirector\".Length);

            if (rest.StartsWith(";", StringComparison.Ordinal))
            {
                var idx = rest.IndexOf('\\');
                if (idx >= 0 && idx < rest.Length - 1)
                    rest = rest[(idx + 1)..];
            }

            return @"\\" + rest.TrimStart('\\');
        }

        // Map \Device\HarddiskVolumeX\... -> C:\...
        if (path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var drive in Environment.GetLogicalDrives())
            {
                var sb = new StringBuilder(1024);
                var rc = QueryDosDevice(drive.TrimEnd('\\'), sb, sb.Capacity);
                if (rc == 0) continue;

                var dev = sb.ToString();
                var nul = dev.IndexOf('\0');
                if (nul >= 0) dev = dev[..nul];

                if (!string.IsNullOrEmpty(dev) &&
                    path.StartsWith(dev, StringComparison.OrdinalIgnoreCase))
                {
                    return drive.TrimEnd('\\') + path.Substring(dev.Length);
                }
            }
        }

        return path;
    }

    // -------------------- P/Invoke --------------------

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, int DesiredAccess, out IntPtr TokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);
}