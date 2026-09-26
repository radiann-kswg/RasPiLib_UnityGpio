# 通信プロトコル（v1 草案）

SDK とデーモンの間の取り決め。**この文書が両者の正本**で、どちらかを変えるときは先にここを直す。
実装時に細部は変わりうる（版数 `proto` は 1 のまま、実装完了時に確定させる）。

## 1. 伝送路

| 経路 | 用途 | 認証 |
| --- | --- | --- |
| Unix ソケット `/run/unitygpio/unitygpio.sock`（stream） | Pi 上のゲーム | ファイルの権限（`gpio` グループ） |
| TCP（既定で無効。`/etc/unitygpio.conf` で待受アドレスを指定） | Editor の遠隔モード | `hello` のトークン必須 |

- 1 メッセージ = UTF-8 の JSON 1 行（末尾 `\n`）。`socat` などで手で叩ける。
- 同時接続は 1 つ（D11）。使用中に来た接続には `SESSION_TAKEN` を返して閉じる。

## 2. 要求と応答

- クライアント → デーモンの要求は `op` を持つ。
- `id`（整数）を付けた要求には、同じ `id` の応答が 1 つ返る。
- **`id` を付けない要求は送るだけ**（`write` / `pwm_set` など、D13）。失敗したときだけ `error` イベントが届く。

```json
→ {"id":1,"op":"hello","proto":1,"token":"..."}
← {"id":1,"ok":true,"proto":1,"board":"pi5","i2c":[1],"pwm":[12,13],"reserved":[21]}
```

- `board`: `pi4` / `pi5`。`i2c` / `pwm`: 今使えるバスとピン（無効なら空配列）。`reserved`: 予約ピン。
- `hello` より前の要求、版数の不一致、トークンの誤りは、エラーを返して切断する。

失敗の応答: `{"id":2,"ok":false,"code":"BUSY","message":"..."}`

## 3. 操作（v1）

| op | 引数 | 応答 | 内容 |
| --- | --- | --- | --- |
| `hello` | `proto`, `token`（TCP のみ） | 上記 | 最初に 1 回 |
| `claim_out` | `pin`, `value` | `ok` | 出力として確保し初期値を出す |
| `claim_in` | `pin`, `bias`, `edge`, `debounce_ms` | `ok`, `value` | 入力として確保し、現在値を返す。`bias`: `pull_up` / `pull_down` / `none`。`edge`: `rising` / `falling` / `both` / `none` |
| `write` | `pin`, `value` | なし（送るだけ） | 出力値を変える |
| `pwm_claim` | `pin` | `ok` | PWM ピン（12 / 13）を確保 |
| `pwm_set` | `pin`, `period_ns`, `duty_ns`, `enabled` | なし（送るだけ） | 周期・デューティ・有効 |
| `release` | `pin` | `ok` | 後始末（§5）をして解放 |
| `i2c` | `bus`, `addr`, `write`（16 進文字列）, `read`（バイト数） | `ok`, `data`（16 進文字列） | 書いて続けて読む（リピーテッドスタート）。`write` だけ・`read` だけも可 |

RGB LED とサーボは SDK 側で上の操作を組み合わせて作る（デーモンは知らない）。

## 4. イベント（デーモン → クライアント）

`op` の代わりに `ev` を持つ。

```json
← {"ev":"edge","pin":17,"value":0,"t":123456789}
← {"ev":"error","code":"RESERVED","pin":21,"message":"..."}
```

- `edge`: 入力の変化。`t` はデーモン側の単調時刻（ns）。
- `error`: 送るだけの要求の失敗。

## 5. 切断時の後始末（D9）

接続が切れたら（正常終了・SIGKILL・ネットワーク断を問わず）、その接続が確保していたすべてについて:

1. 出力ピン → 入力（バイアス無し）に設定し直してから解放する。
2. PWM → デューティ 0・無効にしてから解放する。
3. 入力ピン → 解放する。

`release` の後始末も同じ手順。

## 6. エラーコード

| code | 意味 |
| --- | --- |
| `PROTO` | 版数の不一致・形式の誤り |
| `AUTH` | トークンの誤り（TCP） |
| `SESSION_TAKEN` | ほかの接続が使用中 |
| `BAD_PIN` | 存在しないピン、または操作に合わないピン（PWM でないピンへの `pwm_claim` など） |
| `RESERVED` | 予約ピン |
| `BUSY` | 有効なバスが使っているピン、またはカーネルの別ドライバが使用中 |
| `DISABLED` | バスが無効（`message` に `unitygpio-config ... on` と再起動を案内） |
| `IO` | I2C の NACK・タイムアウトなど |
