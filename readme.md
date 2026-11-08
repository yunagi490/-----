# KyoshinMonitor

JQuake風の強震モニタ (C# / WinForms / .NET 8)。

## 機能

- 強震モニタ: 防災科研の配信画像(1秒更新)をベース地図に重ねて表示
- 緊急地震速報(EEW): Wolfx の WebSocket で受信し、パネル表示・通知・警告音。EEW中は予測震度とP/S波の円も重ねて表示
- 地震情報: P2P地震情報 (履歴 + WebSocket) を一覧表示。震度4以上で通知
- WebSocketは切断時に自動再接続 (1秒 → 最大60秒)

## 実行

```
dotnet run
```

## データソース

| 用途               | 取得先                                                                                                |
| ------------------ | ----------------------------------------------------------------------------------------------------- |
| 強震モニタ最新時刻 | `http://www.kmoni.bosai.go.jp/webservice/server/pros/latest.json`                                     |
| 強震モニタ画像     | `http://www.kmoni.bosai.go.jp/data/map_img/...` (ベース地図 / RealTimeImg / EstShindoImg / PSWaveImg) |
| EEW                | `wss://ws-api.wolfx.jp/jma_eew` (非公式の無料API)                                                     |
| 地震情報           | `https://api.p2pquake.net/v2/history`, `wss://api.p2pquake.net/v2/ws`                                 |

## 注意

- 防災科研の強震モニタ画像は非公式な利用方法です。アクセス間隔(現状1秒)を詰めすぎないでください。
- EEWはWolfxの非公式配信です。実際の避難判断には、気象庁・自治体などの公式情報や公式アプリを使ってください。
- 画像の取得URL形式は変更される可能性があります。
