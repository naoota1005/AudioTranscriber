# AudioTranscriber 技術資料

## 構成

- Windows 11 x64 / .NET 8 WPF、self-contained配布。
- NAudio 2.2.1は維持。Windowsの`ActivateAudioInterfaceAsync`に`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`と`PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE`を指定し、自己PIDツリー以外の再生音を取得。48kHz stereo PCM16を既存の正規化処理へ渡す。マイクは開かない。
- Process Loopbackが失敗した場合は従来のMultimedia render endpoint loopbackへfallback。fallback理由・取得方式をログと画面へ明示。旧方式は完全ミュート対応として扱わない。
- Whisper.net 1.9.1 (whisper.cpp) を別プロセスで実行。CPU / CUDA 12 / Vulkanのnative runtimeを同梱。
- Python、システムPATHの変更、既存のWhisper環境は不要。GPUドライバーはWindows側の既存ドライバーを利用。
- 認識はすべてローカル。HTTPS通信は不足したWhisperモデル・VADのダウンロードだけ。音声、文字起こし、用語集、ログを送信しない。

## 保存・クラッシュ耐性

通常データの基点は `%LOCALAPPDATA%\AudioTranscriber`。

| 内容 | 既定の場所 |
|---|---|
| 設定 | `settings.json` |
| PCM / WAV | `audio\<session-id>\recording_NNN.wav[.pcm]` |
| 一時データ | `temp` |
| モデル | `models`（同梱modelsがあれば利用） |
| セッションjournal・中間TXT/JSON | `sessions\<session-id>` |
| ログ | `logs\app_yyyyMMdd.log` |
| 完成TXT | Windowsの「ドキュメント」内の`AudioTranscriber`（設定変更可能） |

セッションとチャンク開始情報を先に原子的に保存し、PCMを逐次追記。2秒相当ごとに`Flush(true)`。WAVは確定時に別ファイルへ書き、flush後rename。中間結果と完成TXTも同様。電源断ではストレージ・OSの保証範囲に依存し、最後の約2秒は失われ得る。過去にflush済みのPCMと確定チャンクを優先する。

起動時、未完了セッションを列挙。「復旧」は録音自体を再開する機能ではなく、保存済みPCMをWAVに再構築し、未処理チャンクの文字起こしを再開する。ヘッダのないPCMや奇数長PCMも完全な16bitサンプルまで救出する。セッションJSON自体の破損、保存先消失、ディスク障害は完全復旧を保証しない。

音声自動削除は完成TXTのflushと完成journal保存の後だけ。既定は保持。中間テキストとセッションは残す。

## 時刻・スレッド

WASAPI取得専用スレッドはnative packetのコピーだけ。QPCの100ns時刻をStopwatch基点に変換。正規化・ディスク処理は別スレッド。Whisperはさらに別プロセス、BelowNormal優先度、CPUは最大8スレッド。

パケットなしの期間は300msの猶予後、単調クロックに追従する無音PCMで補完。入力の時刻誤差は10ms以内のみ吸収。大きな欠落は無音として保持。WDL resamplerで16kHz mono PCM16へ変換。サンプルを20ms程度のブロックでRMS評価。

取得キューは3000パケット。上限到達時は黙って破棄せずエラー停止し、保存済みPCMを保持。巨大な音声キューをメモリに溜めない。

既定出力は1秒間隔で照合し、無効化例外も捕捉。再接続中は無音を補完。旧・新デバイス名と時刻をログへ記録。実機の物理抜去/接続パターン全種は未検証。

取得スレッドで`SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)`。finallyで解除。プロセス終了時はWindowsがスレッドの要求を破棄する。明示的な手動スリープ、蓋を閉じた強制スリープ、電源断を防ぐ機能ではない。

## 分割と認識

- 30分以後、連続2秒の無音で分割。
- 新規設定の最大値は120分で強制分割。直前3秒を次チャンクの先頭へコピー。既存JSONの3600秒は保持し、欠けているフィールドのみ7200秒が既定値になる。
- 連続5分無音で自動停止。最後の現在チャンクは最後の有音まで切り詰める。既に確定したチャンク内の末尾無音はVADで除外する。
- 確定チャンクをsingle-reader queueで順次認識。録音終了を待たずに開始。
- 録音開始前にモデルの取得と整合性確認を行う。バックエンドの初期化とCUDA→Vulkan→CPU fallbackは最初の実音声チャンクの認識時に実行する。
- CUDA→Vulkan→CPU。明示CPUはCPUだけ、明示VulkanはVulkan→CPU。選択モデルで全backendが失敗したらmediumで再試行。
- GPUライブラリの存在だけで成功扱いにしない。native logでGPU model memory allocationとbackend使用を確認する。
- Silero VAD v6.2.0を通った区間だけを認識。区間位置を元WAVの時刻へ戻す。特定の語句を削除しない。
- 長いチャンクはVADで最大約28秒の発話区間へ分けて認識するため、区間境界で文脈が弱くなる場合がある。
- 重複削除は重複時間内・時刻一致・12文字以上の完全一致セグメントだけ。曖昧な重複は残す。

## モデルと取得元

モデル: <https://huggingface.co/ggerganov/whisper.cpp>
VAD: <https://huggingface.co/ggml-org/whisper-vad>
SHA-256は公式配布元のLFS oid。取得したAPI情報は`model-provenance.json`と`vad-provenance.json`。サイズ・SHA-256両方を照合し、不一致は隔離。ダウンロード途中は`.download`。再試行は先頭から行う。

| プリセット | 内部モデル | ダウンロード量 |
|---|---|---|
| 高精度 | large-v3 | 約3.10 GB |
| 標準 | large-v3-turbo | 約1.62 GB |
| 軽量 | medium | 約1.53 GB |

CPUではturboの方がmediumより速いこともある。mediumを「軽量」と呼ぶのは指定プリセットへの対応であり、全PCで最速という保証ではない。

Whisper.net公式: <https://github.com/sandrohanea/whisper.net>
NAudio公式: <https://github.com/naudio/NAudio>
whisper.cpp公式: <https://github.com/ggml-org/whisper.cpp>
CUDA依存DLL配布元: whisper.cpp v1.9.2 `whisper-cublas-12.4.0-bin-x64.zip`。GitHub release APIのSHA-256 `443110ddaad70d4290ab2e77179e31cf712035bbc4fad56bb4519a90c917b39c` と照合。

## ビルドと開発テスト

`AudioTranscriber.sln`、`build.ps1`を参照。.NET 8 SDKは`work/dotnet`を優先。パッケージは固定バージョン。NuGet restoreでself-contained runtime packを取得した後、`build.ps1 -Publish`。

テスト起動オプション（一般ユーザーの操作には不要）:

- `--self-test`: 30秒/2秒/20秒設定、最大長、重複、設定永続化、無音補完、PCM復旧、リサンプル、TXT結合の回帰テスト。
- `--diagnostic`: 独立した`diagnostic-data/settings.json`を使い、Windows標準音声合成で一時生成した音声を録音・認識する。音声ファイルは配布物に含めない。音量・ミュートを読み取り、完全ミュート録音を検査する。
- `--pipeline-test`: 実録音中のバックグラウンド認識と手動停止を検証。`--play-test-audio`を独立した兄弟プロセスとして起動し、Windows標準音声合成で実行時に生成した音声を使う。録音中は音量0かつmute=trueを読み取り確認する。音量設定は変更しない。
- `--crash-seed` / `--crash-recover`: 正常終了処理を通さない別プロセス終了からの復旧。

本番の設定値をテストのために上書きしない。試験結果は別ファイル`TEST-RESULTS.md`に記録。

## 制限と未実装

- 特定アプリだけのProcess Loopback、m4a取り込み、installerは未実装。
- 3時間以上の連続実機試験、異なるGPU構成での実機試験、全種類の出力デバイス抜去試験は未実施。
- 初版のEndpoint Loopbackでは音量0・ミュート時に無音となる事例があった。v1.1のProcess Loopbackでは両方を維持した実機録音・認識に成功。保護されたDRM音声など、全再生方式を網羅して保証するものではない。
- PC負荷、ディスク残量、ドライバー、GPU VRAM使用状況によって性能が変わる。
- モデル取得中は録音を開始しない。準備完了後に赤い「録音中」となってから対象音声を再生する。

## 配布

EXEだけでは動かないため、`AudioTranscriber`フォルダ全体をコピーする。self-contained .NET、worker、native DLL、assets、モデルを含む。設定と録音は各PCのユーザープロファイルへ保存するため、USB上で設定まで持ち歩く厳密な意味のportableではなく、インストール不要の配布形態。

ライセンスは`licenses`。Microsoft .NET、VC runtime、NVIDIA CUDA再配布コンポーネントは各社のライセンスに従う。診断音声はWindows標準音声合成から実行時に一時作成し、ソースや配布物に録音ファイルを含めない。ビルド成果物はコード署名なし。
