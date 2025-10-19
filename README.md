# 📦 メッセージ送受信フォーマット仕様（BitConverter vs BinaryPrimitives 比較）

## 概要

任意長のバイナリ `payload` を送受信する際、**先頭に4バイトの長さ（Little-Endian）** を付与します。
**ワイヤフォーマット（送信データの並び）は両方式で同一**です。

```
[length: 4 bytes (Little Endian)] [payload: variable length]
```

---

## 送信（Client）

### ✅ BinaryPrimitives 版（推奨：明示エンディアン + 配列再利用）

```csharp
using System.Buffers.Binary;
// using System; using System.IO; など適宜

// ここで一度だけ確保して使い回す
var lenBuf = new byte[4];

byte[] payload = /* 任意のデータ */;

// 4バイトのメッセージ長（Little Endian）を明示的に書き込む
BinaryPrimitives.WriteInt32LittleEndian(lenBuf, payload.Length);

// 配列オーバーロードを使う（Span は await をまたがない）
await ns.WriteAsync(lenBuf, 0, 4, cts.Token);
await ns.WriteAsync(payload, 0, payload.Length, cts.Token);

// NetworkStream なら Flush は通常不要（バッファリングしないため）
// await ns.FlushAsync(cts.Token); // ラッパーによっては必要
Console.WriteLine($"Client: sent {payload.Length} bytes.");
```

### ✅ BitConverter 版（シンプル：毎回4バイト配列を作る）

```csharp
// using System; using System.IO; など適宜

byte[] payload = /* 任意のデータ */;

// 実行環境のエンディアンに依存するため、Little-Endian に正規化する
var len = BitConverter.GetBytes(payload.Length);
if (!BitConverter.IsLittleEndian)
{
    Array.Reverse(len); // Little-Endian に揃える
}

await ns.WriteAsync(len, 0, len.Length, cts.Token);
await ns.WriteAsync(payload, 0, payload.Length, cts.Token);

// NetworkStream なら通常 Flush 不要
// await ns.FlushAsync(cts.Token);
Console.WriteLine($"Client: sent {payload.Length} bytes.");
```

---

## 受信（Server）

> ポイント：まず **4バイト** 読んで長さを取得 → 続けて **その長さ分** を読む。
> `ReadAsync` は要求バイト数ちょうどを一度で返す保証がないため、**ちょうど読む**処理が必要です。

### 共通：ヘルパー（ちょうど N バイト読む）

```csharp
static async Task ReadExactAsync(Stream s, byte[] buffer, int offset, int count, CancellationToken ct)
{
    int readTotal = 0;
    while (readTotal < count)
    {
        int n = await s.ReadAsync(buffer, offset + readTotal, count - readTotal, ct);
        if (n <= 0) throw new EndOfStreamException();
        readTotal += n;
    }
}
```

### ✅ BinaryPrimitives 版（推奨）

```csharp
using System.Buffers.Binary;

// 4バイトの長さを読む
var lenBuf = new byte[4];
await ReadExactAsync(ns, lenBuf, 0, 4, cts.Token);

// Little-Endian で int を復元（環境エンディアン非依存）
int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);

// 本体を読む
var payload = new byte[payloadLength];
await ReadExactAsync(ns, payload, 0, payloadLength, cts.Token);

Console.WriteLine($"Server: received {payloadLength} bytes.");
```

### ✅ BitConverter 版（環境が BE でも正しく処理する）

```csharp
// 4バイトの長さを読む
var lenBuf = new byte[4];
await ReadExactAsync(ns, lenBuf, 0, 4, cts.Token);

// BitConverter は実行環境のエンディアンを前提に解釈するため、受信データ（Little-Endian）に合わせて並べ替え
if (!BitConverter.IsLittleEndian)
{
    Array.Reverse(lenBuf); // Little-Endian → 環境エンディアン に合わせる
}
int payloadLength = BitConverter.ToInt32(lenBuf, 0);

// 本体を読む
var payload = new byte[payloadLength];
await ReadExactAsync(ns, payload, 0, payloadLength, cts.Token);

Console.WriteLine($"Server: received {payloadLength} bytes.");
```

---

## 両方式の比較

| 観点            | BinaryPrimitives                                                 | BitConverter                                        |
| ------------- | ---------------------------------------------------------------- | --------------------------------------------------- |
| **ワイヤフォーマット** | 同じ（Little-Endian 4B + payload）                                   | 同じ                                                  |
| **エンディアン指定**  | **明示（Little-Endian固定）**。環境に依存しない                                 | 実行環境のエンディアンに依存。Little-Endian を保証するには **Reverse 必須** |
| **割り当て**      | 4バイトバッファを **再利用**可能 → GC 負荷小                                     | `GetBytes` が **毎回新規配列**を作成 → 高頻度だと GC 負荷増           |
| **可読性**       | 「Little-Endian 固定」がコードから一目で分かる                                   | シンプルだが BE 環境考慮のコードが必要                               |
| **API 追加**    | `System.Buffers.Binary` が必要                                      | BCL に標準（常に利用可）                                      |
| **パフォーマンス**   | 高頻度送受信で有利（割り当て減 + 明示変換）                                          | 単発/低頻度なら十分簡潔                                        |
| **Span の扱い**  | `WriteInt32LittleEndian(Span<byte>, int)` が使える（ただし await を跨がせない） | 配列 API 中心。簡単                                        |

> まとめ：**明示的なエンディアン管理と割り当て削減**の観点から、**BinaryPrimitives が推奨**。
> ただし「簡潔さ」を優先する単発処理なら BitConverter でも問題ありません（Reverse を忘れない）。

---

## フォーマット例

* `payload.Length == 1000 (0x000003E8)` の場合、最初の 4 バイトは Little-Endian で
  `E8 03 00 00` に続いて 1000 バイトの本体が送られます。

```
E8 03 00 00 [ ...1000 bytes of data... ]
```

---

## 実装上の注意

* **NetworkStream での Flush**
  `NetworkStream.WriteAsync` は即時に下位ソケットへ渡すため、**通常 `FlushAsync` は不要**。
  ただし、`StreamWriter` 等の**バッファ付きラッパー**を挟む場合は `Flush` が必要になります。
* **Span を await またぎで使わない**
  非同期境界を跨ぐ際は **配列オーバーロード**（`byte[]`）を使用してください。
* **長さの妥当性チェック**
  受信側では、負数／過大サイズ（上限超過）などを検出して早期に拒否するのが安全です。

---

## クイックリファレンス（最小コード）

**送信（BinaryPrimitives 推奨）**

```csharp
var lenBuf = new byte[4];
BinaryPrimitives.WriteInt32LittleEndian(lenBuf, payload.Length);
await ns.WriteAsync(lenBuf, 0, 4, ct);
await ns.WriteAsync(payload, 0, payload.Length, ct);
```

**受信（BinaryPrimitives 推奨）**

```csharp
var lenBuf = new byte[4];
await ReadExactAsync(ns, lenBuf, 0, 4, ct);
int len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
var payload = new byte[len];
await ReadExactAsync(ns, payload, 0, len, ct);
```

# 📦 メッセージ送信方式の比較（BinaryPrimitives / BitConverter / Socket）

共通フォーマット：

```
[length: 4 bytes (Little Endian)] [payload: variable length bytes]
```

受信側は「先に4バイトで長さを読み、その長さ分のデータを読む」だけです。
いずれの方法も **ネットワーク上のデータ内容は完全に同一** になります。

---

## 1️⃣ BinaryPrimitives × 2回 Write（推奨）

```csharp
using System.Buffers.Binary;

public static async Task Send_BinaryPrimitives_2Writes(NetworkStream ns, byte[] payload, CancellationToken ct)
{
    var lenBuf = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(lenBuf, payload.Length);

    await ns.WriteAsync(lenBuf, 0, 4, ct);
    await ns.WriteAsync(payload, 0, payload.Length, ct);
}
```

✅ 明示的 Little-Endian
✅ 配列再利用で GC 負荷小
✅ コピーなし・最もバランス良い

---

## 2️⃣ BinaryPrimitives × 1回 Write（結合）

```csharp
using System.Buffers.Binary;

public static async Task Send_BinaryPrimitives_1Write(NetworkStream ns, byte[] payload, CancellationToken ct)
{
    var buf = new byte[4 + payload.Length];
    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), payload.Length);
    Buffer.BlockCopy(payload, 0, buf, 4, payload.Length);

    await ns.WriteAsync(buf, 0, buf.Length, ct);
}
```

✅ 結果は同じ
⚠️ 一時バッファ確保＋payloadコピーが発生（大容量に不向き）

---

## 3️⃣ BitConverter × 2回 Write

```csharp
public static async Task Send_BitConverter_2Writes(NetworkStream ns, byte[] payload, CancellationToken ct)
{
    var len = BitConverter.GetBytes(payload.Length);
    if (!BitConverter.IsLittleEndian) Array.Reverse(len); // LE化

    await ns.WriteAsync(len, 0, len.Length, ct);
    await ns.WriteAsync(payload, 0, payload.Length, ct);
}
```

✅ シンプルで理解しやすい
⚠️ Big-Endian環境では Reverse 必須
⚠️ `GetBytes` が毎回配列確保（微GC）

---

## 4️⃣ BitConverter × 1回 Write（結合）

```csharp
public static async Task Send_BitConverter_1Write(NetworkStream ns, byte[] payload, CancellationToken ct)
{
    var buf = new byte[4 + payload.Length];
    var len = BitConverter.GetBytes(payload.Length);
    if (!BitConverter.IsLittleEndian) Array.Reverse(len);

    Buffer.BlockCopy(len, 0, buf, 0, 4);
    Buffer.BlockCopy(payload, 0, buf, 4, payload.Length);

    await ns.WriteAsync(buf, 0, buf.Length, ct);
}
```

✅ 結果は同一
⚠️ 長さ配列＋結合バッファ両方確保
⚠️ パフォーマンス的には BinaryPrimitives より劣る

---

## 5️⃣ Socket.SendAsync（複数バッファ一括送信：.NET 8+）

```csharp
using System.Buffers.Binary;
using System.Net.Sockets;

public static async Task Send_Socket_GatherWrite(Socket socket, byte[] payload, CancellationToken ct)
{
    var lenBuf = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(lenBuf, payload.Length);

    var buffers = new ReadOnlyMemory<byte>[]
    {
        lenBuf,
        payload
    };

    await socket.SendAsync(buffers, SocketFlags.None, ct);
}
```

✅ コピーなしで1回送信
✅ 最速だが `NetworkStream` では不可（Socket直接利用時専用）
⚠️ .NET 8以降でサポート

---

## ⚖️ 比較まとめ

| 方法                    | Write回数 | 余分な配列確保   | エンディアン明示   | コピー有無 | 適用範囲          | 備考            |
| --------------------- | ------- | --------- | ---------- | ----- | ------------- | ------------- |
| BinaryPrimitives × 2回 | 2       | lenBufのみ  | ✅ 明示Little | ❌ 無し  | NetworkStream | **推奨：バランス最良** |
| BinaryPrimitives × 1回 | 1       | 4+payload | ✅ 明示Little | ⚠️ 有り | NetworkStream | 小サイズで簡潔にしたい時  |
| BitConverter × 2回     | 2       | len配列毎回   | ⚠️ 環境依存    | ❌ 無し  | NetworkStream | 簡潔だがLE化必要     |
| BitConverter × 1回     | 1       | len＋結合buf | ⚠️ 環境依存    | ⚠️ 有り | NetworkStream | 手軽だが非効率       |
| Socket.SendAsync      | 1       | lenBufのみ  | ✅ 明示Little | ❌ 無し  | Socket直       | **最速・最少コピー**  |

---

## ✅ 総評

| 評価軸           | ベスト選択                         | コメント                  |
| ------------- | ----------------------------- | --------------------- |
| **移植性・正確性**   | 🥇 BinaryPrimitives           | 明示Little-Endianで環境非依存 |
| **パフォーマンス**   | 🥇 Socket.SendAsync (.NET 8+) | コピーゼロ・単一送信            |
| **メモリ効率**     | 🥇 BinaryPrimitives × 2回      | GC負荷最小（lenBuf再利用）     |
| **シンプルさ**     | 🥇 BitConverter × 2回          | 分かりやすいがGC面では劣る        |
| **1回送信したい場合** | BinaryPrimitives × 1回         | シンプルにまとまるがコピーあり       |

---

## 🧪 結果の等価性

すべてのパターンで、ネットワーク上に送られるデータは同一：

```
[length:4bytes LittleEndian][payload:nbytes]
```

つまり、**受信側の処理は共通で問題なし**です。

---

> 💡実務でのおすすめ：
>
> * 通常用途 → **BinaryPrimitives × 2回 Write**
> * 高性能Socket処理（.NET 8以降）→ **Socket.SendAsync**
> * 簡易ツール・小テスト → **BitConverter × 2回 Write**

---
