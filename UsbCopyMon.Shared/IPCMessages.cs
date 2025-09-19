namespace UsbCopyMon.Shared
{
    public static class PipeNames
    {
        public const string ServiceToTray = "UsbCopyMon.ServiceToTray";
    }

    public record DeviceInfo(
        string DriveLetter,
        string Label,
        string? Vid,
        string? Pid,
        string? Serial,
        string PnpId
    );

    // Add an optional field so we can persist the answer in the same JSON record
    public record CopyLog(
        DateTimeOffset Timestamp,
        string Computer,
        string User,
        string SourcePath,
        string DestPath,
        string DeviceName,
        IReadOnlyList<string> FileNames,
        string? AttributedTo = null       // <--- NEW (optional)
    );

    // What the tray sends back
    public record CopyAttribution(string AttributedTo);
}
