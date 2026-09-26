# アーキテクチャ

## 1. 全体像

```
 ┌──────────── Raspberry Pi（arm64） ─────────────────────────────┐
 │                                                                │
 │  Unity ゲーム（x86_64 を box64 で実行してもよい）              │
 │   └ Unity SDK（純 C#） ──┐                                     │
 │                          │ Unix ソケット                        │
 │                          │ /run/unitygpio/unitygpio.sock        │
 │                          ▼                                     │
 │  unitygpiod（Python 3・arm64 ネイティブ） ── libgpiod v2 → /dev/gpiochip*
 │     ▲   設定 /etc/unitygpio.conf          ├ sysfs  → /sys/class/pwm
 │     │                                    └ ioctl  → /dev/i2c-1
 └─────┼──────────────────────────────────────────────────────────┘
       │ TCP（遠隔モード。既定で無効・トークン必須）
 ┌─────┴───── 開発 PC（Windows など） ───┐
 │  Unity Editor ＋ Unity SDK            │  ※模擬モードなら接続しない
 └───────────────────────────────────────┘
```

## 2. 構成要素

| 要素 | 役割 | 置き場所（予定） |
| --- | --- | --- |
| プロトコル | SDK とデーモンの間のメッセージ形式・版数・エラーコード | [protocol.md](protocol.md) |
| `unitygpiod` | GPIO・PWM・I2C の実処理。ピン予約の判定、1 接続の制限、切断時の後始末、遠隔モードの TCP | `daemon/unitygpiod.py` |
| `unitygpiod.service` | systemd ユニット。root ではなく gpio・i2c グループ権限で起動 | `daemon/` |
| `/etc/unitygpio.conf` | 予約ピン、TCP の有効化・待受アドレス・トークン | `daemon/unitygpio.conf`（例） |
| `unitygpio-config` | I2C・PWM を有効/無効にする CLI。`config.txt` を書き換え、再起動を促す | `daemon/` |
| `install.sh` | 任意の Raspberry Pi OS へ上記を導入する。pi-gen の chroot 内からも使える | `daemon/` |
| Unity SDK | 公開 API、接続管理（Unix ソケット / TCP / 模擬）、メインスレッドへのイベント配送 | `unity/Runtime/` |
| Editor 拡張 | 遠隔モードの接続先・トークンの設定、模擬モードのピン表示 | `unity/Editor/` |

デーモンを Python にするのは、依存が `python3-libgpiod` だけで済み（PWM は sysfs、I2C は標準ライブラリの `fcntl.ioctl`）、
Pi 上ですぐ直せるから（仮。応答が目標に届かなければ C へ置き換える。プロトコルは変えない）。

## 3. ライセンス境界

**このリポジトリ（MIT）の中身は、特定の OS を知らない。** OS との接点は次の 3 つだけにする。

1. `/etc/unitygpio.conf`（予約ピンなど）
2. `config.txt` の I2C・PWM の行（`unitygpio-config` で切り替え）
3. v2 以降: 一時停止・再開の通知（`unitygpio-ctl pause|resume` を予定）

OS 固有の事情（ホームボタンが GPIO21 を使う、ランチャーがゲームを止める、など）は、
上の接点を通して**OS 側のリポジトリ（そのリポジトリのライセンス）で**設定する。
例えば [RasPiOS_UnityConsole](https://github.com/radiann-kswg/RasPiOS_UnityConsole)（CC BY-SA 4.0）は、
本リポジトリをサブモジュールとして取り込み、予約ピン `21` を書いた設定をイメージに入れる。

| 置く場所 | 例 |
| --- | --- |
| 本リポジトリ（MIT） | デーモン、SDK、プロトコル、汎用の導入スクリプト、推奨ピン配置表 |
| OS 側リポジトリ | 予約ピンの値、I2C・PWM の既定の有効/無効、ビルド時の導入手順、ランチャーとの連携 |

## 4. 他の Raspberry Pi 用 OS へ導入する方法（実装後）

1. Raspberry Pi OS（trixie・arm64）に本リポジトリを置き、`sudo daemon/install.sh` を実行する。
   `python3-libgpiod` の導入、デーモンとユニットの設置、`systemctl enable --now` まで行う。
2. `/etc/unitygpio.conf` の `reserved` に、OS やほかの HAT が使うピンを書く。
3. I2C や PWM が要るなら `sudo unitygpio-config i2c on` / `sudo unitygpio-config pwm on` を実行し、再起動する。
4. ゲームを動かすユーザーを `gpio` グループに入れる（Raspberry Pi OS の初期ユーザーは最初から入っている）。

pi-gen で焼き込む場合は、独自ステージの `on_chroot` 内で `install.sh --no-start` を実行する。

## 5. I2C・PWM の有効化（D8）

- `unitygpio-config i2c on|off` → `config.txt` の `dtparam=i2c_arm=on` を追加/削除（raspi-config と同じ行）。
- `unitygpio-config pwm on|off` → `dtoverlay=pwm-2chan,...` の行を追加/削除（Pi 4B と Pi 5 の書き分けは実装時に確認）。
- どちらも**再起動で反映**。PC で SD カードを開いて `config.txt` を直接書き換えてもよい。
- デーモンは設定ファイルではなく**起動時のカーネルの実状態**（`/dev/i2c-1` と pwmchip の有無）を見て、
  使えるバスと、それが占有するピン（I2C: GPIO2/3、PWM: GPIO12/13）を決める。設定と実状態のずれが起きない。
- 無効なバスを使おうとすると `DISABLED` エラーで、有効にする方法をメッセージに含める。

## 6. ピン配置

ピンは BCM 番号で自由に指定できる（D7）。以下は、将来のバス（v2 以降）とぶつからないように
拡張パーツを作るための**推奨**配置。

| 用途 | BCM | 物理ピン | 備考 |
| --- | --- | --- | --- |
| I2C1 SDA / SCL | GPIO2 / GPIO3 | 3 / 5 | 有効時はデジタル入出力に使えない。基板上に 1.8kΩ プルアップあり |
| PWM ch0 / ch1 | GPIO12 / GPIO13 | 32 / 33 | 有効時はデジタル入出力に使えない。サーボ・LED の明るさ用 |
| **デジタル入出力（推奨）** | GPIO5, 6, 16, 17, 22, 23, 24, 25, 26, 27 | 29, 31, 36, 11, 15, 16, 18, 22, 37, 13 | v2 以降のバスと重ならない |
| デジタル入出力（可） | GPIO4 | 7 | v2 の 1-Wire 既定ピン |
| デジタル入出力（可） | GPIO18, 19, 20, 21 | 12, 35, 38, 40 | I2S（音声 HAT）と共用 |
| SPI0（v2 予定） | GPIO7, 8, 9, 10, 11 | 26, 24, 21, 19, 23 | 今はデジタル入出力に使えるが、パーツ設計では避ける |
| UART（v2 予定） | GPIO14 / GPIO15 | 8 / 10 | 同上 |
| 非推奨 | GPIO0 / GPIO1 | 27 / 28 | HAT の ID EEPROM 用 |

- 物理 35〜40 番ピンがほかの基板（例: RasPiOS_UnityConsole のホームボタン基板）で塞がる場合は、
  HAT や 2x20 のブレークアウト用パーツで引き出す（D14）。
- GPIO は 3.3V。5V の信号を直接入れないこと。

## 7. SDK の API（草案）

実装時に名前は変わりうる。形（同期・非同期の別、event の配送先）は D13 で確定。

```csharp
using RasPiLib.UnityGpio;

if (!Gpio.IsAvailable) { /* 拡張パーツ無しで遊べるようにする */ }

var button = Gpio.OpenInput(17, Bias.PullUp, Edge.Both, debounceMs: 10);
bool pressed = !button.Value;                  // 保持している最新値を即時に返す
button.Changed += v => Debug.Log($"button {v}"); // メインスレッドで発火

var lamp = Gpio.OpenOutput(27, initialValue: false);
lamp.Value = true;                              // 送るだけ（ブロックしない）

var servo = new Servo(Gpio.OpenPwm(12, frequencyHz: 50), minPulseUs: 500, maxPulseUs: 2500);
servo.Angle = 90f;

var rgb = Gpio.OpenRgbLed(r: 13, g: 22, b: 23, activeLow: false); // R だけ明るさ可変
rgb.Color = new Color(0.3f, 1f, 0f);

var sensor = Gpio.OpenI2c(bus: 1, address: 0x76);
byte[] id = await sensor.WriteReadAsync(new byte[] { 0xD0 }, readLength: 1); // Awaitable<byte[]>
```

各ハンドルは `IDisposable`。`Dispose` でデーモン側の確保も解放する。

## 8. パッケージ情報（仮）

| 項目 | 値 |
| --- | --- |
| UPM パッケージ名 | `net.numbertales-radiann.unitygpio` |
| C# 名前空間 | `RasPiLib.UnityGpio` |
| 最低 Unity | 6000.3 |
| デーモンのソケット | `/run/unitygpio/unitygpio.sock`（モード 0660、グループ `gpio`） |
| 遠隔モードの TCP ポート | `8787`（既定で無効） |
