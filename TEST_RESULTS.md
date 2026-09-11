# TEST_RESULTS.md — テスト結果

最終更新: 2026-09-10 / 対象ブランチ: `claude/meeting-capture-remove-realtime-stt-tg6hhs`

自動テスト合計 **237 件**（Core 199 / Stt 13 / Audio 15 / App 10）。すべて GitHub Actions の
`windows-latest` 上で成功しています（実行記録:
[run #57](https://github.com/chihirohashimotoac-coder/Meeting-Capture/actions/runs/34430878293)、
commit `f7232d4`）。

このうち **3 件は実際の音声デバイスを開いて実測**します（2.11）。CI では
サウンドカードの代役として仮想オーディオデバイスを導入しており、
**WASAPI ループバックが再生中の音を実際に取得することを毎ビルド確認**しています。

このうち 2 件（`RealModelRecognitionTests`）は**実際の whisper.cpp と実モデル**を使う
テストで、モデルと音声ファイルが揃った専用ステップで再実行されます。CI では
`MEETINGRECORDER_REQUIRE_REAL_STT=1` を設定しているため、**フィクスチャが無ければ
スキップではなく失敗**します（テストが黙って実行されなくなる事故を防ぐため）。

### この改修（録音優先化）で何を検証したか

| 要求 | 検証方法 |
| --- | --- |
| 録音中にWhisperを動かさない | `RecordingPipeline` / `MeetingSessionManager` の公開APIに認識器・話者分離器を渡す口が無いことをリフレクションで検査（2.4）。加えて、録音中〜停止後を通じて文字起こしが空のままであることを実録音で確認 |
| 録音停止で自動文字起こししない | `StoppingDoesNotStartATranscription`（2.4） |
| ゲート／AGC／コンプ／リミッターの削除 | **アセンブリ内に型が存在しないこと**と、設定に該当プロパティが無いことを検査（2.1） |
| 固定ゲインの一定性 | 非ゼロサンプルの `output[n]/input[n]` が一定であることを検査（2.1） |
| クリッピングしない／ハードクリップしない | 目標ピーク超過が無いこと、および出力が `入力 × gain` と16bit量子化3ステップ以内で一致することを検査（2.1） |
| インポート元ファイルを変更しない | バイト列と更新日時の一致を検査（2.12） |
| インポート音声で話者を推測しない | 全セグメントが `インポート音声` で `SpeakerId` / `SpeakerName` が null であることを検査（2.4） |
| 認識音声の削除条件 | 完了／中止／失敗 × 設定ON/OFF の6通りを検査（2.13） |
| 古い設定ファイルで落ちない | 旧版が書いた settings.json を読み込ませて検査（2.14） |

---

## 0. 読み方（重要）

`Environment` 列は次のいずれかです。

| Environment | 意味 |
| --- | --- |
| **GitHub Actions / windows-latest** | CI（`windows-latest`）上で自動実行され、成功が記録されています |
| **Windows physical machine** | 実機で人間が実行して確認しました |
| **Not tested** | まだ実行していません |

**GitHub Actions の Windows ランナーには、実マイク・実スピーカー・実オーディオ
セッション・会議アプリケーションが存在しません。したがって
「CI ビルド成功」＝「WASAPI 実機動作確認済み」ではありません。**

**2026-09-08 に実機での 1 回目の確認を実施しました**（詳細は 2.17）。
これにより 11 項目が `Windows physical machine / PASS` になり、
**本製品の中核である WASAPI ループバックが実サウンドカードで動作することが
確認されました（T-09）。** 全 53 項目のうち、残る 42 項目は引き続き `Not tested` です。
手順は [`docs/WINDOWS_E2E_TEST.md`](docs/WINDOWS_E2E_TEST.md) にあります。

分類:

- **A. GitHub Actions 上で検証済み** — CI で実際に実行され成功したもの
- **B. 自動テストで代替検証済み** — 実デバイスの代わりに合成信号／テストダブルで検証したもの
- **C. Windows 実機確認が必要** — CI では原理的に検証できないもの

---

## 1. A. GitHub Actions 上で検証済み

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| ソリューション全体の restore | GitHub Actions / windows-latest | PASS | 10 プロジェクト |
| Release ビルド（全プロジェクト） | GitHub Actions / windows-latest | PASS | WPF アプリを含む。**Linux では WPF をビルドできないため、アプリ本体のコンパイル確認は CI が唯一の手段です** |
| `MeetingRecorder.Core.Tests`（199 件） | GitHub Actions / windows-latest | PASS | 音声処理・永続化・パイプライン・インポート・設定移行・話者分離・議事録 |
| `MeetingRecorder.Stt.Tests`（13 件） | GitHub Actions / windows-latest | PASS | モデルダウンロード整合性・認識器契約・実モデル認識 |
| `MeetingRecorder.Audio.Tests`（15 件） | GitHub Actions / windows-latest | PASS | うち 3 件は実オーディオデバイスを開いて実測（2.11）、4 件は実コーデックでのインポート（2.12） |
| `MeetingRecorder.App.Tests`（10 件） | GitHub Actions / windows-latest | PASS | 全ウィンドウの XAML ロード＋データバインド検証 |
| 自己完結型 publish（win-x64） | GitHub Actions / windows-latest | PASS | `--self-contained true` |
| publish 出力の検証 | GitHub Actions / windows-latest | PASS | `MeetingRecorder.exe` / `hostfxr.dll` / `coreclr.dll` / `PresentationFramework.dll` / whisper ネイティブの存在確認 |
| AIモデルが配布物に混入しないこと | GitHub Actions / windows-latest | PASS | `*.gguf` / `ggml-*.bin` があればビルド失敗 |
| ZIP 生成 | GitHub Actions / windows-latest | PASS | `MeetingRecorder-win-x64.zip` |
| ZIP の内容検証 | GitHub Actions / windows-latest | PASS | 展開せずに `MeetingRecorder.exe` の存在を確認 |
| Artifact アップロード | GitHub Actions / windows-latest | PASS | Artifact 名 `MeetingRecorder-win-x64` / 76,843,081 バイト。ZIP 73.5 MB・345 エントリ（展開後 169.4 MB） |
| llama.cpp の命令セット別レイアウト保持 | GitHub Actions / windows-latest | PASS | noavx/avx/avx2/avx512 の4種が存在し、ルートに平坦化された `llama.dll` が無いことを検証 |
| AIモデルの SHA-256 実測 | GitHub Actions / ubuntu-latest | PASS | 全 6 モデルをダウンロードしてハッシュを取得し、カタログにピン留め |
| ワークフローの静的検査 | GitHub Actions / windows-latest | PASS | `tools/check_workflows.py`。`shell: powershell` ステップに非ASCII文字が入るとビルド失敗（PowerShell 5.1 のANSI誤読対策） |
| PowerShell 補助スクリプトの構文検査 | GitHub Actions / windows-latest | PASS | `tools/Verify-NoNetwork.ps1` をパーサーに通す |
| **実モデルのダウンロードとハッシュ照合（毎ビルド）** | GitHub Actions / windows-latest | PASS | `ggml-tiny-q5_1.bin` を実取得し、ピン留めした SHA-256 と一致することを確認 |
| SAPI による音声合成 | GitHub Actions / windows-latest | PASS | 213,486 バイトの英語音声を生成（16kHz/mono） |
| **実 whisper.cpp による音声認識** | GitHub Actions / windows-latest | PASS | 下記 2.9 に認識結果を記載 |
| **パッケージ済み実行ファイルの自己診断** | GitHub Actions / windows-latest | PASS | publish 出力の `MeetingRecorder.exe --diagnose` を起動し、11 チェックが実行されることを確認（詳細は 2.10） |
| 仮想オーディオデバイスの導入（CI治具） | GitHub Actions / windows-latest | PASS | 失敗してもビルドは継続し、その場合はループバック検証を「未実施」と報告します |
| **WASAPI ループバックによる実音声取得** | GitHub Actions / windows-latest | PASS | 再生したトーンを実際に取得（詳細は 2.11） |
| **実デバイスを通した実録音** | GitHub Actions / windows-latest | PASS | 実 `RecordingPipeline` + 実 WASAPI で録音し、WAV を読み戻して検証（2.11） |
| **配布バイナリによる D-05 実測** | GitHub Actions / windows-latest | PASS | 音を再生しながら `--diagnose --seconds 6` を実行し、D-05 が Ok でなければビルド失敗 |
| ミュート（片系統・両系統・録音中の切替） | GitHub Actions / windows-latest | PASS | 無音になること、タイムラインが壊れないこと、ミュート側が文字起こしに出ないことを検査 |
| **認識器に渡す音声が DSP 前であること** | GitHub Actions / windows-latest | PASS | 正規化目標を超える信号で、認識器側は入力レベルのまま／ファイル側は 8 dB 以上低いことを検査 |
| 2パス（確定版）の窓・時刻・発言元 | GitHub Actions / windows-latest | PASS | 窓が 10 秒超かつ 30 秒以下、時刻が会議先頭からの絶対値、系統別ファイル由来の発言元を維持 |
| 確定版が話者名を引き継ぐこと | GitHub Actions / windows-latest | PASS | 別系統の名前を誤って適用しないことも検査 |
| 系統別認識音声の保存 | GitHub Actions / windows-latest | PASS | 16 kHz で書かれること、無効時には 1 バイトも書かないことを検査 |

---

## 2. B. 自動テストで代替検証済み

実デバイスの代わりに、決定論的な合成音声とテストダブルで検証しています。

### 2.1 音声処理

**この改修の中心です。** 検証の主眼は「加工が正しく効くこと」ではなく
**「加工が行われていないこと」** に変わりました。

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| **NoiseGate 型が存在しない** | GitHub Actions / windows-latest | PASS | `MeetingRecorder.Core` アセンブリを反射で走査 |
| **Compressor 型が存在しない** | GitHub Actions / windows-latest | PASS | 同上 |
| **Limiter 型が存在しない** | GitHub Actions / windows-latest | PASS | 同上 |
| **LoudnessNormalizer / AGC 型が存在しない** | GitHub Actions / windows-latest | PASS | 同上。「無効化」ではなく「不在」を検査 |
| **設定にゲート／コンプ／リミッター／AGC の項目が無い** | GitHub Actions / windows-latest | PASS | 残存プロパティから復活するのを防ぐ |
| **非ゼロサンプルのゲインが一定** | GitHub Actions / windows-latest | PASS | `output[n]/input[n]` を 20,000 サンプル以上で比較、許容 0.5% |
| **小音量部と大音量部の相対関係が不変** | GitHub Actions / windows-latest | PASS | 区間RMS比の変化 1% 以内 |
| **無音が無音のまま** | GitHub Actions / windows-latest | PASS | 中央のデジタル無音 19,200 サンプルが厳密に 0 |
| **微小な過渡成分が比例して残る** | GitHub Actions / windows-latest | PASS | 4ms / -48 dBFS のバースト（ゲートが消す典型） |
| **正規化後にフルスケールのサンプルが出ない** | GitHub Actions / windows-latest | PASS | 振幅 0.001〜0.999 の5通り |
| **ハードクリップしていない** | GitHub Actions / windows-latest | PASS | 出力が `入力 × gain` と16bit 3ステップ以内で一致、波高率も不変 |
| ブースト上限が効く | GitHub Actions / windows-latest | PASS | -80 dBFS 入力で +20 dB 上限 |
| 完全な無音は増幅しない | GitHub Actions / windows-latest | PASS | ゲイン 1.0、ピーク 0 |
| **入力側クリッピングを検出して報告する** | GitHub Actions / windows-latest | PASS | 潰れた波形は潰れたまま（修復を偽装しない） |
| 正常な音声をクリップと誤検出しない | GitHub Actions / windows-latest | PASS | 振幅 0.9 の正弦波 |
| **書き直し失敗時に元の録音が無傷** | GitHub Actions / windows-latest | PASS | ディスク満杯を模擬し、バイト列一致を確認 |
| 処理チェーンが大小の区間に同じゲインを与える | GitHub Actions / windows-latest | PASS | -34 / -4 dBFS の区間比が 1% 以内 |
| 処理チェーンが DC オフセットを除去 | GitHub Actions / windows-latest | PASS | |
| 処理チェーンの遅延が 0 | GitHub Actions / windows-latest | PASS | 先読み段が無いことの裏返し |
| **2系統がフルスケールでも合計がフルスケールを超えない** | GitHub Actions / windows-latest | PASS | リミッター無しでの保証 |
| ミキサーが線形和 + 定数ゲイン | GitHub Actions / windows-latest | PASS | 全サンプルで式と一致 |
| リサンプラーの出力サンプル数 | GitHub Actions / windows-latest | PASS | 48k→16k |
| リサンプラーが帯域内信号を保持 | GitHub Actions / windows-latest | PASS | |
| リサンプラーのエイリアシング抑制 | GitHub Actions / windows-latest | PASS | 12kHz 入力を -20 dB 以下に抑制 |
| リサンプラーがブロック境界で連続 | GitHub Actions / windows-latest | PASS | 分割処理と一括処理が一致 |
| RMS / dB 変換の正確性 | GitHub Actions / windows-latest | PASS | |
| **実機での聴感・音量の妥当性** | **Not tested** | — | **C 分類。実機確認が必要** |

### 2.2 同期・バッファ

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| **バースト配信でも録音が途切れない** | Ubuntu / GitHub Actions | PASS | 実機と同じ100ms単位のバースト配信＋遅延。**挿入無音 0 ms・欠落 0 箇所**（修正前は 3.3 秒中 323 ms が無音、100ms窓23個中2個が破損） |
| マイクが痩せたら警告する | Ubuntu / GitHub Actions | PASS | クロック 0.66 倍のマイクで 24% の挿入無音を検出し報告 |
| 正常な録音では警告を出さない | Ubuntu / GitHub Actions | PASS | 停止時にリングを空にする処理を欠落と誤認しない |
| リングバッファの順序保持とラップ | GitHub Actions / windows-latest | PASS | |
| コンシューマ停止時に最古を破棄しカウント | GitHub Actions / windows-latest | PASS | オーバーラン計上 |
| ドリフト補正が通常時に発動しない | GitHub Actions / windows-latest | PASS | |
| ドリフト補正が持続的な滞留にのみ発動 | GitHub Actions / windows-latest | PASS | 5 秒継続後 |
| 無音後のバースト到着をドリフトと誤認しない | GitHub Actions / windows-latest | PASS | WASAPI ループバックの実挙動を模擬 |
| ドリフト補正量が tick あたり上限内 | GitHub Actions / windows-latest | PASS | 最大 1 ms/tick |
| **実機での 30分／1時間の同期精度** | **Not tested** | — | **C 分類。T-16 / T-17** |

### 2.3 録音・保存・復旧

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| **MP3 の実ビットレートを記録する** | Ubuntu / GitHub Actions | PASS | 要求と異なる値で符号化された場合に `metadata.json` へ実値を記録し、警告を出す |
| 要求どおり符号化できたら警告を出さない | Ubuntu / GitHub Actions | PASS | |
| 旧既定の 96 kbps を 192 kbps へ引き上げる | Ubuntu / GitHub Actions | PASS | 96 は UI から選べなかったため「既定値」であって「選択」ではない |
| 手動で設定した値は変更しない | Ubuntu / GitHub Actions | PASS | 320 kbps はそのまま |
| WAV ラウンドトリップ（16bit量子化のみの誤差） | GitHub Actions / windows-latest | PASS | |
| フラッシュ後のヘッダが正しい（強制終了模擬） | GitHub Actions / windows-latest | PASS | 未 Dispose のまま読み出し |
| データチャンク切断時も読める | GitHub Actions / windows-latest | PASS | 電源断模擬 |
| 範囲外サンプルをクランプ（ラップしない） | GitHub Actions / windows-latest | PASS | |
| 合成2ストリームが1ファイルにミックスされる | GitHub Actions / windows-latest | PASS | 合成キャプチャによるE2E |
| 片方のデバイスが開けなくても録音継続 | GitHub Actions / windows-latest | PASS | 警告付きで継続 |
| 両方失敗時は明示的に開始を拒否 | GitHub Actions / windows-latest | PASS | |
| 無音のシステム音声でもタイムラインが継続 | GitHub Actions / windows-latest | PASS | 壁時計マスタークロック |
| 認識用音声の書き込み失敗でも録音が継続 | GitHub Actions / windows-latest | PASS | 文字起こしを諦めても録音は諦めない |
| **系統別の認識用音声が両方保存される** | GitHub Actions / windows-latest | PASS | 16kHz / モノラル、両系統に信号あり |
| **文字起こし機能オフのときは保存しない** | GitHub Actions / windows-latest | PASS | 毎時115MB×2 を勝手に使わない |
| **ミュートした系統は認識用音声にも入らない** | GitHub Actions / windows-latest | PASS | あとから復元されない |
| **認識用音声が加工されていない** | GitHub Actions / windows-latest | PASS | 既知レベルのトーンがそのレベルで届く |
| **停止時に一定ゲインが1回だけ適用される** | GitHub Actions / windows-latest | PASS | -16.5 dBFS の録音に +15 dB、結果 -1 dBFS 付近 |
| **無音の録音は増幅しない** | GitHub Actions / windows-latest | PASS | ゲイン記録なし、ピーク 0 |
| **入力側クリッピングを録音でも報告** | GitHub Actions / windows-latest | PASS | 「復元できません」を含む警告 |
| セッション終了で全成果物が揃う | GitHub Actions / windows-latest | PASS | wav/txt/md/metadata、ジャーナル削除 |
| 2回目以降の録音でもスリープ抑制が機能する | GitHub Actions / windows-latest | PASS | 共有インスタンスを破棄しないことの検証 |
| 中断されたセッションが復旧候補になる | GitHub Actions / windows-latest | PASS | |
| ジャーナル再生（最終行破損を含む） | GitHub Actions / windows-latest | PASS | |
| クラッシュ復旧で成果物を再生成 | GitHub Actions / windows-latest | PASS | |
| 復旧しない選択でも音声を保持 | GitHub Actions / windows-latest | PASS | |
| MP3 エンコーダー不在時に WAV へフォールバック | GitHub Actions / windows-latest | PASS | テストダブル |
| 設定の保存・読込（日本語を含む） | GitHub Actions / windows-latest | PASS | |
| 破損した設定ファイルからの回復 | GitHub Actions / windows-latest | PASS | |
| 原子的書き込みで一時ファイルが残らない | GitHub Actions / windows-latest | PASS | |
| フォルダー名の生成とサニタイズ | GitHub Actions / windows-latest | PASS | Windows 禁止文字（ホスト非依存） |

### 2.4 文字起こし（録音後のみ）

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| **`RecordingPipeline.Start` が認識器・話者分離器を受け取らない** | GitHub Actions / windows-latest | PASS | 反射で引数型を検査 |
| **`RecordingPipeline` が認識器・話者分離器のフィールドを持たない** | GitHub Actions / windows-latest | PASS | 別経路で入手できないことの担保 |
| **`MeetingSessionManager` の Start / Stop も同様** | GitHub Actions / windows-latest | PASS | 上位層にも渡す口が無い |
| **録音中〜停止まで文字起こしが空のまま** | GitHub Actions / windows-latest | PASS | 3秒間 0.5 秒ごとに検査 + 停止後 |
| **停止が文字起こしを開始しない** | GitHub Actions / windows-latest | PASS | 停止は 20 秒以内に完了し、`transcript.txt` は「認識された発言はありません」 |
| 系統別音声を走査し発言元を保つ | GitHub Actions / windows-latest | PASS | マイク／PC音声の両方からセグメントが出る |
| **窓が Whisper の受容野いっぱいまで伸びる** | GitHub Actions / windows-latest | PASS | 最長窓 > 10 秒かつ ≤ 30 秒 |
| 手で付けた話者名を引き継ぐ | GitHub Actions / windows-latest | PASS | やり直しが人の作業を消さない |
| 他系統の話者名を流用しない | GitHub Actions / windows-latest | PASS | |
| 進捗が完了まで報告される | GitHub Actions / windows-latest | PASS | |
| **中止が即座に効く** | GitHub Actions / windows-latest | PASS | 決定論的に検査（時計に依存しない） |
| **インポート音声の全セグメントが「インポート音声」** | GitHub Actions / windows-latest | PASS | `SpeakerId` / `SpeakerName` がいずれも null |
| 話者分離は指定したときだけ動く | GitHub Actions / windows-latest | PASS | 既定では付与しない |
| 音声が無いときに理由を示して失敗する | GitHub Actions / windows-latest | PASS | 設定名を含むメッセージ |
| 16kHz 以外の音声を拒否する | GitHub Actions / windows-latest | PASS | 混合ファイルを黙って使わない |
| VAD が発話を検出しハングオーバー後に閉じる | GitHub Actions / windows-latest | PASS | |
| VAD が定常ノイズで誤検出しない | GitHub Actions / windows-latest | PASS | |
| VAD が 180ms の短い発話を捕捉 | GitHub Actions / windows-latest | PASS | 相槌相当 |
| チャンカーがプリロールを含める | GitHub Actions / windows-latest | PASS | 語頭欠け防止 |
| 長い発話の分割とオーバーラップ | GitHub Actions / windows-latest | PASS | 継ぎ目の語の欠落防止 |
| 無音のみでは何も発行しない | GitHub Actions / windows-latest | PASS | |
| **実モデルでの英語認識（合成音声）** | GitHub Actions / windows-latest | PASS | 実 whisper.cpp。詳細は 2.9 |
| **録音中に文字起こしを開始できない／その逆も** | — | — | **自動テストなし。** UI コマンドの実行可否条件であり、コードレビューで確認しました |
| **実モデルでの日本語認識精度** | **Not tested** | — | **C 分類。T-23。ランナーに日本語音声（SAPI日本語ボイス）が無いため CI では検証できません** |

### 2.5 モデル管理

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| SHA-256 一致時にダウンロード成功・記録 | GitHub Actions / windows-latest | PASS | スタブHTTPハンドラ |
| SHA-256 不一致でファイルを削除して失敗 | GitHub Actions / windows-latest | PASS | |
| ピン留めが無い場合に「照合済み」と偽らない | GitHub Actions / windows-latest | PASS | |
| 部分ファイルからのレジューム（Rangeヘッダ） | GitHub Actions / windows-latest | PASS | |
| カタログ外の URL を拒否 | GitHub Actions / windows-latest | PASS | |
| HTTP（非HTTPS）を拒否 | GitHub Actions / windows-latest | PASS | |
| 進捗が実バイト数で完了まで報告される | GitHub Actions / windows-latest | PASS | 偽の進捗ではない |
| 途中までのファイルを「取得済み」と扱わない | GitHub Actions / windows-latest | PASS | |
| カタログ全件が HTTPS・ライセンス明記 | GitHub Actions / windows-latest | PASS | |
| ピン留めハッシュの形式検証 | GitHub Actions / windows-latest | PASS | 64桁の16進 |
| **実際のモデルの SHA-256 照合** | GitHub Actions / ubuntu-latest | PASS | 実ファイルをダウンロードして測定・ピン留め |

### 2.6 モデル選定と性能自動調整

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| 実測スコアからモデルが選択される | GitHub Actions / windows-latest | PASS | CPU型番判定ではない |
| **十分な性能なら梯子の最上段（large-v3-turbo）を選ぶ** | GitHub Actions / windows-latest | PASS | 「間に合うか」ではなく「最も精度が高いか」で選ぶようになった |
| 自動選択が待ち時間の上限内に収まる | GitHub Actions / windows-latest | PASS | 音声長の 1.5 倍以内 |
| **上限を超えるモデルも手動では選べる** | GitHub Actions / windows-latest | PASS | 16GB で turbo が RAM 条件を満たすことも確認 |
| RAM 不足時に大型モデルを選ばない | GitHub Actions / windows-latest | PASS | |
| 極端に遅いPCでも必ず1つ選ぶ | GitHub Actions / windows-latest | PASS | 「文字起こしできない」より遅いほうがまし |
| **プロファイルにリアルタイム用の項目が残っていない** | GitHub Actions / windows-latest | PASS | チャンク長・オーバーラップ・ビーム幅・二つ目のモデルID |
| スレッド数が余裕を残す | GitHub Actions / windows-latest | PASS | |
| 自動選択は Apache-2.0 の LLM のみ | GitHub Actions / windows-latest | PASS | |
| CPU 実測プローブが動作し所要時間内 | GitHub Actions / windows-latest | PASS | |
| large-v3-turbo のサイズと SHA-256 | GitHub Actions / ubuntu-latest | PASS | 配布元から実ダウンロードして測定（2.15） |
| **large-v3-turbo が実エンジンで読み込める** | GitHub Actions / windows-latest | PASS | 実測結果は 2.15 |
| **基準PC（i5-1335U）での実処理時間・RAM** | **Not tested** | — | **C 分類。設定画面の表示は推定値であることを明示しています** |

### 2.7 話者分離・議事録

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| 同一の合成音声が同じクラスタになる | GitHub Actions / windows-latest | PASS | 合成音声 |
| 異なる合成音声が別クラスタになる | GitHub Actions / windows-latest | PASS | 合成音声 |
| クラスタ空間がストリームごとに独立 | GitHub Actions / windows-latest | PASS | |
| 短すぎる／静かすぎる音声を帰属しない | GitHub Actions / windows-latest | PASS | 誤帰属より無帰属を選ぶ |
| クラスタ数に上限がある | GitHub Actions / windows-latest | PASS | |
| MFCC のフレーム数と次元 | GitHub Actions / windows-latest | PASS | |
| FFT が離散フーリエ変換と一致 | GitHub Actions / windows-latest | PASS | 誤差 1e-6 以内 |
| 議事録が 6 セクションを必ず出力 | GitHub Actions / windows-latest | PASS | |
| 決定事項・ToDo が原文から抽出される | GitHub Actions / windows-latest | PASS | |
| 不明な担当者・期限が「要確認」になる | GitHub Actions / windows-latest | PASS | 人物名を捏造しない |
| 空の文字起こしで内容を捏造しない | GitHub Actions / windows-latest | PASS | |
| LLM モデル未取得時に抽出型へフォールバック | GitHub Actions / windows-latest | PASS | |
| **実話者音声での分離精度** | **Not tested** | — | **C 分類。T-27** |
| **実 LLM による議事録生成** | **Not tested** | — | **C 分類。T-47** |

### 2.8 UI

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| メインウィンドウの XAML ロードとバインド | GitHub Actions / windows-latest | PASS | バインドエラーがあれば失敗 |
| 設定ウィンドウ | GitHub Actions / windows-latest | PASS | |
| 初回設定ウィザード | GitHub Actions / windows-latest | PASS | |
| 話者名編集ウィンドウ | GitHub Actions / windows-latest | PASS | |
| クラッシュ復旧ウィンドウ | GitHub Actions / windows-latest | PASS | |
| モデルダウンロードダイアログ | GitHub Actions / windows-latest | PASS | 通信は行わない |
| 全チェック合格時に確認ダイアログを出さない | GitHub Actions / windows-latest | PASS | |
| 文字起こし編集・話者名の表示ロジック | GitHub Actions / windows-latest | PASS | |
| **実際の操作性・表示崩れ・IME** | **Not tested** | — | **C 分類。実機確認が必要** |

### 2.9 実エンジンでの音声認識（テストダブルではありません）

CI が Windows の SAPI で英語音声を合成し、**実際の `ggml-tiny-q5_1.bin` を実 whisper.cpp
に読み込ませて**認識させています。ここだけはフェイクを一切通していません。

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| 実モデルのロードと認識 | GitHub Actions / windows-latest | PASS | 音声 6.67 秒 → 2 スパン、処理 1.48 秒（**RTF 0.22**） |
| **製品と同じオフライン経路の全体** | GitHub Actions / windows-latest | PASS | 系統別 WAV → 窓分割 → 実推論 → セグメント。発言元は窓の出所で決まる |
| 期待語の一致 | GitHub Actions / windows-latest | PASS | `meeting, recorder, test, budget, review, monday` の **6/6** が一致 |

実際に返ってきたテキスト（CI ログからの逐語引用）:

```
This is a meeting recorder test. The quarterly budget review is scheduled for Monday.
```

**これで確認できたこと:** whisper.cpp のネイティブバイナリが自己完結型 publish から
ロードされること、窓分割がモデルへ正しく音声を渡していること、経路全体が
無音ではなくテキストを産出すること。

**これで確認できていないこと:** 合成音声であり、実マイクで録った実会議ではありません。
また日本語ではありません。**日本語の実音声での精度は T-23 のまま未実施です。**

### 2.10 パッケージ済み実行ファイルの自己診断（`--diagnose`）

ビルドしたソースではなく、**配布する ZIP の中身と同じ publish 出力の `MeetingRecorder.exe`**
を CI が起動し、自己診断を実行させています。

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| `--diagnose` が実行ファイルとして起動する | GitHub Actions / windows-latest | PASS | WinExe から `AttachConsole` で出力 |
| 11 個のチェックが実行される | GitHub Actions / windows-latest | PASS | D-01〜D-08 |
| JSON レポートが出力される | GitHub Actions / windows-latest | PASS | `--json` で機械可読 |
| 問題があれば終了コードが 0 以外 | GitHub Actions / windows-latest | PASS | ランナーではデバイス不在のため exit code 1 |
| **デバイス不在を「正常」と偽らない** | GitHub Actions / windows-latest | PASS | D-01 / D-02 を明確に「エラー」と報告 |
| 外向き TCP 接続の実測 | GitHub Actions / windows-latest | PASS | D-08 = **0 件** |

ランナー上での実際の出力（抜粋）:

```
OS            : Microsoft Windows 10.0.26100
CPU / RAM     : 4 論理コア / 16.0 GB
データ保存先  : ...\publish\MeetingRecorder-win-x64\data（ポータブルモード）
[ｴﾗｰ]   D-01  録音デバイス (マイク)          利用可能な録音デバイスが 0 件です。
[ｴﾗｰ]   D-02  再生デバイス (ループバック元)  利用可能な再生デバイスが 0 件です。
[ OK ]  D-03  空き容量  31.4 GB（WAVで約98時間分）
[未実施] D-04/05  音声取得テスト  --seconds 0 が指定されたため実行していません。
[ OK ]  D-06  実測スコア 0.81 / 推定RTF 0.34 / 選択モデル Whisper base (q5_1) / スレッド 2
[ OK ]  D-08  このプロセスが確立している外向きTCP接続は 0 件です
総合判定: エラーあり
```

**D-01 / D-02 のエラーは正しい結果です。** GitHub Actions のランナーには実マイクも
実スピーカーも存在しません。ここで「OK」と出ていたら、それこそが偽の検証です。

**D-08 が 0 件であることは、CI 環境での 1 プロセス・1 時点の実測です。**
実機での常時監視は T-45（`tools/Verify-NoNetwork.ps1`）のままです。

---

### 2.11 実オーディオデバイスでの取得・録音（仮想エンドポイント上）

GitHub のランナーにはサウンドカードが 1 枚もありません。そこで CI は
**スピーカーの代役として仮想オーディオデバイスを導入**し、そのうえで
製品と同じコードに実際に音を取得させています。

**仮想オーディオドライバは CI の治具であり、製品の依存ではありません。**
アプリは仮想ドライバも Stereo Mix も使わず、Windows が既定と報告する
再生エンドポイントをそのままループバックします。今回はそのエンドポイントが
たまたま仮想だった、という関係です。導入に失敗した場合、ビルドは継続し、
ログに `T-09 remains not tested` と出力され、**検証済みには決してなりません。**

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| ループバックが再生中の音を取得する | GitHub Actions / windows-latest | PASS | 4.05 秒再生 → 192,000 サンプル（4.00 秒）取得、peak -0.1 dBFS |
| **取得した音が「再生した音そのもの」であること** | GitHub Actions / windows-latest | PASS | Goertzel で 1 kHz と未使用の 3.3 kHz を比較 |
| 無音の再生デバイスを fault にしない | GitHub Actions / windows-latest | PASS | 無音時も 70,560 サンプル届き、fault は 0 件 |
| 実 `RecordingPipeline` での実録音 | GitHub Actions / windows-latest | PASS | 5.49 秒の WAV を生成し、読み戻してトーンの存在・長さ・非クリップを確認 |
| 配布バイナリの D-05 実測 | GitHub Actions / windows-latest | PASS | 6 秒間に 286,560 サンプル（**想定の 100%**）/ peak -0.5 dBFS |

CI ログからの逐語引用です。

```
Played 1000 Hz for 4.05 s.
Captured 192000 samples (4.00 s), peak -0.1 dBFS, RMS -3.7 dBFS.
Energy at 1000 Hz: 1.122E+004; at the unused control frequency 3300 Hz: 4.465E-006.
```

振幅だけならノイズやバッファ固着でも通ってしまうため、**再生した 1 kHz が
支配的かどうか**を、鳴らしていない 3.3 kHz と比較しています。比は約 25 億倍で、
取得したのが「再生していた音そのもの」であることは疑いようがありません。

配布する実行ファイル自身の測定結果:

```
[ OK ] D-05  PC内部音声取得テスト (WASAPIループバック)
      デバイス「CABLE Input」から 6 秒間に 286,560 サンプル受信（想定の 100%）
      / peak -0.5 dBFS / RMS -20.1 dBFS — 信号を検出しました。
```

**これで確認できたこと:** WASAPI ループバックの実装が、実際に再生中の音声を
取り落としなく（想定の 100%）取得すること。実 `RecordingPipeline` がそれを
DSP・ミキサー・WAV ライターまで通してファイルに残すこと。配布するバイナリ自身が
同じことを実測して報告すること。

**これで確認できていないこと:** 仮想エンドポイントであり、実サウンドカード・
実スピーカー・実 Bluetooth ではありません。**T-09（実機でのループバック）は
引き続き未実施です。** また CI ではマイク側が権限拒否されたため（下記）、
**マイクとPC音声の同時録音（T-15）は実演できていません。**

### 2.12 音声インポート（実コーデック）

WAV は**このアプリ自身のリーダー**で読むため、OS のコーデックに一切依存しません。
MP3 / M4A / AAC は **Windows の Media Foundation** を使うため、CI は
その場でエンコードしたファイルを実際に読み戻して検証します。

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| WAV: 16kHz モノラルの作業音声を生成 | GitHub Actions / windows-latest | PASS | 44.1kHz からの変換を含む |
| WAV: OS コーデック不要 | GitHub Actions / windows-latest | PASS | Windows N でも動作する経路 |
| MP3: 往復（エンコード→インポート） | GitHub Actions / windows-latest | PASS | 実 Media Foundation。長さ・レベルを検査 |
| M4A: 往復（AAC / MP4 コンテナ） | GitHub Actions / windows-latest | PASS | 同上 |
| `.aac`: 開けるか、理由付きで断るか | GitHub Actions / windows-latest | PASS | **下記の注記を参照** |
| **元ファイルが変更・移動・削除されない** | GitHub Actions / windows-latest | PASS | バイト列と更新日時の一致 |
| **作業音声にゲイン・フィルター・ゲートを掛けない** | GitHub Actions / windows-latest | PASS | 既知レベルが保たれ、無音は無音のまま |
| インポート音声に発言元情報が無い | GitHub Actions / windows-latest | PASS | `HasSourceAttribution` が false |
| 元パスを記録し、作業音声を作り直せる | GitHub Actions / windows-latest | PASS | 削除後の再文字起こし経路 |
| 元ファイルが消えていれば理由を示す | GitHub Actions / windows-latest | PASS | |
| 非対応形式を作成前に拒否 | GitHub Actions / windows-latest | PASS | 会議フォルダーを作らない |
| 壊れたファイルを拒否し、元は残す | GitHub Actions / windows-latest | PASS | |
| 途中で失敗しても半端な作業音声を残さない | GitHub Actions / windows-latest | PASS | ディスク満杯を模擬 |
| 既にクリップした音声を報告する（修復しない） | GitHub Actions / windows-latest | PASS | |
| **生の ADTS `.aac` ファイル** | **Not tested** | — | 下記 |
| **Windows N / KN での MP3 / M4A / AAC** | **Not tested** | — | Media Feature Pack 非導入環境が CI にありません |

> **`.aac` について正確に。** CI が作れる `.aac` は「AAC を MP4 コンテナに入れて
> 拡張子を .aac にしたもの」です。Media Foundation のエンコーダーが MP4 しか
> 出力せず、生の ADTS ストリームを生成する手段が無いためです。したがって
> **真の ADTS ファイルが読めるかどうかは CI では確認できていません。**
> テストが確認しているのは「読めるか、または理由を示して断るか」であり、
> 「黙って壊れた結果を作らないこと」です。README.md にも同じ内容を記載しています。

### 2.13 認識用音声の削除条件

| 設定 | 文字起こしの結果 | 期待 | Environment | Result |
| --- | --- | --- | --- | --- |
| ON | 正常完了 | **削除する** | GitHub Actions / windows-latest | PASS |
| OFF | 正常完了 | 残す | GitHub Actions / windows-latest | PASS |
| ON | **中止** | **残す** | GitHub Actions / windows-latest | PASS |
| OFF | 中止 | 残す | GitHub Actions / windows-latest | PASS |
| ON | **失敗** | **残す** | GitHub Actions / windows-latest | PASS |
| OFF | 失敗 | 残す | GitHub Actions / windows-latest | PASS |

加えて、**録音した音声そのものはこの処理で決して削除されない**ことを検査しています。
削除対象は 16kHz の作業音声だけです。

### 2.14 旧バージョンの設定ファイルの読み込み

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| **旧 settings.json が例外なく読める** | GitHub Actions / windows-latest | PASS | ゲート・コンプ・リミッター・チャンク長を含む実物の形 |
| 「やり直し用モデル」が文字起こしモデルになる | GitHub Actions / windows-latest | PASS | ユーザーが待つ気だったモデルを引き継ぐ |
| 系統別音声の保存設定を引き継ぐ | GitHub Actions / windows-latest | PASS | オフにしていた人はオフのまま |
| 削除設定を新しい名前へ引き継ぐ | GitHub Actions / windows-latest | PASS | |
| 旧 DSP 定数は復活せず既定値になる | GitHub Actions / windows-latest | PASS | |
| 保存し直すと現行の形になり、旧項目は消える | GitHub Actions / windows-latest | PASS | `refine` / `gate` / `compressor` / `limiter` を含まない |
| 壊れた設定ファイルでも起動する | GitHub Actions / windows-latest | PASS | 設定を失うのは不便、録音機を失うのは損失 |

### 2.15 large-v3-turbo の実検証

`build-windows.yml` を `evaluate_models` 入力付きで手動実行すると、指定した ggml
モデルを**実際にダウンロードして製品と同じデコード設定で推論**させ、
サイズ・SHA-256・読み込み時間・ワーキングセット・処理時間を出力します。

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| large-v3-turbo q5_0 のサイズと SHA-256 | GitHub Actions / ubuntu-latest | PASS | 574,041,195 バイト / `394221709c...` を実ダウンロードで測定 |
| **whisper.cpp（Whisper.net 1.8.1 同梱）が turbo 形式を読める** | GitHub Actions / windows-latest | PASS | 実モデルをロードして推論、英文テキストが返る |
| 3モデルを実エンジンで比較実行 | GitHub Actions / windows-latest | PASS | 下表。run 34432515577 |
| **基準PC（i5-1335U）での処理時間・RAM** | **Not tested** | — | CI ランナーは基準PCではありません |

#### 実測値（GitHub Actions windows-latest、2026-09-10、run 34432515577）

**これは基準PC（i5-1335U）の値ではありません。**ランナーは 4 論理コア、
アプリと同じ `SpeechRecognitionOptions.Offline(threads: 2)` で実行しています。
入力は CI が SAPI で合成した英語音声 6.67 秒（日本語ではありません）。

| モデル | ファイル | 読み込み | ロード後WS | ピークWS | 推論時間 | RTF（実測） |
| --- | --- | --- | --- | --- | --- | --- |
| `ggml-small-q5_1.bin` | 181 MiB | 0.20 s | 258 MB | 665 MB | 12.17 s | 1.82 |
| `ggml-medium-q5_0.bin` | 514 MiB | 0.55 s | 591 MB | 1,364 MB | 38.67 s | 5.80 |
| `ggml-large-v3-turbo-q5_0.bin` | 547 MiB | 0.56 s | 623 MB | 1,085 MB | 66.36 s | 9.95 |

**この RTF をそのまま実運用の目安にしないでください。** whisper.cpp は入力を
30 秒窓へパディングするため、6.67 秒の音声でも 30 秒分のエンコーダー計算が走ります。
つまり上の RTF には約 4.5 倍のパディング分が含まれます。1窓あたりの実測時間
（12.17 / 38.67 / 66.36 秒）を 30 秒で割った **0.41 / 1.29 / 2.21** が長時間音声での
RTF に近い値ですが、これは実測値ではなく上記実測からの**換算値**です。

**読み取れたこと（重要）**: このランナーでは turbo は medium より
**1.72 倍遅い**（66.36 / 38.67）。turbo は「large 系の中では速い」蒸留モデルであって、
medium より速いわけではありません。**turbo を無条件に既定にはしていません。**
ピークワーキングセットは turbo 1,085 MB < medium 1,364 MB で、16GB 環境で両方とも
動作可能です。

カタログの `RelativeCost`（small=1.0 / medium=3.6 / turbo=7.4）は
エンコーダーの計算量（層数 × 次元²）から導いた**推定値**で、
`ProfileSelector` の初期選択と設定画面の目安表示にのみ使われます。
推定比 7.4/3.6 = 2.06 に対して実測比は 1.72 でした（推定が約 20% 重め）。
安全側に外れているため定数は変更していません。ハードウェア 1 種類の
実測値に合わせて定数を書き換えるほうが、かえって誤差の原因になります。
UI 上も「実測値ではなく推定です」と明示しています。

**採用しなかった量子化**（同じ手順で測定済み、カタログには載せていません）:

| ファイル | サイズ（実測バイト） | 見送った理由 |
| --- | --- | --- |
| `ggml-large-v3-turbo-q8_0.bin` | 874,188,075 | 16GB 環境で他アプリと共存させる余裕が乏しい |
| `ggml-large-v3-turbo.bin`（f16） | 1,624,555,275 | 同上、より顕著 |

### 2.16 実デバイスで発見・修正した不具合

実デバイスを開くテストを入れた初回の実行で、製品側の不具合が 1 件見つかりました。

| 事象 | 内容 |
| --- | --- |
| 症状 | マイク権限が拒否されている環境で、**録音が一切開始できない**（PC内部音声も道連れ） |
| 原因 | Windows は列挙時ではなく `AudioClient.Initialize` で拒否する。`RecordingPipeline.Start` の `_micSource.Start()` が無防備で、例外がそのまま外へ出ていた |
| 影響 | 要件「片方が失敗しても録音を継続する」に違反。マイク権限がオフのユーザーは会議を丸ごと失う |
| 修正 | 各系統の開始を防御し、失敗はその系統を落として警告に変換。両方失敗時のみ、後始末をしてから対処付きで失敗する |
| 検証 | CI 上で実際に権限拒否が発生し、**PC内部音声のみで 5.49 秒の録音を継続**することを実測（下記） |

```
Warning: マイクの録音を開始できませんでした（Access is denied. (0x80070005 (E_ACCESSDENIED))）。
         「設定 > プライバシーとセキュリティ > マイク」でデスクトップアプリのマイク使用を許可してください。
Recorded 5.49 s to ...\meeting.wav
File: 48000 Hz, 1 ch, 5.49 s, 263638 samples.
Peak -6.9 dBFS; energy at 1000 Hz 4.398E+003 against 6.587E-006 at 3300 Hz.
```

診断レポート側も、アクセス拒否のときだけ「デバイスを接続し直す」ではなく
プライバシー設定を案内するよう修正しました（回帰テスト付き）。

**この不具合は、合成デバイスだけを使っていた間は一度も現れませんでした。**

---

### 2.17 Windows 実機での確認（2026-09-08）

**ここまでで唯一残っていた「実サウンドカードでの動作」が確認できました。**

| 項目 | 値 |
| --- | --- |
| OS | Microsoft Windows 10.0.26200 |
| CPU / RAM | 12 論理コア / 15.7 GB |
| 権限 | **一般ユーザー権限**（管理者昇格なし） |
| 実行形態 | `Downloads\MeetingRecorder-win-x64\` に展開しただけ、**ポータブルモード** |
| オーディオ | Realtek(R) Audio / Intel Smart Sound Technology（**仮想オーディオドライバなし**） |
| 総合判定 | **OK — 検査した範囲では問題は見つかりませんでした** |

配布した実行ファイルが実機で出力したレポートです（ユーザー名部分は伏せています）。

```
プロセス      : X64（一般ユーザー権限）
CPU / RAM     : 12 論理コア / 15.7 GB
データ保存先  : C:\Users\<ユーザー名>\Downloads\MeetingRecorder-win-x64\data（ポータブルモード）

[ OK ] D-01  録音デバイス (マイク)
      2 件: ステレオ ミキサー (Realtek(R) Audio) / マイク配列 (Intel SST)（既定）
[ OK ] D-02  再生デバイス (ループバック元)
      1 件: スピーカー (Realtek(R) Audio)（既定）
[ OK ] D-04  マイク音声取得テスト
      10 秒間に 480,480 サンプル受信（想定の 100%）/ peak -3.1 dBFS / RMS -26.1 dBFS
[ OK ] D-05  PC内部音声取得テスト (WASAPIループバック)
      「スピーカー (Realtek(R) Audio)」から 10 秒間に 479,520 サンプル受信（想定の 100%）
      / peak -7.8 dBFS / RMS -23.9 dBFS — 信号を検出しました。
[ OK ] D-06  実測スコア 1.34 / 推定RTF 0.41 / Whisper small (q5_1) / スレッド 6 / 話者分離 有効
[ OK ] D-07  「Whisper small (量子化 q5_1)」取得済み。SHA-256 照合済み。
[ OK ] D-08  このプロセスが確立している外向きTCP接続は 0 件です

総合判定: OK
```

**この 1 回で確定したこと**

| 確認できたこと | 根拠 |
| --- | --- |
| **WASAPI ループバックが実サウンドカードで動く（T-09）** | 実 Realtek 再生デバイスから 10 秒 / 479,520 サンプル（**取りこぼし 0**）、信号検出 |
| **Stereo Mix に依存していない（T-14）** | この実機には「ステレオ ミキサー」が**存在するのに使っていません**。再生デバイスのループバックで取得しています |
| **仮想オーディオドライバに依存していない（T-13）** | 仮想ドライバが 1 つも入っていない環境で成功 |
| **管理者権限が不要（T-03）** | 一般ユーザー権限で全 11 チェックが OK |
| **インストール不要・ポータブル動作（T-01）** | 展開しただけで実行でき、`data\` が実行ファイルの隣に作られました |
| **モデル取得とハッシュ照合が実機で成立（T-22）** | Whisper small を実機で取得し、ピン留めした SHA-256 と一致 |
| **性能実測とモデル自動選択が実機で機能** | スコア 1.34 → Whisper small / 6 スレッドを自動選択し、プロファイルを保存 |

**この 1 回では確定していないこと**

- **マイクとPC音声の同時録音（T-15）は未確認です。** D-04 と D-05 は**順番に**実行されるため、
  2 系統が同時に開かれた状態は測定していません。
- **T-45（外部送信がないこと）は未確認のままです。** D-08 の「0 件」は
  **録音していない一時点の、一プロセス**の測定にすぎません。
- 録音中の CPU / RAM、長時間録音の同期精度、Zoom / Teams / Meet の実音声、
  日本語認識精度、GUI ウィンドウの動作は、いずれも別途の確認が必要です。

---

---

## 3. C. Windows 実機確認が必要（11/47 実施済み）

以下は GitHub Actions 上では原理的に検証できません。
**推測で PASS と記載してはいけません。**

2026-09-08 の実機診断（2.17）で **T-01 / T-03 / T-04 / T-05 / T-06 / T-07 / T-08 /
T-09 / T-13 / T-14 / T-22 の 11 項目**が `PASS` になりました
（T-20 は基準PCではないため据え置き）。残りは未実施のままです。

ただし、このうち 8 項目は実機で
`MeetingRecorder.exe --diagnose --seconds 10` を **1 回実行するだけ**で
確認できるようになりました（`--json` で機械可読なレポートも出ます）。

| 診断ID | 対応する Test | 内容 |
| --- | --- | --- |
| D-01 | T-04 | マイク認識 |
| D-02 | T-05 | 再生デバイス認識 |
| D-03 | T-06, T-43 | 権限・保存先・空き容量 |
| D-04 | T-07 | マイクからの受信サンプル数とレベル |
| **D-05** | **T-09** | **WASAPIループバックからの受信サンプル数とレベル** |
| D-06 | T-20 の一部 | CPU実測とモデル自動選択 |
| D-07 | T-22 の一部 | モデルの有無と SHA-256 照合記録 |
| D-08 | T-45 の簡易版 | このプロセスの外向きTCP接続 |

**このマッピングは「実行方法が用意された」という意味であり、「実行済み」ではありません。**
実行して初めて下表の `Result` を更新できます。

なお 2.11 のとおり、**T-09 の中核メカニズム（ループバックが再生音を取得すること）は
CI の仮想エンドポイント上では実測済み**です。それでも下表の T-09 を `Not tested` の
ままにしてあるのは、実サウンドカード・実スピーカー・実 Bluetooth での挙動が
仮想デバイスと同じである保証はないからです。**実機で確認するまで PASS にはしません。**

| ID | Test | Environment | Result | 理由 |
| --- | --- | --- | --- | --- |
| T-01 | ZIP展開のみで起動できる | **Windows physical machine** | **PASS** | 2026-09-08。`Downloads\MeetingRecorder-win-x64\` に展開しただけで実行でき、`data\` が隣に作られポータブルモードで動作。**GUIウィンドウ自体の表示は未確認**（診断はコンソールモード） |
| T-02 | SmartScreen の挙動 | Not tested | — | ランナーには SmartScreen の実行環境がない |
| T-03 | 一般ユーザー権限での動作 | **Windows physical machine** | **PASS** | 2026-09-08。「X64（一般ユーザー権限）」で全 11 チェックが OK。デバイス開閉・録音取得・ファイル書き込み・SHA-256照合まで昇格なしで実行 |
| T-04 | マイク認識 | **Windows physical machine** | **PASS** | 2026-09-08。2 件を列挙（Realtek ステレオミキサー / Intel SST マイク配列）。既定として後者を選択 |
| T-05 | 再生デバイス認識 | **Windows physical machine** | **PASS** | 2026-09-08。1 件（スピーカー Realtek(R) Audio、既定） |
| T-06 | マイクのプライバシー設定 | **Windows physical machine** | **PASS** | 2026-09-08。許可されている状態で取得成功。**拒否時の挙動は CI の実 Windows で別途確認済み**（2.12）。両側が揃いました |
| T-07 | 録音前のマイクレベルメーター | **Windows physical machine** | **PASS（測定経路）** | 2026-09-08。10 秒で 480,480 サンプル（想定の 100%）/ peak -3.1 dBFS / RMS -26.1 dBFS。**UI メーターの描画自体は未確認**（測定している値は同一経路） |
| T-08 | 録音前のPC音声レベルメーター | **Windows physical machine** | **PASS（測定経路）** | 2026-09-08。10 秒で 479,520 サンプル（想定の 100%）/ peak -7.8 dBFS / RMS -23.9 dBFS。UI メーターの描画自体は未確認 |
| T-09 | **WASAPI ループバックでのPC内部音声取得** | **Windows physical machine** | **PASS** | **2026-09-08。実 Realtek スピーカーからのループバックで 10 秒間に 479,520 サンプル（想定の 100%）取得、peak -7.8 dBFS、信号検出。本製品の中核が実機で確認されました**（2.13） |
| T-10 | Zoom 音声の取得 | Not tested | — | Zoom の実音声が必要 |
| T-11 | Google Meet 音声の取得 | Not tested | — | Meet の実音声が必要 |
| T-12 | Microsoft Teams 音声の取得 | Not tested | — | Teams の実音声が必要 |
| T-13 | 仮想オーディオドライバ非依存 | **Windows physical machine** | **PASS** | 2026-09-08。列挙されたデバイスは Realtek と Intel SST のみ。仮想オーディオドライバが 1 つも入っていない環境でループバック取得に成功 |
| T-14 | Stereo Mix 非依存 | **Windows physical machine** | **PASS** | 2026-09-08。**この実機には「ステレオ ミキサー」が存在します**が、アプリはそれを使わず、再生デバイス「スピーカー (Realtek)」のループバックで取得しました |
| T-15 | マイク＋PC音声の同時録音 | Not tested | — | 実デバイス2系統が必要。**CI ではマイク側が権限拒否のため実演できていません**（2.11） |
| T-16 | 30分録音での同期精度 | Not tested | — | 実クロック差の測定が必要 |
| T-17 | 1時間録音での同期精度 | Not tested | — | 同上 |
| T-18 | 30分録音の安定性 | Not tested | — | 実機の長時間動作 |
| T-19 | 1時間録音の安定性 | Not tested | — | 同上 |
| T-20 | CPU使用率（i5-1335U） | Not tested | — | **基準PC（i5-1335U）での実測が必要。**2026-09-08 に確認した実機は 12 論理コア / 15.7 GB の別構成で、実測スコア 1.34・推定RTF 0.41・Whisper small / 6スレッドが自動選択されました（録音中のCPU使用率は未測定） |
| T-21 | RAM使用量 | Not tested | — | 同上 |
| T-22 | モデルダウンロード（実通信） | **Windows physical machine** | **PASS** | 2026-09-08。「Whisper small (量子化 q5_1)」を実機で取得済み、**SHA-256 照合済み**。ピン留めハッシュが実ダウンロードと一致することが実機でも確認されました |
| T-23 | 文字起こし（日本語・録音後） | Not tested | — | 実モデル＋実音声が必要。**1時間の会議に対する実処理時間はこの手順でしか分かりません** |
| T-23b | 録音中に文字起こしが動かないこと | Not tested | — | 実機のタスクマネージャーでの CPU／メモリ観察が必要。コード上の保証は 2.3 で自動検証済み |
| T-24 | 別のモデルでの再文字起こし | Not tested | — | 実モデル2種と実音声が必要 |
| T-25 | 小さい声・相槌の保持 | Not tested | — | 実音声が必要 |
| T-26 | 発言元の分離（実録音） | Not tested | — | 実デバイス2系統が必要 |
| T-27 | 話者分離（実話者） | Not tested | — | 実音声が必要 |
| T-28 | 話者名の一括反映 | Not tested | — | 実機UI操作 |
| T-29 | 文字起こしの編集 | Not tested | — | 実機UI操作 |
| T-30 | 録音音声が聞き取れる音量になっている | Not tested | — | 実音声での聴感確認が必要 |
| T-31 | クリッピングが発生しない（実録音） | Not tested | — | 実音声が必要 |
| T-32 | 音量が時間とともに変化しない | Not tested | — | 実環境ノイズでの確認が必要。固定ゲインであることは 2.4 で自動検証済み |
| T-32b | 大小の声の比が保たれている | Not tested | — | 実音声が必要。比が保たれることは 2.4 で自動検証済み |
| T-33 | WAV 保存（実機再生） | Not tested | — | 実機での再生確認 |
| T-34 | MP3 保存（実機再生） | Not tested | — | 実機での再生確認 |
| T-35 | MP3 エンコーダー不在時の挙動 | Not tested | — | N/KN エディションが必要 |
| T-36 | txt / md 保存 | Not tested | — | 実録音からの生成確認 |
| T-37 | USB デバイス切断 | Not tested | — | 物理的な抜き差しが必要 |
| T-38 | Bluetooth 切断 | Not tested | — | 実 Bluetooth 機器が必要 |
| T-39 | 既定デバイスの変更 | Not tested | — | 実デバイスが必要 |
| T-40 | スリープ抑制 | Not tested | — | 実電源設定での確認が必要 |
| T-41 | クラッシュ復旧（実機） | Not tested | — | 実プロセス強制終了 |
| T-42 | 電源断からの復旧 | Not tested | — | 実電源断 |
| T-43 | ディスク空き容量不足 | Not tested | — | 実ドライブ構成（`--diagnose` の D-03 で確認可） |
| T-44 | オフライン動作 | Not tested | — | 実機のネットワーク切断 |
| T-45 | **通信の監視（外部送信がないこと）** | Not tested | — | **実機での録音中の監視が未実施。最重要確認項目。**2026-09-08 の実機診断では D-08 が「外向きTCP接続 0 件」でしたが、これは**録音していない一時点の一プロセス**の測定です。録音〜議事録生成の間の監視は `tools/Verify-NoNetwork.ps1` で別途必要 |
| T-45b | WAV のインポート | Not tested | — | 実機のファイルダイアログ操作が必要。デコード自体は 2.14 で実 Windows 検証済み |
| T-45c | MP3 / M4A / AAC のインポート | Not tested | — | 同上。Media Foundation の可否は 2.14 を参照 |
| T-45d | インポート音声の再文字起こし | Not tested | — | 実モデルと実機UI操作が必要 |
| T-45e | 議事録は文字起こしの後でのみ実行できる | Not tested | — | 実機UI操作。状態遷移は 2.8 で自動検証済み |
| T-46 | 抽出型議事録（実録音から） | Not tested | — | 実文字起こしが必要 |
| T-47 | ローカルLLM議事録 | Not tested | — | 実モデル＋実機性能が必要 |

手順: [`docs/WINDOWS_E2E_TEST.md`](docs/WINDOWS_E2E_TEST.md)

---

## 4. 既知の未解決事項

| 項目 | 状態 |
| --- | --- |
| コード署名 | 未実施。SmartScreen の警告が出ます |
| 実機での性能実測 | 未実施。`ProfileSelector` の推定処理時間は基準機の想定値であり、実測で較正されていません。UI 上も「実測値ではなく推定です」と明示しています。**録音に追いつく必要が無くなったため、推定が外れても失われるのは待ち時間だけで、録音や文字起こしの可否には影響しません** |
| **基準PCでの medium / large-v3-turbo の所要時間・RAM** | **未実測。** CI の Windows ランナー（4論理コア）での測定値は 2.15 にありますが、これは i5-1335U ではありません |
| **生の ADTS `.aac` のインポート** | **未検証。** CI で真の ADTS ファイルを生成する手段がないためです（2.12） |
| **Windows N / KN での MP3 / M4A / AAC インポート** | **未検証。** Media Feature Pack 非導入環境が CI にありません。WAV は OS コーデック非依存のため影響を受けません |
| **録音と文字起こしの排他** | 自動テストなし。UI コマンドの実行可否条件として実装し、コードレビューで確認しました |
| 話者分離の精度評価 | 未実施。合成音声での分離のみ確認しています。**同時発話は分離できず、声質の似た2人は統合され、距離が変わった同一人物は分割されます**（README.md「既知の制約」に同じ記載） |
| 日本語での認識精度 | 未実施。CI で確認できたのは**英語の合成音声**までです（2.9）。日本語は T-23 |
| **精度改善の効果量** | **未測定。**加工の除去・長い窓・ビームサーチ・日本語プロンプトはいずれも原理的な改善ですが、**日本語の実会議でどれだけ良くなるかは実測していません。**「加工していないこと」は検査していますが、「そのおかげで精度が上がること」は検証していません。同条件で録り直して比較する以外に確認手段がありません |
| 文字起こしの所要時間 | 未測定。1時間の録音に対する実処理時間は実機でのみ分かります |
| 実サウンドカードでの動作 | **2026-09-08 に確認済み**（2.13）。Realtek 環境 1 台のみで、他のオーディオチップ・Bluetooth 機器での挙動は未確認です |
| マイクとPC音声の同時録音 | 未実演。CI ではマイク側が権限拒否、実機診断でも D-04 と D-05 は順番に実行されるため、**2 系統が同時に開かれた状態は未測定**です（T-15） |
| 録音後の音量（聴感） | 未評価。固定ゲインが数学的に正しいことは検査していますが、**実際の会議録音が聞きやすい音量になるか**は実機で確認が必要です |
| コード署名の代替 | 未実施。配布 ZIP の SHA-256 は Actions のログから取得できますが、リリースに署名は付いていません |
| OpenVINO / GPU 高速化 | 未実装（CPU のみ。要件どおり GPU 必須にはしていません） |
| 実行中の自動機能縮退 | **廃止しました。** リアルタイムに追いつくための仕組みであり、追いつく相手が存在しません |

---

## 5. 更新方法

実機テストを実施したら、本ファイルの該当行の `Environment` を
`Windows physical machine` に、`Result` を `PASS` または `FAIL` に更新し、
Notes に実施日・OS ビルド・使用デバイスを記載してください。

最短の手順は次の 2 つです。結果をそのまま貼り付けられます。

```powershell
# D-01〜D-08（T-04/05/06/07/09/20/22/43/45 の一部）
.\MeetingRecorder.exe --diagnose --seconds 10 --json diagnose.json

# T-45（会議を録音している間、外部通信が無いことを外側から監視）
powershell -ExecutionPolicy Bypass -File tools\Verify-NoNetwork.ps1
```

**未実施の項目を PASS にしないでください。**
`--diagnose` が「エラー」を返した項目を PASS にするのも同様に禁止です。
