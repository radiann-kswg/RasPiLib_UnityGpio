# RasPiLib_UnityGpio

Raspberry Pi 4B / 5 の GPIO を **Unity 6.3 LTS 製の Linux ゲーム**から使うためのライブラリ。
Pi 上で動く常駐デーモン（`unitygpiod`）と、Unity 用の純 C# パッケージ（UPM）の 2 つで構成する。

> **状態: 設計段階（2026-09-26 に要件定義）。コードはまだ無い。**
> 実装は次のセッション以降で行う。

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

## リポジトリ構成（予定）

```
LICENSE              MIT
docs/                設計ドキュメント
daemon/              unitygpiod（Python 3）・systemd ユニット・設定例・install.sh・unitygpio-config
unity/               Unity パッケージ（UPM）。Runtime/ と Editor/
```

Unity への導入（実装後）: Package Manager の「Add package from git URL」に
`https://github.com/radiann-kswg/RasPiLib_UnityGpio.git?path=unity` を指定する。

## 商標

「Unity」は Unity Technologies、「Raspberry Pi」は Raspberry Pi Ltd の商標です。
本プロジェクトはいずれとも関係のない非公式のプロジェクトです。
