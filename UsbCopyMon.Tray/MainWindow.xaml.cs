using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using UsbCopyMon.Shared;

namespace UsbCopyMon.Tray;

public sealed class TrayHost : IDisposable
{
    private System.Windows.Forms.NotifyIcon? _ni;
    private CancellationTokenSource _cts = new();
    private Task? _listenTask;

    public void Start()
    {
        _ni = new System.Windows.Forms.NotifyIcon
        {
            Text = "UsbCopyMon",
            Visible = true,
            Icon = System.Drawing.SystemIcons.Information
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var exit = new System.Windows.Forms.ToolStripMenuItem("Exit");
        exit.Click += (_, __) => Application.Current.Shutdown();
        menu.Items.Add(exit);
        _ni.ContextMenuStrip = menu;

        _listenTask = Task.Run(ListenLoop);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listenTask?.Wait(1000); } catch { }
        if (_ni is not null) { _ni.Visible = false; _ni.Dispose(); }
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServer();
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                _ = HandleOneAsync(server); // fire & forget
            }
            catch (OperationCanceledException) { server?.Dispose(); break; }
            catch { server?.Dispose(); await Task.Delay(500); }
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        var ps = BuildPipeSecurity();

        return NamedPipeServerStreamAcl.Create(
            pipeName: PipeNames.ServiceToTray,
            direction: PipeDirection.InOut,
            maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: ps
        );
    }

    private static PipeSecurity BuildPipeSecurity()
    {
        var ps = new PipeSecurity();

        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));

        ps.SetOwner(WindowsIdentity.GetCurrent().User!);
        return ps;
    }

    private async Task HandleOneAsync(NamedPipeServerStream s)
    {
        try
        {
            // ---- Read request: length-prefixed UTF-8 suggestedName ----
            var hdr = new byte[4];
            await ReadExactAsync(s, hdr, 0, 4, _cts.Token).ConfigureAwait(false);
            var reqLen = BitConverter.ToInt32(hdr, 0);
            if (reqLen < 0 || reqLen > 1_048_576) throw new InvalidDataException("Invalid request length.");

            string? suggestedName = null;
            if (reqLen > 0)
            {
                var buf = new byte[reqLen];
                await ReadExactAsync(s, buf, 0, reqLen, _cts.Token).ConfigureAwait(false);
                suggestedName = Encoding.UTF8.GetString(buf);
            }

            // Prompt with the suggested name
            var answer = await Application.Current.Dispatcher.InvokeAsync<string?>(() =>
            {
                var dlg = new PromptWindow(suggestedName);
                var ok = dlg.ShowDialog() == true;
                return ok ? dlg.Answer : null;
            });

            // ---- Write response: length-prefixed UTF-8 attributedTo ----
            var payload = Encoding.UTF8.GetBytes(answer ?? string.Empty);
            var len = BitConverter.GetBytes(payload.Length);

            await s.WriteAsync(len, 0, len.Length, _cts.Token).ConfigureAwait(false);
            if (payload.Length > 0)
                await s.WriteAsync(payload, 0, payload.Length, _cts.Token).ConfigureAwait(false);
            await s.FlushAsync(_cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // swallow per-connection errors; keep server alive
        }
        finally
        {
            try { s.Dispose(); } catch { }
        }
    }

    private static async Task ReadExactAsync(Stream s, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int readTotal = 0;
        while (readTotal < count)
        {
            int n = await s.ReadAsync(buffer.AsMemory(offset + readTotal, count - readTotal), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            readTotal += n;
        }
    }
}
