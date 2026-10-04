# RasPiLib_UnityGpio

Raspberry Pi 4B / 5 の GPIO を **Unity 6.3 LTS 製の Linux ゲーム**から使うためのライブラリ。
Pi 上で動く常駐デーモン（`unitygpiod`）と、Unity 用の純 C# パッケージ（UPM）の 2 つで構成する。

> **状態: v1 実装済み（2026-10-04）・実機未検証。**
> デーモンの純ロジック自己テストと、Unity 6000.3 での模擬モードの EditMode テストは通っている。
> Pi 4B / Pi 5 での結合試験（[docs/requirements.md §7](docs/requirements.md#7-実装時に実機で確かめること)）はこれから。

> **License**: [MIT](LICENSE)
> © ラジアン（柏木主税） / RadianN_kswg

## なぜデーモン方式か

Unity の Linux プレイヤーは x86_64 なので、Pi（arm64）では [box64](https://github.com/ptitSeb/box64) 上で動かすことが多い
（[RasPiOS_UnityConsole](https://github.com/radiann-kswg/RasPiOS_UnityConsole) がそう）。
その場合、ゲームのプロセスは arm64 の libgpiod などのネイティブライブラリを読み込めない。
そこで GPIO の実処理は arm64 ネイティブのデーモンに任せ、ゲーム側はソケットで命令を送るだけにする。

- ゲーム側の SDK は純 C# なので、box64 の有無・Mono / IL2CPP を問わない。
- ゲームが強制終了（SIGKILL）されても、デーモンが切断を検知して出力を安全な状態に戻す。
- 同じプロトコルを TCP に載せれば、Windows の Unity Editor から実機の GPIO を動かせる。

## v1 の機能

| 機能 | 内容 |
| --- | --- |
| デジタル入出力 | BCM 番号で任意のピン。入力はプルアップ/ダウンとエッジイベント付き |
| ハードウェア PWM | GPIO12 / GPIO13 の 2ch。サーボ用ヘルパー（補正値付き） |
| I2C | I2C1（GPIO2/3）への生のバイト読み書き |
| RGB LED | 3 ピン指定。通常ピンはオン/オフ、PWM ピン（最大 2ch）は明るさ可変 |
| Editor 開発 | 模擬モード（実機不要）と遠隔モード（TCP で実機のデーモンへ接続） |

v2 以降の予定は [docs/requirements.md](docs/requirements.md#6-ロードマップ) を参照。

## ドキュメント

- [docs/requirements.md](docs/requirements.md) — 要件定義（確定事項と v2 以降の予定）
- [docs/architecture.md](docs/architecture.md) — 構成、ライセンス境界、他の OS への導入方法、ピン配置
- [docs/protocol.md](docs/protocol.md) — デーモンと SDK の間の通信プロトコル（v1 草案）

## リポジトリ構成

```
LICENSE                      MIT
docs/                        設計ドキュメント
daemon/unitygpiod.py         デーモン本体（Python 3 + python3-libgpiod v2。PWM は sysfs、I2C は ioctl）
daemon/unitygpiod.service    systemd ユニット（専用ユーザー unitygpio、gpio/i2c グループ）
daemon/unitygpio.conf        設定例（/etc/unitygpio.conf）
daemon/unitygpio-config      I2C / PWM を config.txt で有効化する CLI
daemon/install.sh            導入スクリプト（pi-gen の on_chroot では --no-start）
daemon/test_unitygpiod.py    自己テスト（実機不要）
unity/                       UPM パッケージ net.numbertales-radiann.unitygpio
unity/Runtime/               Gpio.cs（入口・接続・メインスレッド配送）, Handles.cs, Transport.cs, UnityGpioSettings.cs（Editor 専用設定）
unity/Tests/Editor/          模擬モードの EditMode テスト
```

### Pi 側の導入

```sh
git clone https://github.com/radiann-kswg/RasPiLib_UnityGpio.git
sudo RasPiLib_UnityGpio/daemon/install.sh
sudo unitygpio-config pwm on     # 必要なら。i2c も同様。反映は再起動
```

予約ピンは `/etc/unitygpio.conf` の `reserved`。手で叩くなら
`echo '{"id":1,"op":"hello","proto":1}' | socat - UNIX-CONNECT:/run/unitygpio/unitygpio.sock`。

### Unity 側の導入

Package Manager の「Add package from git URL」に
`https://github.com/radiann-kswg/RasPiLib_UnityGpio.git?path=unity` を指定する。

```csharp
using RasPiLib.UnityGpio;

var button = Gpio.OpenInput(17, Bias.PullUp, Edge.Both, debounceMs: 10);
button.Changed += v => Debug.Log($"button {v}");       // メインスレッド (Update の前)
var lamp = Gpio.OpenOutput(27);
lamp.Value = true;                                       // 送るだけ
var servo = new Servo(Gpio.OpenPwm(12, 50f)) { Angle = 90f };
var rgb = Gpio.OpenRgbLed(13, 22, 23);  rgb.Color = Color.cyan;
byte[] id = await Gpio.OpenI2c(1, 0x76).WriteReadAsync(new byte[] { 0xD0 }, 1);
```

- デーモンが無い環境でも例外は出ない（`Gpio.IsAvailable` が false、操作は無視＋警告ログ）。
- Editor では **Project Settings > RasPiLib UnityGpio** で模擬モード / 遠隔モードを選ぶ。
  模擬モードの入力は `Gpio.Mock.SetInput(pin, value)`、I2C は `Gpio.Mock.I2cHandler` で差し込む。
- Linux プレイヤーは `/run/unitygpio/unitygpio.sock` に接続する。環境変数 `UNITYGPIO_SOCKET` /
  `UNITYGPIO_TCP=127.0.0.1:8787` で切り替えられる（box64 上で Unix ソケットが使えない場合の逃げ道）。

### 検証

```sh
python3 daemon/test_unitygpiod.py                  # デーモンの純ロジック
unity test <テスト用プロジェクト> --mode EditMode   # manifest.json の testables にパッケージを入れておく
```

## 商標

「Unity」は Unity Technologies、「Raspberry Pi」は Raspberry Pi Ltd の商標です。
本プロジェクトはいずれとも関係のない非公式のプロジェクトです。
