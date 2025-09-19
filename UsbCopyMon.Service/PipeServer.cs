using System.IO.Pipes;
using System.Text.Json;
using UsbCopyMon.Shared;

namespace UsbCopyMon.Service;

public sealed class PipeServer // name kept as you used before (acts as a client)
{
    private const int ConnectTimeoutMs = 4000;
    private const int RoundtripTimeoutMs = 4000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<PipeServer> _log;

    public PipeServer(ILogger<PipeServer> log)
    {
        _log = log;
    }

    public async Task<string?> RequestAttributionAsync(CopyLog log, CancellationToken ct = default)
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

            client.ReadMode = PipeTransmissionMode.Byte; // ok to keep; default is Byte anyway

            // Serialize request
            var payload = JsonSerializer.SerializeToUtf8Bytes(log, JsonOpts);

            // I/O ROUNDTRIP with timeout
            using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ioCts.CancelAfter(RoundtripTimeoutMs);

            // write length-prefixed request
            await WriteLengthPrefixedAsync(client, payload, ioCts.Token).ConfigureAwait(false);

            // read length-prefixed response
            var respBuf = await ReadLengthPrefixedAsync(client, ioCts.Token).ConfigureAwait(false);

            var resp = JsonSerializer.Deserialize<CopyAttribution>(respBuf, JsonOpts);
            return string.IsNullOrWhiteSpace(resp?.AttributedTo) ? null : resp!.AttributedTo;
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Tray request timed out (connect={connect}ms, io={io}ms).", ConnectTimeoutMs, RoundtripTimeoutMs);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not contact tray or roundtrip failed.");
            return null;
        }
    }

    private static async Task WriteLengthPrefixedAsync(Stream s, byte[] data, CancellationToken ct)
    {
        var len = BitConverter.GetBytes(data.Length);
        await s.WriteAsync(len, ct).ConfigureAwait(false);
        await s.WriteAsync(data, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadLengthPrefixedAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[4];
        await ReadExactAsync(s, header, 0, 4, ct).ConfigureAwait(false);
        var respLen = BitConverter.ToInt32(header, 0);
        if (respLen <= 0 || respLen > 1_048_576) // 1MB guard
            throw new InvalidDataException($"Invalid response length {respLen}.");
        var buf = new byte[respLen];
        await ReadExactAsync(s, buf, 0, respLen, ct).ConfigureAwait(false);
        return buf;
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