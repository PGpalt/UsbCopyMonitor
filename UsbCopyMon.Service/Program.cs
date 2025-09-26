using Microsoft.Extensions.Logging.EventLog;

namespace UsbCopyMon.Service;

public class Program
{
    public static async Task Main(string[] args)
    {
        await Host.CreateDefaultBuilder(args)
            .UseWindowsService(o => o.ServiceName = "UsbCopyMon Service")
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                var basePath = AppContext.BaseDirectory;       // <— the folder where the service EXE lives
                cfg.SetBasePath(basePath);
                cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                cfg.AddEnvironmentVariables();
            })
            .ConfigureLogging(b =>
            {
                b.ClearProviders();
                b.AddSimpleConsole();
                b.SetMinimumLevel(LogLevel.Information);
                b.AddEventLog(new EventLogSettings
                {
                    SourceName = "UsbCopyMon Service", // shows as Provider name in Event Viewer
                    LogName = "Application"
                });
            })
            .ConfigureServices((ctx, services) =>
            {
                services.Configure<UsbCopyMonOptions>(ctx.Configuration.GetSection("UsbCopyMon"));
                services.AddSingleton<DeviceMap>();
                services.AddSingleton<PipeServer>();
                services.AddSingleton<SessionManager>();
                services.AddSingleton<FileMonitor>();
                services.AddHostedService<Worker>();
            })
            .Build()
            .RunAsync();
    }
}
