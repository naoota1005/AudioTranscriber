# AudioTranscriber

Windowsの内部音声を録音し、Whisperでローカル文字起こしするデスクトップアプリです。

## 主な機能

- Windowsの再生音を録音。対応するWindows 11環境では音量0・ミュート中も取得できます。
- WhisperとSilero VADによるローカル文字起こし。音声・文字起こし内容・用語集を外部へ送信しません。初回はモデルのダウンロードが必要です。
- CUDA → Vulkan → CPUの順でバックエンドを切り替えます。
- 30分以後の2秒無音で分割。無音が来なければ最大120分で強制分割します。
- TXTの時系列結合と、保存済み音声からのクラッシュ復旧に対応します。
- 診断用の音声はWindows標準音声合成で一時生成します。録音音声ファイルはリポジトリに含めません。

Windows音声合成を利用できない環境では、自動の「動作テスト」は実行できません。通常の録音・文字起こしには音声合成を必要としません。今回の音声合成への変更はビルド確認済みですが、この実行環境ではSAPIの音声列挙がエラーとなり、録音から認識までの再検証は完了していません。

開発用のWindows 11 / Core Ultra 7クラスのCPU / NVIDIA RTX 4060搭載機では快調に動作しています。速度はPC構成やドライバー、モデルにより異なります。

## ソースから起動する

Windows 11 x64と.NET 8 SDKのWindowsデスクトップ開発環境が必要です。

リポジトリを取得し、ルートでPowerShellから次を実行します。

```powershell
.\build.ps1 -Publish
.\outputs\AudioTranscriber\AudioTranscriber.exe
```

アプリと文字起こしworkerをまとめて出力します。初回録音時にWhisperモデルとVADを取得します。標準モデルのダウンロードは約1.62GBです。GPUドライバーや追加実行ライブラリが不足する場合はCPUへfallbackします。

`AudioTranscriber.sln`をVisual Studioで開くこともできます。詳細な構成は[技術資料](docs/README-technical.md)、過去の検証記録は[検証結果](docs/TEST-RESULTS.md)を参照してください。

## ライセンス

アプリのコードは[MIT License](LICENSE)です。利用、改変、再配布、商用利用が可能です。依存ソフトウェアとモデルにはそれぞれのライセンスが適用されます。通知は[docs/licenses](docs/licenses/)に記載しています。

録音・文字起こしは、利用する権利のある音声に限ってください。
