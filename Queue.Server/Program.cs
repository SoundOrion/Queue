using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();
Console.WriteLine("Server: listening on 0.0.0.0:5000 ...");

try
{
    var handlers = new List<Task>();

    while (!cts.IsCancellationRequested)
    {
        // 複数クライアント対応
        var client = await listener.AcceptTcpClientAsync(cts.Token);
        Console.WriteLine("Server: client connected.");
        handlers.Add(Task.Run(() => HandleClientAsync(client, cts.Token)));
    }

    await Task.WhenAll(handlers);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server: canceled.");
}
catch (Exception ex)
{
    Console.WriteLine($"Server: error {ex.Message}");
}
finally
{
    listener.Stop();
    Console.WriteLine("Server: stopped.");
}

static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
{
    client.ReceiveTimeout = 30000;
    client.SendTimeout = 30000;

    using (client)
    using (var ns = client.GetStream())
    {
        var lenBuf = new byte[4];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // メッセージ長（4バイト・Big-Endian）
                if (!await ReadExactAsync(ns, lenBuf, 4, ct))
                {
                    Console.WriteLine("Server: client closed.");
                    break;
                }

                int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
                if (len <= 0 || len > 100_000_000) // 簡易上限
                    throw new InvalidDataException($"Invalid payload length: {len}");

                var payload = new byte[len];
                if (!await ReadExactAsync(ns, payload, len, ct))
                {
                    Console.WriteLine("Server: client closed during payload.");
                    break;
                }

                // 受信payloadは「圧縮後のバイト列」(Deflate想定)
                var text = DecompressText(payload);
                Console.WriteLine($"[RECV] {text}");
            }
        }
        catch (OperationCanceledException) { /* graceful */ }
        catch (Exception ex)
        {
            Console.WriteLine($"Server: client handler error {ex.Message}");
        }
    }
}

// ---- helpers ----
static string DecompressText(byte[] payload)
{
    var decompressed = DecompressDeflate(payload);
    var text = Encoding.GetEncoding("shift_jis").GetString(decompressed);
    return text;
}

static async Task<bool> ReadExactAsync(NetworkStream ns, byte[] buffer, int count, CancellationToken ct)
{
    int off = 0;
    while (off < count)
    {
        int n = await ns.ReadAsync(buffer.AsMemory(off, count - off), ct);
        if (n == 0) return false; // 切断
        off += n;
    }
    return true;
}

static byte[] DecompressDeflate(byte[] compressed)
{
    using var input = new MemoryStream(compressed);
    using var def = new DeflateStream(input, CompressionMode.Decompress);
    using var output = new MemoryStream();
    def.CopyTo(output);
    return output.ToArray();
}

// GZip版が良ければこちら（クライアント側も合わせて変更すること）
/*
static byte[] DecompressGZip(byte[] compressed)
{
    using var input = new MemoryStream(compressed);
    using var gz = new GZipStream(input, CompressionMode.Decompress);
    using var output = new MemoryStream();
    gz.CopyTo(output);
    return output.ToArray();
}
*/
