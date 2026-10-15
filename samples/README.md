# 架空の解析対象

`FictionalOrders/FictionalOrders.sln`、同名の `.slnx`、または `FictionalOrders` フォルダーを ProjectLens に指定してください。実在のプロジェクトのコードやデータは含みません。

2 プロジェクトの WPF アプリで、ProjectReference、名前空間、インターフェース実装、基底クラス、partial、入れ子型、コンストラクター、プロパティ、既定引数、日本語パス、日本語コメント、XAML とコードビハインドを確認できます。外部パッケージは使用していません。

サンプル自体のビルド・起動:

```powershell
dotnet build samples/FictionalOrders/FictionalOrders.sln
dotnet run --project samples/FictionalOrders/FictionalOrders.Desktop
```

大量ファイル・構文エラー・CP932・除外・キャンセルなどのケースは、`tests/ProjectLens.Tests` が一時ディレクトリに生成し、終了後に削除します。

`OrdersApiClient.cs` と `appsettings.json` は WebAPI 解析用の架空例です。「WebAPI」タブで GET／POST、接続先の設定、送信データ、JSON 応答の読み込み、ステータス判定、結果の加工を確認できます。設定ファイルとコードの対応は解析では確定しません。クライアントはサンプル画面から生成・呼び出さず、サンプル起動時の通信はありません。
