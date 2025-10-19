using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Queue.Library;

public static class FileSender
{
    /// <summary>
    /// Int32フレーム（[len:4B LE][flag:1B][payload...]）
    /// flag: 0 = 生、1 = Brotli圧縮
    /// 圧縮して小さければ圧縮版を、そうでなければ元のファイルを送信します。
    /// </summary>
    public static async Task SendFileWithOptionalCompressionAsync(
        NetworkStream ns,
        string filePath,
        CancellationToken ct,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        int ioBufferSize = 1024 * 64)
    {
        var fi = new FileInfo(filePath);
        if (!fi.Exists) throw new FileNotFoundException(filePath);

        // 長さが 2GB を超えるならこのプロトコルでは送れない（flag 1B を含めるため -1）
        if (fi.Length > int.MaxValue - 1)
            throw new InvalidOperationException("File too large for 32-bit framing (need Int64 framing or chunking).");

        // まずテンポラリに圧縮してサイズ比較
        string tmp = Path.GetTempFileName();
        try
        {
            long compressedLen;
            using (var src = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, ioBufferSize, FileOptions.Asynchronous))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, ioBufferSize, FileOptions.Asynchronous))
            using (var brotli = new BrotliStream(dst, compressionLevel, leaveOpen: true))
            {
                await src.CopyToAsync(brotli, ioBufferSize, ct);
            }
            compressedLen = new FileInfo(tmp).Length;

            bool useCompressed = compressedLen < fi.Length;

            // payloadLen = flag(1B) + dataLen
            long dataLen = useCompressed ? compressedLen : fi.Length;
            long payloadLen = 1 + dataLen;
            if (payloadLen > int.MaxValue)
                throw new InvalidOperationException("Framed payload exceeds Int32 limit.");

            // 4バイトの長さ（LE）を書き出し
            var lenBuf = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBuf, checked((int)payloadLen));
            await ns.WriteAsync(lenBuf, 0, 4, ct);

            // フラグを書き出し
            byte flag = useCompressed ? (byte)1 : (byte)0;
            await ns.WriteAsync(new[] { flag }, 0, 1, ct);

            // 本文を書き出し（ストリーミング、コピー配列を作らない）
            if (useCompressed)
            {
                using var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, ioBufferSize, FileOptions.Asynchronous);
                await fs.CopyToAsync(ns, ioBufferSize, ct);
            }
            else
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, ioBufferSize, FileOptions.Asynchronous);
                await fs.CopyToAsync(ns, ioBufferSize, ct);
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }
}
