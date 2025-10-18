using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// バックプレッシャ付き送信キュー
var sendQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024)
{
    FullMode = BoundedChannelFullMode.Wait,
    SingleReader = true,  // 送信タスクは1つ
    SingleWriter = false, // 複数生産者OK
});

var sender = Task.Run(async () =>
{
    try
    {
        using var client = new TcpClient();
        client.ReceiveTimeout = 30000;
        client.SendTimeout = 30000;

        await client.ConnectAsync("127.0.0.1", 5000);
        Console.WriteLine("Client: connected to 127.0.0.1:5000");

        using var ns = client.GetStream();

        // ここで一度だけ確保して使い回す
        var lenBuf = new byte[4];

        await foreach (var payload in sendQueue.Reader.ReadAllAsync(cts.Token))
        {
            // 4バイトのメッセージ長（Big-Endian）
            BinaryPrimitives.WriteInt32BigEndian(lenBuf.AsSpan(), payload.Length);

            // 配列オーバーロードを使う（Span を await またがせない）
            await ns.WriteAsync(lenBuf, 0, 4, cts.Token);
            await ns.WriteAsync(payload, 0, payload.Length, cts.Token);

            Console.WriteLine($"Client: sent {payload.Length} bytes.");
        }

        Console.WriteLine("Client: queue completed, closing.");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Client: canceled.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Client: error {ex.Message}");
    }
}, cts.Token);

// ---- 送信例（Shift_JIS → Deflate圧縮） ----
await EnqueueTextAsync("こんにちは、サーバー！", sendQueue, cts.Token);
await EnqueueTextAsync("圧縮して送るよ（Deflate）", sendQueue, cts.Token);
await EnqueueTextAsync("最後のメッセージです。", sendQueue, cts.Token);

// 完了
sendQueue.Writer.TryComplete();
await sender;

Console.WriteLine("Client: done.");

// ---- helpers ----
static async Task EnqueueTextAsync(string text, Channel<byte[]> queue, CancellationToken ct)
{
    var sjis = Encoding.GetEncoding("shift_jis").GetBytes(text);
    var compressed = CompressDeflate(sjis); // ← Deflate圧縮（GZipに替えるなら CompressGZip）
    await queue.Writer.WriteAsync(compressed, ct);
}

static byte[] CompressDeflate(byte[] data)
{
    using var output = new MemoryStream();
    using (var def = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
    {
        def.Write(data, 0, data.Length);
    }
    return output.ToArray();
}

// GZip版が良ければこちら（サーバ側も合わせて変更すること）
/*
static byte[] CompressGZip(byte[] data)
{
    using var output = new MemoryStream();
    using (var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
    {
        gz.Write(data, 0, data.Length);
    }
    return output.ToArray();
}
*/
