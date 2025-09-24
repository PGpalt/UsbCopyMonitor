using Microsoft.Extensions.Logging;
using System.IO.Pipes;
using System.Text;
using UsbCopyMon.Shared;

namespace UsbCopyMon.Service;

public sealed class PipeServer // acts as a client to the tray
{
    private const int ConnectTimeoutMs = 3600000;    // 1h max wait for tray
    private const int RoundtripTimeoutMs = 3600000;  // 1h for prompt/answer

    private readonly ILogger<PipeServer> _log;

    public PipeServer(ILogger<PipeServer> log) => _log = log;

    public async Task<string?> RequestAttributionAsync(string? suggestedName, CancellationToken ct = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                serverName: ".",
                pipeName: PipeNames.ServiceToTray,
                direction: PipeDirection.InOut,
                options: PipeOptions.Asynchronous);

            // CONNECT with timeout
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(ConnectTimeoutMs);
                await client.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            }

            client.ReadMode = PipeTransmissionMode.Byte;

            // I/O ROUNDTRIP with timeout
            using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ioCts.CancelAfter(RoundtripTimeoutMs);

            // ---- Write request: length-prefixed UTF-8 suggested name ----
            var reqBytes = Encoding.UTF8.GetBytes(suggestedName ?? string.Empty);
            var reqLen = BitConverter.GetBytes(reqBytes.Length);
            await client.WriteAsync(reqLen, 0, reqLen.Length, ioCts.Token).ConfigureAwait(false);
            if (reqBytes.Length > 0)
                await client.WriteAsync(reqBytes, 0, reqBytes.Length, ioCts.Token).ConfigureAwait(false);
            await client.FlushAsync(ioCts.Token).ConfigureAwait(false);

            // ---- Read response: length-prefixed UTF-8 attributedTo ----
            var hdr = new byte[4];
            await ReadExactAsync(client, hdr, 0, 4, ioCts.Token).ConfigureAwait(false);
            var respLen = BitConverter.ToInt32(hdr, 0);
            if (respLen < 0 || respLen > 1_048_576) // 1MB guard
                throw new InvalidDataException($"Invalid response length {respLen}.");

            var buf = new byte[respLen];
            if (respLen > 0)
                await ReadExactAsync(client, buf, 0, respLen, ioCts.Token).ConfigureAwait(false);

            var text = Encoding.UTF8.GetString(buf);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Tray request timed out (connect={connect}ms, io={io}ms).",
                ConnectTimeoutMs, RoundtripTimeoutMs);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not contact tray or roundtrip failed.");
            return null;
        }
    }

    private static async Task ReadExactAsync(Stream s, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await s.ReadAsync(buffer.AsMemory(offset + read, count - read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }
}
