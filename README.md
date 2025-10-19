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


