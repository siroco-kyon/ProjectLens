# 架空の解析対象

`FictionalOrders/FictionalOrders.sln`、同名の `.slnx`、または `FictionalOrders` フォルダーを ProjectLens に指定してください。実在のプロジェクトのコードやデータは含みません。

2 プロジェクトの WPF アプリで、ProjectReference、名前空間、インターフェース実装、基底クラス、partial、入れ子型、コンストラクター、プロパティ、既定引数、日本語パス、日本語コメント、XAML とコードビハインドを確認できます。外部パッケージは使用していません。

サンプル自体のビルド・起動:

```powershell
dotnet build samples/FictionalOrders/FictionalOrders.sln
dotnet run --project samples/FictionalOrders/FictionalOrders.Desktop
```

大量ファイル・構文エラー・CP932・除外・キャンセルなどのケースは、`tests/ProjectLens.Tests` が一時ディレクトリに生成し、終了後に削除します。
