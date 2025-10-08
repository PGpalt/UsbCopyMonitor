namespace UsbCopyMon.Service;

public sealed class Worker : BackgroundService
{
    private readonly FileMonitor _monitor;
    private readonly SessionManager _sessions;
    private readonly DeviceMap _devices;
    private readonly ILogger<Worker> _log;

    public Worker(FileMonitor monitor, SessionManager sessions, DeviceMap devices, ILogger<Worker> log)
    {
        _monitor = monitor;
        _sessions = sessions;
        _devices = devices;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const int maxBackoffSeconds = 30;
        var backoff = TimeSpan.FromSeconds(2);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // (Re)start components that can be safely started multiple times.
                _devices.Start();
                _monitor.Start();

                _log.LogInformation("Worker loop started.");

                // Run the session manager loop until cancelled or faulted.
                await _sessions.RunAsync(stoppingToken);

                // If RunAsync returns normally, exit the while (service stopping).
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Worker crashed; restarting after {Backoff}s", backoff.TotalSeconds);
            }
            finally
            {
                try { _monitor.Stop(); } catch { }
                try { _devices.Stop(); } catch { }
            }

            // Backoff before trying again
            try
            {
                await Task.Delay(backoff, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            // Exponential-ish backoff (cap at 30s)
            var next = TimeSpan.FromSeconds(Math.Min(maxBackoffSeconds, backoff.TotalSeconds * 2));
            backoff = next;
        }

        _log.LogInformation("Worker stopped.");
    }
}
