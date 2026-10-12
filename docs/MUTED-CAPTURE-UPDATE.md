# v1.1 — 完全ミュート録音・120分上限

## 変更範囲

録音入力だけをProcess Loopback優先へ変更。既存NAudio 2.2.1、リサンプル、逐次PCM保存、無音判定、チャンク順序、Whisper/VAD、CUDA→Vulkan→CPU、復旧、TXT結合と保守的重複除去は維持。

- API: `ActivateAudioInterfaceAsync` / `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`
- 仮想デバイス: `VAD\Process_Loopback`
- 通常録音: `PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE`、対象はAudioTranscriber自身のPID。その他プロセスの再生音を捕捉。
- 診断: `INCLUDE_TARGET_PROCESS_TREE`で自己プロセスの同梱音声を取得。通常録音の除外動作とは別に明示。
- fallback: 既存WASAPI endpoint loopback。理由・方式をログ、方式をメイン画面に表示。完全ミュート動作を保証しない方式であることを表示。
- マイク、VB-CABLE、音量を上げる処理、ミュート解除、OS設定変更は使用しない。初版の開発診断にあった音量変更コードも削除。

## 実機結果

Windows 11実機で確認。開発端末の詳細な型番や接続デバイス名はここには記載しない。

音量0・mute=trueが既に設定されていたため、そのまま変更せず実行。テスト音声は開発時に使用したもので、公開リポジトリや配布物には含めない。

- 音量0継続・mute=true継続を読み取り確認。
- Process Loopbackが有効、Endpoint fallbackなし。
- 有音のWAVを2本生成（16kHz mono PCM16）。
- 録音継続中に最初のチャンクをWhisperで認識。
- 手動停止後、時刻順のUTF-8 TXTを生成。サンプルの発話を認識。
- WAVのピーク値は25506、RMSは約−18.04 / −20.15 dBFS。無音ファイルではないことを数値でも確認。
- GUIの「動作テスト」と同じ診断経路も完走。135.1秒・5 WAV、30秒＋2秒無音分割、20秒無音自動停止、Whisper/VAD認識とTXT保存に成功。全録音中の音量0・ミュート継続を確認。
- 診断でCUDA、CPU単独、Vulkan優先の各認識に成功（実backendもそれぞれ一致）。
- 外部クラウド認識やYouTube再生は使用していない。別Windowsプロセスによるローカル音声再生で検査した。

音量0とミュートを**同時に有効**にした条件での成功である。ミュート解除が禁止されているため、音量0・ミュート無効の独立条件は試していない。実スピーカーの音をこちらで聴くことはできず、聴覚確認はOS状態の確認とは区別する。

## 120分と回帰テスト

- 新規設定の最大チャンク長: 7200秒。
- 保存済み3600秒の設定: 読み込み後も3600秒を維持。
- 30分、2秒、5分、-45dBFS、日本語、large-v3-turbo、自動backendの既定値は変更なし。
- テスト専用値30秒・2秒で自然無音分割を検証。通常分割のoverlap=0を検査。
- 30/120分を3/12秒へ短縮したテストで、12秒になるまでは強制分割せず、12秒で分割することを検証。
- 強制分割の3秒overlapをバイト単位で検査。
- 従来のPCM復旧・無パケット補完・設定永続化・リサンプル・時刻順TXT結合・安全な重複除去テストを再実行。

本番設定は書き換えていない。実時間120分の待機、3時間連続録音、異なるGPU機種、DRM保護音声、全デバイス切断パターンは未検証。

## 復帰点

安定版タグ: `stable-before-muted-capture-20260916` → `9923958`。
取得方式、120分の既定値、診断更新は別コミット。旧版配布フォルダは上書きせず、更新版を`AudioTranscriber-v1.1`へ出力。

## 公式資料

- [Microsoft Application Loopback sample](https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/ApplicationLoopback)
- [ActivateAudioInterfaceAsync](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-activateaudiointerfaceasync)
- [PROCESS_LOOPBACK_MODE](https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ne-audioclientactivationparams-process_loopback_mode)

API選択は公式資料に基づく。ミュート時の実動作は上記のWindows実機試験に基づく。
