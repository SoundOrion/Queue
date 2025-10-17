using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO.Compression;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();
Console.WriteLine("Server: listening on 0.0.0.0:5000 ...");

using var client = await listener.AcceptTcpClientAsync(cts.Token);
Console.WriteLine("Server: client connected.");
using var ns = client.GetStream();

// 受信ループ： [length(4byte, little-endian)] + [payload(圧縮後)]
var lenBuf = new byte[4];
while (!cts.IsCancellationRequested)
{
    // 4バイト（長さ）を正確に読む
    if (!await ReadExactAsync(ns, lenBuf, 4, cts.Token))
    {
        Console.WriteLine("Server: client closed.");
        break;
    }

    int len = BitConverter.ToInt32(lenBuf, 0);
    if (len <= 0 || len > 100_000_000) // 簡易上限チェック
        throw new InvalidDataException($"Invalid payload length: {len}");

    var payload = new byte[len];
    if (!await ReadExactAsync(ns, payload, len, cts.Token))
    {
        Console.WriteLine("Server: client closed during payload.");
        break;
    }

    // 受信payloadは「圧縮後のバイト列」
    var decompressed = DecompressDeflate(payload); // ← Deflate 解凍（GZipに替えるなら DecompressGZip）
    var text = Encoding.GetEncoding("shift_jis").GetString(decompressed);
    Console.WriteLine($"[RECV] {text}");
}

listener.Stop();
Console.WriteLine("Server: stopped.");

// ---- helpers ----
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

// GZip版が良ければこちら
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
