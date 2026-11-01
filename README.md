# HDMI Capture - NDI Sender

HDMIキャプチャデバイス(またはRTSPストリーミング/既存動画ファイル/ATOM Camの録画)からの映像を取り込み、**NDI**として送出しつつ、**流星の自動検出**を行うWindowsデスクトップアプリケーション(WPF, .NET 8)。

天体撮影(流星群観測)用途として、ATOM Cam2向けPython実装 meteor-detect の検出アルゴリズムをC#/OpenCvSharpへ移植し、リアルタイムのHDMIキャプチャ・NDI配信と統合したもの。

> **出典**: 流星検出アルゴリズムは、長谷川氏がGitHub上に公開されている [meteor-detect](https://github.com/kin-hasegawa/meteor-detect) を元に移植したものです。オリジナルの実装・ノウハウに感謝します。

## 主な機能

### 映像ソース(4モード)

アプリ上部のラジオボタンで切り替える。いずれのモードもプレビュー表示・流星検出・NDI送出を共通のパイプラインで扱う。

| モード | ソース | 実装クラス |
|---|---|---|
| キャプチャモード | HDMIキャプチャデバイス(USB video等、MSMFバックエンド) | [`OpenCvVideoCapture`](HDMICapture.Core/VideoCapture.cs) |
| ストリーミングモード | RTSP URL(ATOM Cam2等のネットワークカメラ) | [`StreamingVideoCapture`](HDMICapture.Core/StreamingVideoCapture.cs) |
| ファイルモード | 既存の動画ファイル(指定した撮影開始日時から) | [`FileVideoCapture`](HDMICapture.Core/FileVideoCapture.cs) |
| ATOMCamモード | ATOM Camの録画フォルダ(`yyyyMMdd/hh/mm.mp4`)を日時順に連結再生 | [`AtomCamVideoCapture`](HDMICapture.Core/AtomCamVideoCapture.cs) |

ファイルモード/ATOMCamモードは実時間を待たずに高速で読み進め、フレームのタイムスタンプは実時刻の代わりに「開始日時 + フレーム番号/fps」から算出することで、既存動画の一括処理(流星の後解析)に使える。

### NDI映像・音声送出

[`NdiSender`](HDMICapture.Core/NdiSender.cs) がNewTek NDI SDK(NDILibDotNet2経由)を使い、キャプチャした映像をNDIソースとしてネットワークに配信する。音声は[`AudioCapture`](HDMICapture.Core/AudioCapture.cs)(WASAPI/NAudio)でキャプチャデバイスに紐づく録音デバイスを取り込み、インターリーブ→プレーナー変換のうえ送出する(オン/オフ切替可)。

### 流星自動検出

[`MeteorDetector`](HDMICapture.Core/MeteorDetector.cs) が meteor-detect(Python版 `atomcam.py`)の `brightest()`/`diff()`/`detect()` を移植したコアロジック。

- 指定した露出時間(1〜5秒)ごとにフレームをバッファリングし、比較明合成・フレーム間差分・輝度ストレッチ・Canny・HoughLinesPで直線状の光跡を検出
- 検出線が「1本の直線らしいか」(StraightnessRatio)、「露出内の一部フレームにしか現れない一時的な信号か」(建物などの静止構造物を除外)、「見かけの角速度が十分速いか」(飛行機・衛星などの低速移動体を除外)の3条件で流星候補を判定
- ユーザー指定のマスク画像 + 映像外周の除外帯で、常時写り込むノイズ源を検出対象から除外
- 検出のたびに比較明合成写真(`_synth.jpg`)・検出線を赤で重ねた差分画像(`_diff.jpg`)・タイムコード焼き込み動画(`_movie.mp4`)を出力先フォルダへ保存し、[`MeteorDetectionListStore`](HDMICapture.Core/MeteorDetectionListStore.cs)が`detections.json`に記録を追記する


### 検出レビューパネル

画面右側に開閉できる検出一覧パネルがあり、`detections.json`をもとに検出結果をレビューできる。

- 比較明合成/差分/動画のプレビュー切替
- **誤検知として削除**: 対象ファイルを`_del`フォルダへ移動し、リストから除外。Shift/Ctrlで複数選択して一括削除でき、右クリックメニュー・Deleteキー・ボタンのいずれからも実行できる。削除後は次の項目が自動で選択される
- **飛行機の疑い表示**: 連続した3つ以上の露出で検出が続いた場合(前の露出開始から次の露出開始までの間隔が露出時間の1.5倍以内)、飛行機の可能性が高いとして一覧の該当行を黄色で表示する。同時にプレビューのオーバーレイの検出件数も黄色になり、連続が途切れると元の色(白)に戻る。判定は検出が時間的に連続しているかだけで行い、線の位置や向きは見ていない([`MeteorDetectionController`](HDMICapture.Core/MeteorDetectionController.cs)の`AircraftStreakThreshold`・`ConsecutiveGapFactor`で調整)
- **確認済み**: 各行のチェックボックスで確認済みにすると、行が緑で表示され、削除できなくなる(状態は`detections.json`に保存)。複数選択した行のチェックを操作すると、選択中の全行に反映される。色の優先順位は緑(確認済み)、黄(飛行機の疑い)、通常の順
- 新しい流星が検出されても、選択中の項目とプレビューは変わらない(一覧は差分更新される)
- **マスク追加**: 差分画像上の検出線(赤)部分を抽出し、既存マスクに合成した新しいマスク画像を生成([`MaskEditor`](HDMICapture.Core/MaskEditor.cs))。以後の検出でその領域を除外できる
- **採番出力**: 一覧の並び順を通し番号として、比較明合成・差分・動画の全件に番号を焼き込んで別フォルダ(`_num`)へ一括書き出し([`NumberedExporter`](HDMICapture.Core/NumberedExporter.cs)、[`NumberedExportWindow`](HDMICapture.App/NumberedExportWindow.xaml.cs))。OSインストール済みの実フォント(游ゴシック等)をGDI+で描画するため、番号のフォント・サイズ・余白を設定ダイアログでプレビューしながら調整できる

### オーバーレイ表示

[`OverlayRenderer`](HDMICapture.Core/OverlayRenderer.cs)が映像右下に検出件数・時刻などをOpenCV内蔵フォント(Hershey系)で焼き込む。フォント種類・サイズ倍率・左右マージンをUI下部のスライダーで調整可能。

### 設定の永続化

UIの各種設定(NDI名、露出秒数、マスクパス、オーバーレイ設定、採番出力フォント等)は[`AppConfig`](HDMICapture.App/AppConfig.cs)経由で`%AppData%\HDMICapture\settings.json`へ自動保存され、次回起動時に復元される。

## プロジェクト構成

```
HDMI_CAPTURE/
├── HDMICapture.App/       WPFアプリ本体(UI・設定・エントリポイント)
│   ├── MainWindow.xaml(.cs)         メイン画面(4モード切替・プレビュー・検出パネル)
│   ├── NumberedExportWindow.xaml(.cs) 採番出力ダイアログ
│   ├── AppConfig.cs                 設定の読み書き(%AppData%\HDMICapture\settings.json)
│   └── App.xaml(.cs)                アプリ全体のダーク系配色リソース・未処理例外ハンドリング
└── HDMICapture.Core/      映像処理・検出ロジック(UI非依存の再利用可能な部分)
    ├── IVideoSource.cs              映像ソースの共通インターフェース
    ├── VideoCapture.cs              HDMIキャプチャ(OpenCvVideoCapture, MSMF)
    ├── StreamingVideoCapture.cs     RTSPストリーミング(再接続対応)
    ├── FileVideoCapture.cs          動画ファイル読み込み
    ├── AtomCamVideoCapture.cs       ATOM Cam録画フォルダの連結読み込み
    ├── DirectShowCapture.cs / DirectShowDeviceEnumerator.cs / DirectShowInterop.cs
    │                               DirectShow経由のキャプチャ実装(現状未使用のレガシーコード)
    ├── MeteorDetector.cs            流星検出コアアルゴリズム
    ├── MeteorDetectionController.cs 露出単位のフレームバッファリングと検出実行制御
    ├── MeteorDetectionRecord.cs / MeteorDetectionListStore.cs
    │                               検出記録(detections.json)の読み書き・誤検知削除
    ├── MaskEditor.cs                差分画像からのマスク合成
    ├── NumberedExporter.cs          採番出力(GDI+による実フォント描画)
    ├── OverlayRenderer.cs           映像への情報オーバーレイ焼き込み
    ├── NdiSender.cs                 NDI映像・音声送出
    └── AudioCapture.cs              WASAPI音声キャプチャ
```

## 動作環境・必要ソフトウェア

- Windows 11(Windows 10 1809以降でも動作する想定)
- .NET 8 SDK(`net8.0-windows10.0.19041.0`, WPF)
- [NewTek NDI SDK](https://ndi.video/for-developers/ndi-sdk/)(ランタイムがインストールされ、環境変数`NDI_RUNTIME_DIR_V6`等でパスが解決できること。未インストールでもアプリは起動するが、NDI送出は無効化される)
- HDMIキャプチャデバイス(MSMFで認識されるUVC対応機器)、またはRTSP対応ネットワークカメラ(ATOM Cam2等)

## ビルド・実行

```bash
cd HDMICapture.App
dotnet build
dotnet run
```

## 補足

- `HDMICapture.Core.csproj`はNDI SDKの.NETラッパー(NDILibDotNet2)をプロジェクト参照しているため、ビルドにはこのラッパーが別途必要。
- `HDMICapture.Core`内の`DirectShowCapture`/`DirectShowDeviceEnumerator`/`DirectShowInterop`は、現在の`MainWindow`では使用されていない(HDMIキャプチャはMSMFバックエンドの`OpenCvVideoCapture`を使用)。
- 流星と飛行機・人工衛星は見かけの移動速度が近い場合があり、アルゴリズムだけでは区別しきれないことがある。最終的な判定は検出レビューパネルでの目視確認・誤検知削除を前提としている(連続検出による黄色表示は目安であり、判定の補助)。

## ライセンス

このプロジェクトは[MITライセンス](LICENSE)のもとで公開されています。
