using System.Net.Sockets;
using System.Text;
using System.IO.Compression;
using System.Threading.Channels;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// 送信用キュー（メッセージ単位で圧縮後byte[]を積む）
var sendQueue = Channel.CreateUnbounded<byte[]>();

// 送信タスク（[length]+[payload] で書き出す）
var sender = Task.Run(async () =>
{
    using var client = new TcpClient();
    await client.ConnectAsync("127.0.0.1", 5000);
    Console.WriteLine("Client: connected to 127.0.0.1:5000");
    using var ns = client.GetStream();

    await foreach (var payload in sendQueue.Reader.ReadAllAsync(cts.Token))
    {
        var len = BitConverter.GetBytes(payload.Length); // little-endian
        await ns.WriteAsync(len, 0, len.Length, cts.Token);
        await ns.WriteAsync(payload, 0, payload.Length, cts.Token);
        await ns.FlushAsync(cts.Token);
        Console.WriteLine($"Client: sent {payload.Length} bytes.");
    }
    Console.WriteLine("Client: queue completed, closing.");
}, cts.Token);

// （例）送りたい文字列をShift_JIS→Deflate圧縮して投入
EnqueueText("こんにちは、サーバー！", sendQueue);
EnqueueText("圧縮して送るよ（Deflate）", sendQueue);
EnqueueText("最後のメッセージです。", sendQueue);

// 完了
sendQueue.Writer.Complete();
await sender;

Console.WriteLine("Client: done.");

// ---- helpers ----
static void EnqueueText(string text, Channel<byte[]> queue)
{
    var sjis = Encoding.GetEncoding("shift_jis").GetBytes(text);
    var compressed = CompressDeflate(sjis); // ← Deflate圧縮（GZipに替えるなら CompressGZip）
    queue.Writer.TryWrite(compressed);
}

static byte[] CompressDeflate(byte[] data)
{
    using var output = new MemoryStream();
    using (var def = new DeflateStream(output, CompressionMode.Compress, leaveOpen: true))
    {
        def.Write(data, 0, data.Length);
    }
    return output.ToArray();
}

// GZip版が良ければこちら
/*
static byte[] CompressGZip(byte[] data)
{
    using var output = new MemoryStream();
    using (var gz = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
    {
        gz.Write(data, 0, data.Length);
    }
    return output.ToArray();
}
*/
