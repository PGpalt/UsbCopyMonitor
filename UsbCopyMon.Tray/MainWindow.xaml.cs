using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public void Start()
    {
        // Tray icon
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

        // Start pipe listener
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
                _ = HandleOneAsync(server);   // fire & forget per-connection
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

        // Allow LocalSystem (the service), Administrators, and Authenticated Users to read/write
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));

        // Owner = current user
        ps.SetOwner(WindowsIdentity.GetCurrent().User!);
        return ps;
    }

    private async Task HandleOneAsync(NamedPipeServerStream s)
    {
        try
        {
            var header = new byte[4];
            await ReadExactAsync(s, header, 0, 4, _cts.Token).ConfigureAwait(false);
            var len = BitConverter.ToInt32(header, 0);

            if (len <= 0 || len > (1024 * 1024))
                throw new InvalidDataException("Invalid request length.");

            var buf = new byte[len];
            await ReadExactAsync(s, buf, 0, len, _cts.Token).ConfigureAwait(false);

            var log = JsonSerializer.Deserialize<CopyLog>(buf, JsonOpts)
                      ?? throw new InvalidDataException("Bad JSON request.");

            // Minimal UI: ignore log details; use only log.User as a suggested name
            var answer = await Application.Current.Dispatcher.InvokeAsync<string?>(() =>
            {
                var dlg = new PromptWindow(log.User);
                var ok = dlg.ShowDialog() == true;
                return ok ? dlg.Answer : null;
            });

            var resp = JsonSerializer.SerializeToUtf8Bytes(new CopyAttribution(answer ?? string.Empty), JsonOpts);
            var respHdr = BitConverter.GetBytes(resp.Length);
            await s.WriteAsync(respHdr, 0, respHdr.Length, _cts.Token).ConfigureAwait(false);
            await s.WriteAsync(resp, 0, resp.Length, _cts.Token).ConfigureAwait(false);
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
