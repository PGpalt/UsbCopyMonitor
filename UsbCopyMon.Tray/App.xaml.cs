using System.Windows;

namespace UsbCopyMon.Tray
{
    public partial class App : Application
    {
        private TrayHost? _host;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            _host = new TrayHost();
            _host.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _host?.Dispose();
            base.OnExit(e);
        }
    }
}
