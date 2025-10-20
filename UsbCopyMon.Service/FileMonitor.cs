using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace UsbCopyMon.Service;

public sealed class FileMonitor
{
    private TraceEventSession? _session;
    private readonly DeviceMap _devices;
    private readonly SessionManager _sessions;

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

        // Keep minimal reads ONLY to learn the PC-side source folder (ignore reads from USB).
        _session.Source.Kernel.FileIORead += d => OnRead(d.FileName, d.ProcessID, d.TimeStamp);

        // Only writes that go TO USB will be recorded (PC → USB).
        _session.Source.Kernel.FileIOWrite += d => OnWrite(d.FileName, d.ProcessID, d.TimeStamp, (long)d.IoSize);

        _ = Task.Run(() => _session.Source.Process());
    }

    public void Stop()
    {
        try { _session?.Dispose(); } catch { }
    }

    // -------------------- Event handlers --------------------

    private void OnRead(string? rawPath, int pid, DateTime when)
    {
        var path = NormalizeToDosPath(rawPath);
        if (string.IsNullOrWhiteSpace(path)) return;

        // We only keep READ hints if they are from the PC (non-USB).
        var isUsb = _devices.ResolveForPath(path) is not null;
        if (isUsb) return;

        var user = GetProcessUser(pid) ?? string.Empty;
        _sessions.HintRead(pid, when, path, user);
    }

    private void OnWrite(string? rawPath, int pid, DateTime when, long bytes)
    {
        var path = NormalizeToDosPath(rawPath);
        if (string.IsNullOrWhiteSpace(path)) return;

        // Only proceed if the destination of the write is a USB drive (PC → USB).
        var isUsbDest = _devices.ResolveForPath(path) is not null;
        if (!isUsbDest) return;

        var user = GetProcessUser(pid);
        _sessions.RecordUsbDestinationWrite(pid, when, path, bytes, user);
    }

    // -------------------- Helpers --------------------

    private static string? GetProcessUser(int pid)
    {
        IntPtr hProcess = IntPtr.Zero, hToken = IntPtr.Zero;
        try
        {
            // Try limited query first, then full
            hProcess = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)pid);
            if (hProcess == IntPtr.Zero)
                hProcess = OpenProcess(0x0400 /* PROCESS_QUERY_INFORMATION */, false, (uint)pid);
            if (hProcess == IntPtr.Zero) return null;

            if (!OpenProcessToken(hProcess, 0x0008 /* TOKEN_QUERY */, out hToken) || hToken == IntPtr.Zero)
                return null;

            using var wi = new WindowsIdentity(hToken);
            return wi.Name;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
        }
    }

    // NT/extended → DOS path normalization
    private static string NormalizeToDosPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path ?? string.Empty;

        // Strip \\?\ and \\?\UNC\
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                path = @"\\" + path[8..];
            else
                path = path[4..];
        }

        // Map \Device\HarddiskVolumeX\... -> E:\...
        if (path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var drive in Environment.GetLogicalDrives())
            {
                var buf = new string('\0', 1024);
                if (QueryDosDevice(drive.TrimEnd('\\'), buf, buf.Length) != 0)
                {
                    var dev = buf[..buf.IndexOf('\0')];
                    if (!string.IsNullOrEmpty(dev) &&
                        path.StartsWith(dev, StringComparison.OrdinalIgnoreCase))
                    {
                        return drive.TrimEnd('\\') + path.Substring(dev.Length);
                    }
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
    private static extern int QueryDosDevice(string lpDeviceName, string lpTargetPath, int ucchMax);
}
