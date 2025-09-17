using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using UsbCopyMon.Shared;

namespace UsbCopyMon.Tray
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += (_, __) => StartPipeListener();
        }

        private async void StartPipeListener()
        {
            while (true)
            {
                try
                {
                    // Build ACL that allows LocalSystem + current user
                    var ps = new PipeSecurity();
                    ps.AddAccessRule(new PipeAccessRule(
                        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                        PipeAccessRights.FullControl, AccessControlType.Allow));
                    ps.AddAccessRule(new PipeAccessRule(
                        WindowsIdentity.GetCurrent().User!,
                        PipeAccessRights.FullControl, AccessControlType.Allow));
                    // Optional: let any authenticated user read/write
                    ps.AddAccessRule(new PipeAccessRule(
                        new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                        PipeAccessRights.ReadWrite, AccessControlType.Allow));

                    // Use the ACL-aware factory (requires System.IO.Pipes.AccessControl)
                    using var server = NamedPipeServerStreamAcl.Create(
                        pipeName: PipeNames.ServiceToTray,
                        direction: PipeDirection.InOut,
                        maxNumberOfServerInstances: 5,
                        transmissionMode: PipeTransmissionMode.Byte,
                        options: PipeOptions.Asynchronous,
                        inBufferSize: 0,
                        outBufferSize: 0,
                        pipeSecurity: ps);

                    await server.WaitForConnectionAsync();

                    var req = await JsonSerializer.DeserializeAsync<PromptRequest>(server);
                    if (req != null)
                    {
                        var dlg = new PromptWindow(req) { Owner = this };
                        dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                        dlg.ShowDialog();

                        // Send a response back (even if user canceled)
                        var resp = dlg.Result ?? new PromptResponse(req.SessionId, null, null, System.DateTimeOffset.Now);
                        await JsonSerializer.SerializeAsync(server, resp);
                        await server.FlushAsync();
                    }
                    // Loop to accept next connection
                }
                catch
                {
                    await Task.Delay(300);
                }
            }
        }
    }
}
