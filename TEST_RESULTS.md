# TEST_RESULTS.md — テスト結果

最終更新: 2026-09-07 / 対象ブランチ: `claude/windows-meeting-recorder-app-slkq27`

自動テスト合計 **151 件**（Core 122 / Stt 11 / App 10 / Audio 8）。すべて GitHub Actions の `windows-latest` 上で成功しています。

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

本ドキュメント作成時点で、**実機（Windows physical machine）での確認は
一切行われていません。** 実機確認が必要な項目はすべて `Not tested` です。
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
| `MeetingRecorder.Core.Tests`（122 件） | GitHub Actions / windows-latest | PASS | DSP・永続化・パイプライン・話者分離・議事録 |
| `MeetingRecorder.Stt.Tests`（11 件） | GitHub Actions / windows-latest | PASS | モデルダウンロード整合性・認識器契約 |
| `MeetingRecorder.Audio.Tests`（8 件） | GitHub Actions / windows-latest | PASS | 実デバイス非依存の範囲のみ（下記 C も参照） |
| `MeetingRecorder.App.Tests`（10 件） | GitHub Actions / windows-latest | PASS | 全ウィンドウの XAML ロード＋データバインド検証 |
| 自己完結型 publish（win-x64） | GitHub Actions / windows-latest | PASS | `--self-contained true` |
| publish 出力の検証 | GitHub Actions / windows-latest | PASS | `MeetingRecorder.exe` / `hostfxr.dll` / `coreclr.dll` / `PresentationFramework.dll` / whisper ネイティブの存在確認 |
| AIモデルが配布物に混入しないこと | GitHub Actions / windows-latest | PASS | `*.gguf` / `ggml-*.bin` があればビルド失敗 |
| ZIP 生成 | GitHub Actions / windows-latest | PASS | `MeetingRecorder-win-x64.zip` |
| ZIP の内容検証 | GitHub Actions / windows-latest | PASS | 展開せずに `MeetingRecorder.exe` の存在を確認 |
| Artifact アップロード | GitHub Actions / windows-latest | PASS | Artifact 名 `MeetingRecorder-win-x64`（約 73 MB） |
| llama.cpp の命令セット別レイアウト保持 | GitHub Actions / windows-latest | PASS | noavx/avx/avx2/avx512 の4種が存在し、ルートに平坦化された `llama.dll` が無いことを検証 |
| AIモデルの SHA-256 実測 | GitHub Actions / ubuntu-latest | PASS | 全 6 モデルをダウンロードしてハッシュを取得し、カタログにピン留め |

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
| **実モデルでの日本語認識精度** | **Not tested** | — | **C 分類。T-23** |

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

---

## 3. C. Windows 実機確認が必要（すべて未実施）

以下は GitHub Actions 上では原理的に検証できません。
**推測で PASS と記載してはいけません。**

| ID | Test | Environment | Result | 理由 |
| --- | --- | --- | --- | --- |
| T-01 | ZIP展開のみで起動できる | Not tested | — | ランナーで GUI アプリを起動していない |
| T-02 | SmartScreen の挙動 | Not tested | — | ランナーには SmartScreen の実行環境がない |
| T-03 | 一般ユーザー権限での動作 | Not tested | — | ランナーは管理者権限で動作 |
| T-04 | マイク認識 | Not tested | — | **ランナーに実マイクが存在しない** |
| T-05 | 再生デバイス認識 | Not tested | — | **ランナーに実スピーカーが存在しない** |
| T-06 | マイクのプライバシー設定 | Not tested | — | 実機の Windows 設定が必要 |
| T-07 | 録音前のマイクレベルメーター | Not tested | — | 実音声入力が必要 |
| T-08 | 録音前のPC音声レベルメーター | Not tested | — | 実再生が必要 |
| T-09 | **WASAPI ループバックでのPC内部音声取得** | Not tested | — | **本製品の中核。実機必須** |
| T-10 | Zoom 音声の取得 | Not tested | — | Zoom の実音声が必要 |
| T-11 | Google Meet 音声の取得 | Not tested | — | Meet の実音声が必要 |
| T-12 | Microsoft Teams 音声の取得 | Not tested | — | Teams の実音声が必要 |
| T-13 | 仮想オーディオドライバ非依存 | Not tested | — | 実機構成の確認が必要 |
| T-14 | Stereo Mix 非依存 | Not tested | — | 実機構成の確認が必要 |
| T-15 | マイク＋PC音声の同時録音 | Not tested | — | 実デバイス2系統が必要 |
| T-16 | 30分録音での同期精度 | Not tested | — | 実クロック差の測定が必要 |
| T-17 | 1時間録音での同期精度 | Not tested | — | 同上 |
| T-18 | 30分録音の安定性 | Not tested | — | 実機の長時間動作 |
| T-19 | 1時間録音の安定性 | Not tested | — | 同上 |
| T-20 | CPU使用率（i5-1335U） | Not tested | — | 基準PCでの実測が必要 |
| T-21 | RAM使用量 | Not tested | — | 同上 |
| T-22 | モデルダウンロード（実通信） | Not tested | — | 実機でのUI操作が必要 |
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
| T-43 | ディスク空き容量不足 | Not tested | — | 実ドライブ構成 |
| T-44 | オフライン動作 | Not tested | — | 実機のネットワーク切断 |
| T-45 | **通信の監視（外部送信がないこと）** | Not tested | — | **実機でのパケット監視が必要。最重要確認項目** |
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
| OpenVINO / GPU 高速化 | 未実装（CPU のみ。要件どおり GPU 必須にはしていません） |

---

## 5. 更新方法

実機テストを実施したら、本ファイルの該当行の `Environment` を
`Windows physical machine` に、`Result` を `PASS` または `FAIL` に更新し、
Notes に実施日・OS ビルド・使用デバイスを記載してください。

**未実施の項目を PASS にしないでください。**
