namespace UsbCopyMon.Service
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            await Host.CreateDefaultBuilder(args)
                .UseWindowsService(o => o.ServiceName = "UsbCopyMon Service")
                .ConfigureLogging(b =>
                {
                    b.ClearProviders();
                    b.AddSimpleConsole();
                    b.AddEventLog(o =>
                    {
                        o.SourceName = "UsbCopyMon.Service";
                        o.LogName = "Application";
                    });
                    b.SetMinimumLevel(LogLevel.Information);
                })
                .ConfigureServices((_, services) =>
                {
                    services.AddSingleton<DeviceMap>();
                    services.AddSingleton<PipeServer>();      // <--- NEW
                    services.AddSingleton<SessionManager>();
                    services.AddSingleton<FileMonitor>();
                    services.AddHostedService<Worker>();
                })
                .Build()
                .RunAsync();
        }
    }
}
