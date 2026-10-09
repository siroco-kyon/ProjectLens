# オフラインビルド・ZIP 配布

ここで扱うのは ProjectLens 本体の依存関係だけです。会社の解析対象コードや生成された資料を接続環境に持ち出す必要はありません。すべて会社の承認済み媒体・経路で持ち込みます。

## 接続可能な許可済み PC で準備

Windows と .NET 10 SDK / Windows Desktop ワークロードを用意し、リポジトリーのルートで実行します。

```powershell
dotnet restore ProjectLens.sln --locked-mode
dotnet build ProjectLens.sln -c Release --no-restore
dotnet run --project tests/ProjectLens.Tests -c Release --no-build
.\scripts\Prepare-Offline.ps1 -Destination C:\Approved\ProjectLensOfflineKit
```

`Prepare-Offline.ps1` は専用パッケージキャッシュへ通常のソース用依存を復元し、さらに `win-x64` / self-contained 用ランタイムパックを復元します。解析対象の会社プロジェクトは扱いません。

| 生成物 | 用途 |
|---|---|
| `feed/*.nupkg` | Roslyn と推移的依存、自己完結発行用ランタイムパックのローカルフィード |
| `NuGet.Offline.Config` | 外部ソースを clear し、相対 `feed` のみを使う設定 |
| `SHA256.csv` | `.nupkg` の内容確認用ハッシュ一覧 |
| `*.runtime.lock.json` | ランタイム付き復元のプロジェクト別ロック。通常ビルドのロックと分離 |
| `package-cache/` | 準備作業の専用キャッシュ。通常は持ち込み不要 |

`feed`、`NuGet.Offline.Config`、`SHA256.csv` を配置関係を保って持ち込みます。ランタイム依存を監査するため `ProjectLens.App.runtime.lock.json`、`ProjectLens.Core.runtime.lock.json` も残してください。`RuntimeLockDirectory` の指定により各プロジェクトのロックを分け、通常ビルド用の `packages.lock.json` を変更せずに準備します。依存パッケージの利用許可・ライセンス確認は会社のルールで行います。

## 会社 PC の CLI ビルド

会社 PC に Visual Studio 2026 の .NET デスクトップ開発ワークロードと .NET 10 SDK が必要です。パッケージフィードだけで SDK を置き換えることはできません。以下ではオフラインキットを `D:\Approved\ProjectLensOfflineKit`、ツールのソースを別のローカルフォルダーに置いた例を使います。

```powershell
dotnet --version
dotnet restore ProjectLens.sln --locked-mode --configfile D:\Approved\ProjectLensOfflineKit\NuGet.Offline.Config
dotnet build ProjectLens.sln -c Release --no-restore
dotnet run --project tests/ProjectLens.Tests -c Release --no-build
dotnet run --project src/ProjectLens.App -c Release --no-build
```

オフライン設定はフィード以外のパッケージソースを消し、キット内の `restored-packages` へ復元します。既存のグローバルキャッシュによって不足パッケージが隠れにくい構成です。外部パッケージソース・SDK ダウンロードを使わず、通信を遮断して同じコマンドが成功することを確認してください。

`--locked-mode` で失敗した場合はロックファイルや依存バージョンを会社 PC で勝手に変更せず、承認済み準備環境で不足の `.nupkg` とロックを確認します。SDK、Windows Desktop の参照パック、apphost パックが不足するエラーの場合は、会社の管理手順で SDK / ワークロードを用意します。

## Visual Studio の通常ビルド

CLI の `--configfile` はそのコマンドにだけ適用され、Visual Studio の NuGet 設定を変更しません。完全オフラインの Visual Studio ビルドには、会社側のチェックアウトの `NuGet.Config` を次のようにローカル設定へ変更します。既存内容を保存しておき、フィードの絶対パスを実際の配置へ変更してください。

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="approved-offline" value="D:\Approved\ProjectLensOfflineKit\feed" />
  </packageSources>
  <config>
    <add key="globalPackagesFolder" value="D:\Approved\ProjectLensOfflineKit\restored-packages" />
  </config>
</configuration>
```

ソリューションを開き、`ProjectLens.App` をスタートアップに設定してビルドします。会社固有のパスを含むこの設定を公開 GitHub へコミットしないでください。既に復元済みでも、Visual Studio が再復元する可能性があるため、承認されていない外部ソースが設定に残っていないことを確認します。

## ZIP 配布物を作る

会社 PC ではビルドせずに使う場合、許可済みの準備 PC で実行します。

```powershell
.\scripts\Publish-Portable.ps1 -OutputDirectory C:\Approved\ProjectLens-win-x64
```

同じ準備済みキットを使ってオフラインで発行する場合:

```powershell
.\scripts\Publish-Portable.ps1 -OutputDirectory C:\Approved\ProjectLens-win-x64 -OfflineConfig D:\Approved\ProjectLensOfflineKit\NuGet.Offline.Config
```

`ProjectLens-win-x64` フォルダーと、同じ場所の `ProjectLens-win-x64.zip` ができます。Release、win-x64、self-contained で .NET ランタイム・Roslyn・依存 DLL を含めます。発行用の各 `*.runtime.lock.json` はリポジトリーの `artifacts` 内に生成し、通常ビルド用ロックと分離します。単一ファイル化・trimming は行いません。WPF の動作に必要なファイルをフォルダー全体で配布してください。既存 ZIP がある場合は別の出力名を指定します。

ZIP を会社 PC の許可済みローカルフォルダーへ展開し、`ProjectLens.App.exe` を起動します。管理者権限や .NET ランタイムの追加インストールは不要です。会社の実行ポリシー、ウイルス対策ソフト、持ち込み制限、64 ビット Windows の要件を満たす必要があります。

ソースをビルドするときも実行するときも、Microsoft Office は不要です。ZIP に会社のソース・解析結果を追加しないでください。配布物内の `README.md`、`THIRD-PARTY-NOTICES.md`、`licenses/` を残します。

## 運用と再現性

公開ソースはロックファイルを含めて Git 管理し、`bin` / `obj` / `.vs`、専用キャッシュ、フィード、ZIP をコミットしません。Self-contained 配布は同梱ランタイムを自動更新しないため、更新する場合は承認済みの準備 PC で再ビルド・再検証し、会社へ新しい配布物を持ち込みます。

最終確認は [手動セキュリティ・UI 確認](SECURITY-TESTS.md) に従い、実際の配布物を通信遮断状態で起動して行います。NuGet 復元の成功だけでアプリ実行時のオフライン確認を代用しないでください。
