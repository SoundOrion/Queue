using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Queue.Library;

public static class FileReceiver
{
    public static async Task ReceiveFileWithOptionalDecompressionAsync(
        NetworkStream ns,
        string savePath,
        CancellationToken ct,
        int ioBufferSize = 1024 * 64)
    {
        // 1) 長さ4バイト
        var lenBuf = new byte[4];
        await ReadExactAsync(ns, lenBuf, 0, 4, ct);
        int totalLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        if (totalLen < 1) throw new InvalidDataException("Invalid framed length.");

        // 2) フラグ1バイト
        var flagBuf = new byte[1];
        await ReadExactAsync(ns, flagBuf, 0, 1, ct);
        byte flag = flagBuf[0];

        int remaining = totalLen - 1; // 残りは本文サイズ

        // 3) 本文を bounded に制限して読む
        using var bounded = new BoundedReadStream(ns, remaining);

        // 4) フラグに応じて処理
        if (flag == 1)
        {
            // Brotli 解凍
            using var br = new BrotliStream(bounded, CompressionMode.Decompress, leaveOpen: false);
            using var outFs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, ioBufferSize, FileOptions.Asynchronous);
            await br.CopyToAsync(outFs, ioBufferSize, ct);
        }
        else if (flag == 0)
        {
            // 非圧縮のまま保存
            using var outFs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, ioBufferSize, FileOptions.Asynchronous);
            await bounded.CopyToAsync(outFs, ioBufferSize, ct);
        }
        else
        {
            throw new InvalidDataException($"Unknown compression flag: {flag}");
        }

        // 余剰読みは bounded が防ぐ（remainingを超えてReadできない）
    }

    // 要求バイト数ちょうど読む
    private static async Task ReadExactAsync(Stream s, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int readTotal = 0;
        while (readTotal < count)
        {
            int n = await s.ReadAsync(buffer, offset + readTotal, count - readTotal, ct);
            if (n <= 0) throw new EndOfStreamException();
            readTotal += n;
        }
    }
}

/// <summary>
/// 基底ストリームから最大 remaining バイトまでしか読み出さないラッパー。
/// メッセージ境界を越えないための安全装置。
/// </summary>
public sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private int _remaining;
    public BoundedReadStream(Stream inner, int remaining)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _remaining = remaining >= 0 ? remaining : throw new ArgumentOutOfRangeException(nameof(remaining));
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _remaining;
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining == 0) return 0;
        int toRead = Math.Min(count, _remaining);
        int n = _inner.Read(buffer, offset, toRead);
        _remaining -= n;
        return n;
    }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_remaining == 0) return 0;
        int toRead = Math.Min(count, _remaining);
        int n = await _inner.ReadAsync(buffer, offset, toRead, cancellationToken);
        _remaining -= n;
        return n;
    }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

