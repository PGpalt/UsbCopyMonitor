using System.Management;
using UsbCopyMon.Shared;

namespace UsbCopyMon.Service;

public sealed class DeviceMap
{
    private readonly object _gate = new();
    private Dictionary<string, DeviceInfo> _map = new(StringComparer.OrdinalIgnoreCase);
    private ManagementEventWatcher? _watcher;

    public void Start()
    {
        Refresh();
        try
        {
            _watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent"));
            _watcher.EventArrived += (_, __) => Refresh();
            _watcher.Start();
        }
        catch { /* best-effort */ }
    }

    public void Stop()
    {
        try { _watcher?.Stop(); } catch { }
    }

    /// Given a full path (or "E:\"), returns DeviceInfo for a USB-backed drive.
    public DeviceInfo? ResolveForPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return null;
        lock (_gate) return _map.TryGetValue(root, out var di) ? di : null;
    }

    private void Refresh()
    {
        var next = new Dictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;

                var root = d.RootDirectory.FullName;   // e.g. "E:\"
                var label = d.VolumeLabel ?? string.Empty;

                var assoc = GetDiskAssocForDriveLetter(root);
                if (assoc is null) continue;

                var (pnpId, iface) = assoc.Value;
                var isUsb = string.Equals(iface, "USB", StringComparison.OrdinalIgnoreCase)
                         || (pnpId?.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ?? false)
                         || (pnpId?.Contains("USBSTOR", StringComparison.OrdinalIgnoreCase) ?? false);
                if (!isUsb) continue;

                var (vid, pid, serial) = ParseVidPidSerial(pnpId ?? string.Empty);

                next[root] = new DeviceInfo(
                    DriveLetter: root,
                    Label: label,
                    Vid: vid,
                    Pid: pid,
                    Serial: serial,
                    PnpId: pnpId ?? string.Empty
                );
            }
            catch { /* ignore this drive */ }
        }

        lock (_gate) _map = next;
    }

    private static (string? pnp, string? iface)? GetDiskAssocForDriveLetter(string driveRoot)
    {
        var driveId = (driveRoot ?? "").TrimEnd('\\'); // "E:"
        try
        {
            using var q1 = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{driveId}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
            foreach (ManagementObject partition in q1.Get())
            {
                var partDeviceId = partition["DeviceID"]?.ToString();
                if (string.IsNullOrEmpty(partDeviceId)) continue;

                using var q2 = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partDeviceId}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                foreach (ManagementObject disk in q2.Get())
                {
                    var pnp = disk["PNPDeviceID"]?.ToString();
                    var iface = disk["InterfaceType"]?.ToString();
                    return (pnp, iface);
                }
            }
        }
        catch { }
        return null;
    }

    private static (string? vid, string? pid, string? serial) ParseVidPidSerial(string? pnp)
    {
        if (string.IsNullOrEmpty(pnp)) return (null, null, null);

        string? vid = null, pid = null, serial = null;
        foreach (var part in pnp.Split('\\', '&'))
        {
            if (part.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)) vid = part[4..];
            else if (part.StartsWith("PID_", StringComparison.OrdinalIgnoreCase)) pid = part[4..];
        }
        var idx = pnp.LastIndexOf('\\');
        if (idx >= 0 && idx < pnp.Length - 1) serial = pnp[(idx + 1)..];
        return (vid, pid, serial);
    }
}
