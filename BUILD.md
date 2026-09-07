# BUILD.md — ビルド手順

このリポジトリだけから、配布物 `MeetingRecorder-win-x64.zip` を再現できます。

---

## 1. 必要なもの

| 項目 | 要件 |
| --- | --- |
| .NET SDK | **8.0.100 以降**（`global.json` は `rollForward: latestMajor`） |
| OS（フルビルド） | **Windows 10/11 x64**。WPF のビルドと win-x64 の自己完結型 publish に必要 |
| OS（部分ビルド） | Linux / macOS でも `Core` / `Stt` / `Diarization` / `Minutes` / `Audio` とそのテストはビルド・実行できます |
| ネットワーク | NuGet（`api.nuget.org`）へのアクセス。AIモデルのダウンロードはビルドには不要 |

追加のツールチェーンは不要です。Python、Node.js、Visual C++ Build Tools、
CMake、vcpkg はいずれも使いません（whisper.cpp と llama.cpp の
ネイティブバイナリは NuGet パッケージとして配布されています）。

SDK の確認:

```powershell
dotnet --version      # 8.0.x 以降
dotnet --list-sdks
```

---

## 2. 取得

```powershell
git clone https://github.com/chihirohashimotoac-coder/Meeting-Capture.git
cd Meeting-Capture
```

---

## 3. restore

```powershell
dotnet restore MeetingRecorder.sln
```

NuGet のバージョンは `Directory.Packages.props`（Central Package Management）で
一元管理され、すべて厳密なバージョンで固定されています。

---

## 4. build

```powershell
dotnet build MeetingRecorder.sln --configuration Release --no-restore
```

**Linux / macOS の場合**（WPF はビルドできないため、プロジェクトを個別に指定します）:

```bash
for p in src/MeetingRecorder.Core src/MeetingRecorder.Audio src/MeetingRecorder.Stt \
         src/MeetingRecorder.Diarization src/MeetingRecorder.Minutes; do
  dotnet build "$p" -c Release
done
```

`MeetingRecorder.Audio` は `net8.0-windows` ですが、
`EnableWindowsTargeting=true`（`Directory.Build.props`）により
非 Windows 環境でもコンパイルできます（実行は Windows のみ）。

---

## 5. test

```powershell
dotnet test MeetingRecorder.sln --configuration Release --no-build
```

| テストプロジェクト | 対象フレームワーク | 内容 |
| --- | --- | --- |
| `MeetingRecorder.Core.Tests` | net8.0 | DSP（リミッター・AGC・ゲート・リサンプラー・ドリフト補正）、WAV の耐クラッシュ性、VAD/チャンク化、STTキューの無損失性、永続化と復旧、合成キャプチャによる録音パイプラインのE2E、話者分離、議事録生成 |
| `MeetingRecorder.Stt.Tests` | net8.0 | モデルダウンロードの整合性（SHA-256照合・レジューム・許可リスト外の拒否）、認識器の契約 |
| `MeetingRecorder.Audio.Tests` | net8.0-windows | WASAPI エンドポイント列挙、デバイス不在時のエラーメッセージ、スリープ抑制、MP3エンコーダーの有無 |
| `MeetingRecorder.App.Tests` | net8.0-windows | 全ウィンドウの XAML ロードとデータバインド検証（STAスレッド上） |

**Linux / macOS の場合**:

```bash
dotnet test tests/MeetingRecorder.Core.Tests
dotnet test tests/MeetingRecorder.Stt.Tests
```

`Audio.Tests` と `App.Tests` は `net8.0-windows` のため Windows でのみ実行できます。

個別実行の例:

```powershell
dotnet test tests/MeetingRecorder.Core.Tests --filter "LimiterTests"
dotnet test MeetingRecorder.sln --logger "trx;LogFileName=test-results.trx" --results-directory TestResults
```

---

## 6. publish（自己完結型 win-x64）

```powershell
dotnet publish src/MeetingRecorder.App/MeetingRecorder.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output publish/MeetingRecorder-win-x64
```

- `SelfContained=true`: .NET ランタイムを同梱するため、
  利用者側に .NET のインストールは不要です。
- `PublishTrimmed=false`: WPF はトリム非対応で、Whisper.net と LLamaSharp は
  ネイティブアセットをリフレクションで解決するため、トリミングは行いません。
- `PublishSingleFile=false`: 単一 EXE 化はネイティブライブラリの展開を伴い
  起動が遅くなるため採用していません（要件でも必須ではありません）。

出力サイズの目安: 展開後 約 300〜400 MB（LLamaSharp の CPU バックエンドを含む）。

---

## 7. package（ZIP 作成）

```powershell
$target = "publish/MeetingRecorder-win-x64"

Copy-Item packaging/README.txt   -Destination "$target/README.txt" -Force
Copy-Item LICENSE                -Destination "$target/LICENSE" -Force
Copy-Item THIRD_PARTY_NOTICES.md -Destination "$target/THIRD_PARTY_NOTICES.md" -Force

New-Item -ItemType Directory -Force -Path artifacts | Out-Null
Compress-Archive -Path "$target/*" `
  -DestinationPath "artifacts/MeetingRecorder-win-x64.zip" `
  -CompressionLevel Optimal -Force

(Get-FileHash "artifacts/MeetingRecorder-win-x64.zip" -Algorithm SHA256).Hash
```

**AIモデルは配布物に含めません。** CI にはこれを検証するステップがあります
（`*.gguf` や `ggml-*.bin` が publish 出力にあればビルドを失敗させます）。

---

## 8. GitHub Actions workflow

| ワークフロー | ファイル | トリガー | 内容 |
| --- | --- | --- | --- |
| Windows build | `.github/workflows/build-windows.yml` | 全ブランチへの push / PR / 手動 | restore → build → test → publish → 出力検証 → ZIP → Artifact `MeetingRecorder-win-x64` |
| Release | `.github/workflows/release.yml` | タグ `v*` / 手動 | 同じ内容をビルドし、SHA-256 付きの **下書き** Release を作成 |
| Verify model checksums | `.github/workflows/model-hashes.yml` | 手動 / 毎月 | `ModelCatalog` にピン留めした SHA-256 を実ファイルと照合 |

すべて `permissions: contents: read` を既定とし、
`contents: write` は Release を作成するジョブにのみ付与しています。
サードパーティ製（GitHub 公式以外）の Action は使用していません。

### CI の publish 検証

`build-windows.yml` は publish 出力に対して次を検証します。
いずれかが満たされない場合はビルドを失敗させます。

- `MeetingRecorder.exe` が存在する
- `hostfxr.dll` / `coreclr.dll` / `PresentationFramework.dll` / `MeetingRecorder.Core.dll` が存在する
  （自己完結型であることの確認）
- whisper のネイティブバイナリが含まれている
- `*.gguf` / `ggml-*.bin`（AIモデル）が **含まれていない**
- 生成した ZIP が開けて、`MeetingRecorder.exe` を含む

---

## 9. ローカルでの動作確認

ビルドした EXE を起動するだけです。

```powershell
./publish/MeetingRecorder-win-x64/MeetingRecorder.exe
```

初回は設定ウィザードが開きます。文字起こしを試す場合は
ウィザードまたは設定画面からモデルをダウンロードしてください。

**CI のグリーンは実機動作の確認ではありません。**
実オーディオデバイスを伴う確認は `docs/WINDOWS_E2E_TEST.md` に従ってください。

---

## 10. プロジェクト構成

```
MeetingRecorder.sln
├── src/
│   ├── MeetingRecorder.Core          net8.0         DSP・パイプライン・永続化・STT抽象
│   ├── MeetingRecorder.Audio         net8.0-windows WASAPI / Media Foundation / スリープ抑制
│   ├── MeetingRecorder.Stt           net8.0         whisper.cpp アダプタ
│   ├── MeetingRecorder.Diarization   net8.0         MFCC + クラスタリング
│   ├── MeetingRecorder.Minutes       net8.0         抽出型 + ローカルLLM議事録
│   └── MeetingRecorder.App           net8.0-windows WPF
├── tests/                            上記4プロジェクト
├── packaging/README.txt              配布ZIPに同梱される利用者向け説明
├── tools/model_checksums.py          モデルSHA-256の検証・更新
├── Directory.Build.props             共通ビルド設定
├── Directory.Packages.props          NuGetバージョンの一元管理
└── global.json                       SDKバージョン
```

---

## 11. トラブルシューティング（ビルド時）

**`NETSDK1135: SupportedOSPlatformVersion ... cannot be higher than TargetPlatformVersion`**
`net8.0-windows` に `SupportedOSPlatformVersion` を指定しない構成にしています。
指定する場合は TFM を `net8.0-windows10.0.19041.0` にしてください。

**Linux で `MeetingRecorder.App` が復元できない**
WPF は Windows でのみビルドできます。想定どおりです。
`dotnet sln list` には表示されますが、ビルド対象から個別に外してください。

**`MSB5008: Error parsing the solution configuration section`**
`MeetingRecorder.sln` の `ProjectConfigurationPlatforms` セクションに
プロジェクト GUID の設定行が揃っているか確認してください。

**restore が遅い**
`LLamaSharp.Backend.Cpu` と `Whisper.net.Runtime` は
複数プラットフォームのネイティブバイナリを含むため、初回は数十秒〜数分かかります。
CI では `actions/cache` で NuGet キャッシュを再利用しています。
