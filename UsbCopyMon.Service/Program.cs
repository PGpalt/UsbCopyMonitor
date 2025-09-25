namespace UsbCopyMon.Service;

public class Program
{
    public static async Task Main(string[] args)
    {
        await Host.CreateDefaultBuilder(args)
            .UseWindowsService(o => o.ServiceName = "UsbCopyMon Service")
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                // Loads: appsettings.json (+ appsettings.Production.json) with reload
                cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                // cfg.AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json", true, true);
            })
            .ConfigureLogging(b =>
            {
                b.ClearProviders();
                b.AddSimpleConsole();
                b.SetMinimumLevel(LogLevel.Information);
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
