//using System.IO.Pipes;
//using System.Text.Json;
//using System.Text.Json.Serialization;
//using UsbCopyMon.Shared;

//namespace UsbCopyMon.Service;

//public sealed class PipeServer
//{
//    private readonly ILogger<PipeServer> _log;

//    private const string DefaultPipeName = PipeNames.ServiceToTray;
//    private const int ConnectTimeoutMs = 3000;
//    private const int MaxAttempts = 3;
//    private const int BackoffMs = 250;

//    private static readonly JsonSerializerOptions JsonOpts = new()
//    {
//        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
//        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
//        WriteIndented = false
//    };

//    public PipeServer(ILogger<PipeServer> log) { _log = log; }

//    public void Start() { /* no-op */ }
//    public void Stop() { /* no-op */ }

//    public Task RequestAttributionAsync(PromptRequest req, CancellationToken ct = default)
//        => RequestAttributionAsync(DefaultPipeName, req, ct);

//    public async Task RequestAttributionAsync(string pipeName, PromptRequest req, CancellationToken ct = default)
//    {
//        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
//        {
//            try
//            {
//                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
//                    PipeOptions.Asynchronous | PipeOptions.WriteThrough);

//                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
//                cts.CancelAfter(ConnectTimeoutMs);

//                await client.ConnectAsync(cts.Token).ConfigureAwait(false);
//                if (!client.CanWrite) throw new IOException("Pipe connected but not writable.");

//                await JsonSerializer.SerializeAsync(client, req, JsonOpts, ct).ConfigureAwait(false);
//                await client.FlushAsync(ct).ConfigureAwait(false);
//                return;
//            }
//            catch
//            {
//                if (attempt < MaxAttempts)
//                {
//                    try { await Task.Delay(BackoffMs, ct).ConfigureAwait(false); } catch { }
//                }
//            }
//        }
//        // Quietly skip if tray is unavailable
//    }
//}
