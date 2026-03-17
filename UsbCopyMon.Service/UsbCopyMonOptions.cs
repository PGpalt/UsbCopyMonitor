namespace UsbCopyMon.Service;

public sealed class UsbCopyMonOptions
{
    public bool PromptEnabled { get; set; } = true;
    public bool LocalLoggingEnabled { get; set; } = true;
    public UdpOptions Syslog { get; set; } = new();
    public int RetentionDays { get; set; } = 8; // keep last 7 days by default
}

public sealed class UdpOptions
{
    public bool Enabled { get; set; }
    public string Ip { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 514;
}
