# 同梱する依存ソフトウェア

ProjectLensはMicrosoft.CodeAnalysis.CSharp 5.0.0と、その依存Microsoft.CodeAnalysis.Common 5.0.0を使用します。RoslynはMITライセンスです。

- パッケージ: https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/5.0.0
- Roslynのライセンス: https://github.com/dotnet/roslyn/blob/main/License.txt
- .NETランタイムのライセンス: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- WPFのライセンス: https://github.com/dotnet/wpf/blob/main/LICENSE.TXT

ビルド時の間接依存にMicrosoft.CodeAnalysis.Analyzers 3.11.0があります。実際の復元バージョンは各packages.lock.jsonに記録します。オフラインキット内のnupkgにもライセンス情報を含みます。Self-contained配布には.NETランタイムとWindows Desktopランタイムが含まれます。

`licenses/`にはRoslynの使用バージョンのソースコミット、および.NET / WPF 10.0.11のライセンス・.NETサードパーティ通知を保存しています。発行スクリプトはこのフォルダーも配布物に含めます。SDK更新で同梱ランタイムのバージョンが変わる場合は、対応する通知も更新してください。

この文書は対象プロジェクトのライセンスを変更するものではありません。
