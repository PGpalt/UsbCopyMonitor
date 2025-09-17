namespace UsbCopyMon.Shared
{
    public record DeviceInfo(
        string DriveLetter,
        string Label,
        string? Vid,
        string? Pid,
        string? Serial,
        string PnpId
    );

    public record CopyLog(
        DateTimeOffset Timestamp,
        string Computer,
        string User,
        string SourcePath,
        string DestPath,
        string DeviceName,
        IReadOnlyList<string> FileNames
    );
}
