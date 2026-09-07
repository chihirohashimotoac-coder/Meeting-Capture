# Third-party notices

MeetingRecorder 本体は MIT ライセンスです（`LICENSE`）。
配布物および開発時に使用する第三者コンポーネントを以下に示します。

「配布物に含まれる」= `MeetingRecorder-win-x64.zip` に同梱されるもの。
「初回ダウンロード」= 利用者の明示操作でアプリがダウンロードするもの（ZIPには含みません）。

---

## 1. 配布物に含まれるコンポーネント

| コンポーネント | バージョン | ライセンス | 用途 | 配布元 |
| --- | --- | --- | --- | --- |
| .NET Runtime / WPF (Microsoft.WindowsDesktop.App) | 8.0.x | MIT | アプリケーションランタイム（自己完結型配布） | https://github.com/dotnet/runtime |
| NAudio | 2.2.1 | MIT | WASAPI 音声キャプチャ、Media Foundation ラッパー | https://github.com/naudio/NAudio |
| Whisper.net | 1.8.1 | MIT | whisper.cpp の .NET バインディング | https://github.com/sandrohanea/whisper.net |
| Whisper.net.Runtime | 1.8.1 | MIT | whisper.cpp ネイティブバイナリ | https://github.com/sandrohanea/whisper.net |
| whisper.cpp（Whisper.net.Runtime に同梱） | Whisper.net 1.8.1 相当 | MIT | 音声認識推論エンジン | https://github.com/ggerganov/whisper.cpp |
| ggml（whisper.cpp / llama.cpp に同梱） | 同上 | MIT | テンソル演算ライブラリ | https://github.com/ggerganov/ggml |
| LLamaSharp | 0.24.0 | MIT | llama.cpp の .NET バインディング（議事録生成・実験的） | https://github.com/SciSharp/LLamaSharp |
| LLamaSharp.Backend.Cpu | 0.24.0 | MIT | llama.cpp CPU ネイティブバイナリ | https://github.com/SciSharp/LLamaSharp |
| llama.cpp（LLamaSharp.Backend.Cpu に同梱） | LLamaSharp 0.24.0 相当 | MIT | ローカル LLM 推論エンジン | https://github.com/ggerganov/llama.cpp |

### 明示的に採用しなかったもの

| コンポーネント | 不採用の理由 |
| --- | --- |
| LAME / NAudio.Lame（MP3 エンコーダー） | LAME は LGPL であり、ネイティブ DLL の同梱によりライセンス義務が発生します。Windows 標準の Media Foundation MP3 エンコーダーで代替できるため採用していません。 |
| 仮想オーディオドライバ（VB-CABLE 等） | インストーラーと管理者権限が必要で、要件に反します。WASAPI ループバックで代替しています。 |
| 話者分離用の学習済みモデル（pyannote 等） | 配布サイズ・ライセンス・CPU 負荷の観点から採用していません。MFCC + オンラインクラスタリングによる自前実装を使用しています（精度は限定的です。README を参照）。 |

## 2. 初回ダウンロードで取得するAIモデル（ZIPには含みません）

| モデル | ライセンス | 配布元 | 用途 |
| --- | --- | --- | --- |
| ggml-tiny-q5_1.bin | MIT | https://huggingface.co/ggerganov/whisper.cpp | 日本語音声認識（最軽量） |
| ggml-base-q5_1.bin | MIT | https://huggingface.co/ggerganov/whisper.cpp | 日本語音声認識（軽量） |
| ggml-small-q5_1.bin | MIT | https://huggingface.co/ggerganov/whisper.cpp | 日本語音声認識（標準） |
| ggml-medium-q5_0.bin | MIT | https://huggingface.co/ggerganov/whisper.cpp | 日本語音声認識（高精度・高負荷） |
| qwen2.5-1.5b-instruct-q4_k_m.gguf | Apache-2.0 | https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF | ローカル議事録生成（既定） |
| qwen2.5-3b-instruct-q4_k_m.gguf | Qwen Research License | https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF | ローカル議事録生成（自動選択されません。社内利用可否の確認が必要） |

Whisper モデルの重みは OpenAI が MIT ライセンスで公開したものを ggml 形式に変換したものです
（https://github.com/openai/whisper — MIT License）。

**注意**: Qwen2.5-3B は Apache-2.0 ではありません。アプリは自動選択せず、
ダウンロード前にライセンス確認のダイアログを表示します。社内利用の可否は
各組織で判断してください。

## 3. 開発・CI でのみ使用するもの（配布物には含まれません）

| コンポーネント | バージョン | ライセンス | 用途 |
| --- | --- | --- | --- |
| xunit | 2.9.2 | Apache-2.0 | テストフレームワーク |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 | テストランナー |
| Microsoft.NET.Test.Sdk | 17.11.1 | MIT | テストホスト |
| actions/checkout | v4 | MIT | GitHub Actions（GitHub 公式） |
| actions/setup-dotnet | v4 | MIT | GitHub Actions（GitHub 公式） |
| actions/cache | v4 | MIT | GitHub Actions（GitHub 公式） |
| actions/upload-artifact | v4 | MIT | GitHub Actions（GitHub 公式） |
| actions/download-artifact | v4 | MIT | GitHub Actions（GitHub 公式） |

サードパーティ製（GitHub 公式以外）の Action は使用していません。

## 4. フォント

UI では Windows に標準搭載されている Yu Gothic UI / Meiryo UI / Segoe UI を
参照するのみで、フォントファイルは同梱していません。

## 5. ライセンス全文

各コンポーネントのライセンス全文は上記の配布元リポジトリを参照してください。
配布 ZIP には本ファイルと `LICENSE`（MeetingRecorder 本体）を同梱しています。
