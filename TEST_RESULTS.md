# TEST_RESULTS.md — テスト結果

最終更新: 2026-09-07 / 対象ブランチ: `claude/windows-meeting-recorder-app-slkq27`

自動テスト合計 **169 件**（Core 135 / Stt 13 / App 10 / Audio 11）。すべて GitHub Actions の
`windows-latest` 上で成功しています（実行記録:
[run #39](https://github.com/chihirohashimotoac-coder/Meeting-Capture/actions/runs/34172840832)、
commit `5a9563e`）。

このうち **3 件は実際の音声デバイスを開いて実測**します（2.11）。CI では
サウンドカードの代役として仮想オーディオデバイスを導入しており、
**WASAPI ループバックが再生中の音を実際に取得することを毎ビルド確認**しています。

このうち 2 件（`RealModelRecognitionTests`）は**実際の whisper.cpp と実モデル**を使う
テストで、モデルと音声ファイルが揃った専用ステップで再実行されます。CI では
`MEETINGRECORDER_REQUIRE_REAL_STT=1` を設定しているため、**フィクスチャが無ければ
スキップではなく失敗**します（テストが黙って実行されなくなる事故を防ぐため）。

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

**2026-09-08 に実機での 1 回目の確認を実施しました**（詳細は 2.13）。
これにより 11 項目が `Windows physical machine / PASS` になり、
**本製品の中核である WASAPI ループバックが実サウンドカードで動作することが
確認されました（T-09）。** 残る 36 項目は引き続き `Not tested` です。
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
| Release ビルド（全プロジェクト） | GitHub Actions / windows-latest | PASS | WPF アプリを含む |
| `MeetingRecorder.Core.Tests`（135 件） | GitHub Actions / windows-latest | PASS | DSP・永続化・パイプライン・話者分離・議事録 |
| `MeetingRecorder.Stt.Tests`（13 件） | GitHub Actions / windows-latest | PASS | モデルダウンロード整合性・認識器契約 |
| `MeetingRecorder.Audio.Tests`（11 件） | GitHub Actions / windows-latest | PASS | うち 3 件は実オーディオデバイスを開いて実測（2.11） |
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

---

## 2. B. 自動テストで代替検証済み

実デバイスの代わりに、決定論的な合成音声とテストダブルで検証しています。

### 2.1 音声 DSP

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| リミッターが天井を超えない | GitHub Actions / windows-latest | PASS | 振幅 4.0（フルスケールの4倍）入力でも -1 dBFS 以下 |
| リミッター無効時もクリッピング保護が効く | GitHub Actions / windows-latest | PASS | 最終ハードクランプ |
| リミッターが小音量を変化させない | GitHub Actions / windows-latest | PASS | 先読み遅延を考慮して比較 |
| AGC が小声を目標レベルへ持ち上げる | GitHub Actions / windows-latest | PASS | -40 dBFS → 約 -22 dBFS（+18 dB 上限） |
| AGC が最大ゲインを超えない | GitHub Actions / windows-latest | PASS | -70 dBFS 入力 |
| AGC が無音でゲインを上げない | GitHub Actions / windows-latest | PASS | pumping 防止 |
| AGC の追従速度が緩慢であること | GitHub Actions / windows-latest | PASS | 1 秒で 1.8 dB 以内 |
| ノイズゲートが背景を減衰しつつ無音化しない | GitHub Actions / windows-latest | PASS | 減衰上限 12 dB |
| ノイズゲートが短い発話（200ms）を保持 | GitHub Actions / windows-latest | PASS | 相槌の保持 |
| コンプレッサーが閾値超えを圧縮 | GitHub Actions / windows-latest | PASS | |
| コンプレッサーが小音量を素通しする | GitHub Actions / windows-latest | PASS | |
| リサンプラーの出力サンプル数 | GitHub Actions / windows-latest | PASS | 48k→16k |
| リサンプラーが帯域内信号を保持 | GitHub Actions / windows-latest | PASS | |
| リサンプラーのエイリアシング抑制 | GitHub Actions / windows-latest | PASS | 12kHz 入力を -20 dB 以下に抑制 |
| リサンプラーがブロック境界で連続 | GitHub Actions / windows-latest | PASS | 分割処理と一括処理が一致 |
| ミキサーがヘッドルームを確保しクリップしない | GitHub Actions / windows-latest | PASS | |
| 処理チェーン全体で天井を超えない | GitHub Actions / windows-latest | PASS | |
| 処理チェーンが DC オフセットを除去 | GitHub Actions / windows-latest | PASS | |
| RMS / dB 変換の正確性 | GitHub Actions / windows-latest | PASS | |

### 2.2 同期・バッファ

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
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
| WAV ラウンドトリップ（16bit量子化のみの誤差） | GitHub Actions / windows-latest | PASS | |
| フラッシュ後のヘッダが正しい（強制終了模擬） | GitHub Actions / windows-latest | PASS | 未 Dispose のまま読み出し |
| データチャンク切断時も読める | GitHub Actions / windows-latest | PASS | 電源断模擬 |
| 範囲外サンプルをクランプ（ラップしない） | GitHub Actions / windows-latest | PASS | |
| 合成2ストリームが1ファイルにミックスされる | GitHub Actions / windows-latest | PASS | 合成キャプチャによるE2E |
| 片方のデバイスが開けなくても録音継続 | GitHub Actions / windows-latest | PASS | 警告付きで継続 |
| 両方失敗時は明示的に開始を拒否 | GitHub Actions / windows-latest | PASS | |
| 無音のシステム音声でもタイムラインが継続 | GitHub Actions / windows-latest | PASS | 壁時計マスタークロック |
| STT 例外時も録音が継続 | GitHub Actions / windows-latest | PASS | |
| セッション終了で全成果物が揃う | GitHub Actions / windows-latest | PASS | wav/txt/md/metadata、ジャーナル削除、一時ファイル削除 |
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

### 2.4 文字起こし関連

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| VAD が発話を検出しハングオーバー後に閉じる | GitHub Actions / windows-latest | PASS | |
| VAD が定常ノイズで誤検出しない | GitHub Actions / windows-latest | PASS | |
| VAD が 180ms の短い発話を捕捉 | GitHub Actions / windows-latest | PASS | 相槌相当 |
| チャンカーが発話終了で発行しストリームを刻印 | GitHub Actions / windows-latest | PASS | |
| チャンカーがプリロールを含める | GitHub Actions / windows-latest | PASS | 語頭欠け防止 |
| 長い発話の分割とオーバーラップ | GitHub Actions / windows-latest | PASS | 継ぎ目の語の欠落防止 |
| 停止時に末尾チャンクを発行 | GitHub Actions / windows-latest | PASS | |
| 無音のみでは何も発行しない | GitHub Actions / windows-latest | PASS | |
| スケジューラが全チャンクを処理し発言元を刻印 | GitHub Actions / windows-latest | PASS | |
| タイムスタンプがチャンク開始でオフセットされる | GitHub Actions / windows-latest | PASS | |
| **キュー溢れ時にディスク退避し1件も失わない** | GitHub Actions / windows-latest | PASS | 40 チャンク全件処理を確認 |
| 遅延（バックログ）が UI へ報告される | GitHub Actions / windows-latest | PASS | |
| 認識器の例外でスケジューラが停止しない | GitHub Actions / windows-latest | PASS | |
| 一時退避ディレクトリが削除される | GitHub Actions / windows-latest | PASS | |
| **実モデルでの英語認識（合成音声）** | GitHub Actions / windows-latest | PASS | 実 whisper.cpp。詳細は 2.9 |
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

### 2.6 性能自動調整

| Test | Environment | Result | Notes |
| --- | --- | --- | --- |
| 実測スコアからモデルが選択される | GitHub Actions / windows-latest | PASS | CPU型番判定ではない |
| 基準PC相当スコアで RTF 目標内に収まる | GitHub Actions / windows-latest | PASS | medium は選ばれない |
| RAM 不足時に大型モデルを選ばない | GitHub Actions / windows-latest | PASS | |
| スレッド数が余裕を残す | GitHub Actions / windows-latest | PASS | |
| **劣化順序（話者分離→軽量モデル→LLM）** | GitHub Actions / windows-latest | PASS | 録音・文字起こしは最後まで維持 |
| 追従できている間は劣化しない | GitHub Actions / windows-latest | PASS | |
| 自動選択は Apache-2.0 の LLM のみ | GitHub Actions / windows-latest | PASS | |
| CPU 実測プローブが動作し所要時間内 | GitHub Actions / windows-latest | PASS | |
| **基準PC（i5-1335U）での実測 RTF** | **Not tested** | — | **C 分類。T-20 / T-21** |

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
| チャンカー＋スケジューラ経由の同一経路 | GitHub Actions / windows-latest | PASS | VAD が 1 チャンク（6.67 秒）を切り出し、失敗 0、実測 RTF 0.23 |
| 期待語の一致 | GitHub Actions / windows-latest | PASS | `meeting, recorder, test, budget, review, monday` の **6/6** が一致 |

実際に返ってきたテキスト（CI ログからの逐語引用）:

```
This is a meeting recorder test. The quarterly budget review is scheduled for Monday.
```

**これで確認できたこと:** whisper.cpp のネイティブバイナリが自己完結型 publish から
ロードされること、チャンカーがモデルへ正しく音声を渡していること、経路全体が
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

### 2.12 実デバイスで発見・修正した不具合

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

### 2.13 Windows 実機での確認（2026-09-08）

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

2026-09-08 の実機診断（2.13）で **T-01 / T-03 / T-04 / T-05 / T-06 / T-07 / T-08 /
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
| T-23 | リアルタイム日本語文字起こし | Not tested | — | 実モデル＋実音声が必要 |
| T-24 | 文字起こし遅延の表示 | Not tested | — | 実負荷が必要 |
| T-25 | 小さい声・相槌の保持 | Not tested | — | 実音声が必要 |
| T-26 | 発言元の分離（実録音） | Not tested | — | 実デバイス2系統が必要 |
| T-27 | 話者分離（実話者） | Not tested | — | 実音声が必要 |
| T-28 | 話者名の一括反映 | Not tested | — | 実機UI操作 |
| T-29 | 文字起こしの編集 | Not tested | — | 実機UI操作 |
| T-30 | マイクとPC音声の音量差補正 | Not tested | — | 実音声での聴感確認が必要 |
| T-31 | クリッピングが発生しない（実録音） | Not tested | — | 実音声が必要 |
| T-32 | ノイズのポンピングが起きない | Not tested | — | 実環境ノイズが必要 |
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
| T-46 | 抽出型議事録（実録音から） | Not tested | — | 実文字起こしが必要 |
| T-47 | ローカルLLM議事録 | Not tested | — | 実モデル＋実機性能が必要 |

手順: [`docs/WINDOWS_E2E_TEST.md`](docs/WINDOWS_E2E_TEST.md)

---

## 4. 既知の未解決事項

| 項目 | 状態 |
| --- | --- |
| コード署名 | 未実施。SmartScreen の警告が出ます |
| 実機での性能実測 | 未実施。`ProfileSelector` の RTF 推定値は基準機の想定値であり、実測で較正されていません（実行中の実測 RTF による自動劣化は実装済み） |
| 話者分離の精度評価 | 未実施。合成音声での分離のみ確認しています |
| 日本語での認識精度 | 未実施。CI で確認できたのは**英語の合成音声**までです（2.9）。日本語は T-23 |
| 実サウンドカードでの動作 | **2026-09-08 に確認済み**（2.13）。Realtek 環境 1 台のみで、他のオーディオチップ・Bluetooth 機器での挙動は未確認です |
| マイクとPC音声の同時録音 | 未実演。CI ではマイク側が権限拒否、実機診断でも D-04 と D-05 は順番に実行されるため、**2 系統が同時に開かれた状態は未測定**です（T-15） |
| コード署名の代替 | 未実施。配布 ZIP の SHA-256 は Actions のログから取得できますが、リリースに署名は付いていません |
| OpenVINO / GPU 高速化 | 未実装（CPU のみ。要件どおり GPU 必須にはしていません） |

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
