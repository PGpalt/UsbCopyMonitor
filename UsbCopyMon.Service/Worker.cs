namespace UsbCopyMon.Service
{
    public sealed class Worker : BackgroundService
    {
        private readonly FileMonitor _monitor;
        private readonly SessionManager _sessions;
        private readonly DeviceMap _devices;

        public Worker(FileMonitor monitor, SessionManager sessions, DeviceMap devices)
        {
            _monitor = monitor;
            _sessions = sessions;
            _devices = devices;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _devices.Start();
            _monitor.Start();
            await _sessions.RunAsync(stoppingToken);
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _monitor.Stop();
            _devices.Stop();
            return base.StopAsync(cancellationToken);
        }
    }
}
