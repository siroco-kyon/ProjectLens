using System.Security.Cryptography;
using System.Text;
using ProjectLens.Core.Analysis;
using ProjectLens.Core.Context;
using ProjectLens.Core.Export;
using ProjectLens.Core.Models;

namespace ProjectLens.Tests;

// テスト用フレームワークを増やさず、オフライン環境でも同じ検証を実行する。
internal static class Program
{
    private const string BodyDelimiter = "\n\n<!-- CONTEXT BODY -->\n";

    public static async Task<int> Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Roslyn 構造・入れ子・部分型・シグネチャ", SyntaxStructure),
            ("同名型の関係を確定扱いしない", AmbiguousRelationships),
            ("sln / slnx / csproj の対象範囲と参照", InputScopes),
            ("既定・カスタム除外と明示範囲", ExclusionsAndScope),
            ("構文エラーでも正常ファイルの解析を継続", MalformedSyntax),
            ("日本語パス・UTF-8・CP932", JapaneseEncoding),
            ("解析前と解析途中のキャンセル", Cancellation),
            ("400 ファイルと元ファイルのハッシュ維持", ManyFilesReadOnly),
            ("WebAPI の JSON・XML 接続設定と根拠行", WebApiConfigurationSettings),
            ("WebAPI のコード・環境変数による接続設定", WebApiCodeSettings),
            ("WebAPI の送信内容・JSON 応答・エラー処理", WebApiRequestsAndProcessing),
            ("WebAPI の複数応答を混ぜずコメント・同名 API を除外", WebApiFlowIsolation),
            ("WebAPI の壊れた設定・キャンセル・読み取り専用", WebApiMalformedAndReadOnly),
            ("WebAPI 資料の選択範囲・Markdown・機密値", WebApiContextSafety),
            ("概要・構造・6 用途の指示文", ContextKindsAndPrompts),
            ("機能選択とソース本文の明示 opt-in", FeatureAndSource),
            ("関連範囲の既定値と選択ファイルのみ", FeatureSelectionDefaults),
            ("参照方向と 1～3 段の循環グラフ", FeatureDirectionDepths),
            ("範囲外の関係・未解析ファイルを資料へ漏らさない", FeatureScopeIsolation),
            ("選択・参照・診断パスの区切りと大文字小文字", FeatureNormalizedPaths),
            ("複数選択・重複選択の最短距離と明示本文", FeatureMultipleSelections),
            ("解析した参照方向と部分型の全定義ファイル", FeatureAnalyzedReferences),
            ("関連範囲設定の検証と生成中キャンセル", FeatureOptionsValidation),
            ("Markdown の表・パス・コードフェンス", MarkdownEscaping),
            ("分割の文字数・連番・Unicode・情報保持", SplitLossless),
            ("md / txt 保存・対象配下禁止・上書き防止", ExportSafety),
        };
        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                await run();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL  {name}\n      {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
            }
        }
        Console.WriteLine($"\n結果: {tests.Length - failures} / {tests.Length} 成功");
        return failures == 0 ? 0 : 1;
    }

    private static async Task SyntaxStructure()
    {
        using var fixture = new Fixture();
        fixture.Write("Types.cs", """
            using System;
            using Collection = System.Collections.Generic;
            namespace 架空.Core;
            public interface IRepository<T> { T Find(int id); }
            public abstract class BaseEntity { public string Name { get; init; } = ""; }
            public partial class Repository<T> : BaseEntity, IRepository<T> where T : class
            {
                public Repository(string name) { Name = name; }
                public T Find(int id) => default!;
                public static TResult Convert<TResult>(in T item, ref int count, out string note, params string[] tags)
                    { note = ""; return default!; }
                public int Count { get; private set; }
                public class Nested { public void Run() {} }
            }
            public record Entity(int Id);
            """);
        fixture.Write("Part.cs", "namespace 架空.Core; public partial class Repository<T> { public T Save(T item, string label = \"既定\") => item; }");
        fixture.Write("Legacy.cs", "namespace Old { namespace Nested { public class Legacy { public void Go() {} } } }");
        var result = await fixture.Analyze();
        var repository = result.Types.Single(t => t.Name == "Repository" && t.Members.Any(m => m.Name == "Find"));
        Equal("架空.Core", repository.Namespace, "ファイルスコープ名前空間");
        Equal("Class", repository.Kind, "クラス種別");
        True(repository.BaseTypes.Contains("BaseEntity") && repository.BaseTypes.Contains("IRepository<T>"), "基底型・実装インターフェース");
        True(repository.Modifiers.Contains("partial"), "partial 修飾子");
        var find = repository.Members.Single(m => m.Name == "Find");
        Equal("T", find.ReturnType, "戻り値");
        Equal("int", find.Parameters.Single().Type, "引数型");
        Equal("id", find.Parameters.Single().Name, "引数名");
        True(repository.Members.Any(m => m.Kind == "Constructor" && m.Parameters.Single().Name == "name"), "コンストラクター");
        True(repository.Members.Any(m => m.Kind == "Property" && m.Name == "Count" && m.ReturnType == "int"), "プロパティ");
        var convert = repository.Members.Single(m => m.Name.StartsWith("Convert<", StringComparison.Ordinal));
        Equal("TResult", convert.ReturnType, "ジェネリックメソッドの戻り値");
        Equal("in", convert.Parameters[0].Modifier, "in 引数");
        Equal("ref", convert.Parameters[1].Modifier, "ref 引数");
        Equal("out", convert.Parameters[2].Modifier, "out 引数");
        Equal("params", convert.Parameters[3].Modifier, "params 引数");
        True(convert.Signature.Contains("Convert<TResult>") && convert.Signature.Contains("ref int count") && convert.Signature.Contains("params string[] tags"), "Copilotへ渡すシグネチャが型引数と引数修飾子を保持");
        var nested = result.Types.Single(t => t.Name == "Nested");
        True(nested.FullName.Contains("Repository") && nested.FullName.EndsWith("Nested", StringComparison.Ordinal), "入れ子型の識別");
        True(!repository.Members.Any(m => m.Name == "Run"), "子型のメソッドを親型へ混入しない");
        var save = result.Types.SelectMany(t => t.Members).Single(m => m.Name == "Save");
        Equal("\"既定\"", save.Parameters[1].DefaultValue, "既定引数");
        True(result.Types.Any(t => t.Kind == "Interface" && t.Name == "IRepository"), "インターフェース");
        True(result.Types.Any(t => t.Kind == "Record" && t.Name == "Entity"), "record");
        Equal("Old.Nested", result.Types.Single(t => t.Name == "Legacy").Namespace, "ブロック形式の入れ子名前空間");
        True(result.Files.Single(f => f.RelativePath == "Types.cs").Usings.Any(u => u.Contains("Collection")), "using 別名");
        True(result.Types.All(t => t.Line > 0) && find.Line > 0, "1 起点のソース位置");
    }

    private static async Task AmbiguousRelationships()
    {
        using var fixture = new Fixture();
        fixture.Write("A.cs", "namespace A; public class Shared {}");
        fixture.Write("B.cs", "namespace B; public class Shared {}");
        fixture.Write("Consumer.cs", "namespace C; public class Consumer : Shared {}");
        var result = await fixture.Analyze();
        var related = result.Relationships.Where(r => r.FromFile.EndsWith("Consumer.cs", StringComparison.Ordinal)).ToArray();
        Equal(0, related.Length, "同名候補から任意の1ファイルを選ばない");
        True(result.Diagnostics.Any(d => d.FilePath.EndsWith("Consumer.cs", StringComparison.Ordinal) && d.Message.Contains("Shared")), "曖昧な型名を診断に残す");
        True(!related.Any(r => r.Confidence.Equals("Confirmed", StringComparison.OrdinalIgnoreCase)), "名前だけ一致した型を確定しない");
        True(related.All(r => !string.IsNullOrWhiteSpace(r.Evidence) && !string.IsNullOrWhiteSpace(r.Confidence)), "関係には根拠と確度を付ける");
    }

    private static async Task InputScopes()
    {
        using var fixture = new Fixture();
        fixture.Write("App/App.csproj", ProjectXml("net10.0-windows", "<UseWPF>true</UseWPF>", "../Core/Core.csproj"));
        fixture.Write("App/App.cs", "namespace App; public class WindowModel : Core.Base {}");
        fixture.Write("App/Unlisted/NestedUnlisted.csproj", ProjectXml("net10.0"));
        fixture.Write("App/Unlisted/Secret.cs", "public class NestedOutsideSolution {}");
        fixture.Write("Core/Core.csproj", ProjectXml("net10.0"));
        fixture.Write("Core/Base.cs", "namespace Core; public class Base {}");
        fixture.Write("Unlisted/Unlisted.csproj", ProjectXml("net10.0"));
        fixture.Write("Unlisted/Secret.cs", "public class OutsideSolution {}");
        fixture.Write("OnlyApp.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{12345678-1111-1111-1111-111111111111}"
            EndProject
            Global
            EndGlobal
            """);
        fixture.Write("Both.slnx", "<Solution><Folder Name=\"/Source/\"><Project Path=\"App/App.csproj\" /><Project Path=\"Core/Core.csproj\" /></Folder></Solution>");
        var project = await fixture.Analyze("App/App.csproj");
        Equal(1, project.Projects.Count, "単一プロジェクト入力");
        True(project.Files.Any(f => f.RelativePath.EndsWith("App.cs", StringComparison.Ordinal)) && !project.Types.Any(t => t.Name is "Base" or "OutsideSolution" or "NestedOutsideSolution"), "csproj の選択範囲");
        True(project.Projects.Single().Framework.Contains("net10.0-windows"), "ターゲットフレームワーク");
        True(project.Projects.Single().AppKind.Contains("WPF", StringComparison.OrdinalIgnoreCase), "WPF 判別");
        True(project.Projects.Single().ProjectReferences.Any(r => r.Contains("Core.csproj")), "ProjectReference");
        var solution = await fixture.Analyze("OnlyApp.sln");
        Equal(1, solution.Projects.Count, "sln に列挙されたプロジェクトのみ");
        True(!solution.Types.Any(t => t.Name is "OutsideSolution" or "NestedOutsideSolution"), "sln 外のプロジェクトを解析しない");
        var slnx = await fixture.Analyze("Both.slnx");
        Equal(2, slnx.Projects.Count, "slnx のプロジェクト数");
        True(slnx.Types.Any(t => t.Name == "Base") && !slnx.Types.Any(t => t.Name is "OutsideSolution" or "NestedOutsideSolution"), "slnx の範囲");
        True(slnx.Relationships.Any(r => r.Kind == "ProjectReferenceDeclaration" && r.Confidence == "Confirmed"), "参照宣言が確定情報");
        True(slnx.Relationships.Any(r => r.Kind == "Inheritance" && r.Confidence == "Inferred"), "コードの継承関係が推定情報");
        var folder = await fixture.Analyze();
        Equal(4, folder.Projects.Count, "フォルダーは全 csproj を再帰探索");
        True(folder.Types.Any(t => t.Name == "NestedOutsideSolution"), "フォルダー入力は入れ子のプロジェクトも含める");
    }

    private static async Task ExclusionsAndScope()
    {
        using var fixture = new Fixture();
        fixture.Write("Keep.cs", "public class Keep {}");
        fixture.Write("Nested/Child.cs", "public class Child {}");
        foreach (var directory in new[] { "bin", "obj", ".git", ".vs" })
            fixture.Write($"{directory}/Generated.cs", $"public class Excluded{directory.Replace('.', '_')} {{}}");
        fixture.Write("Archive/Old.cs", "public class Archived {}");
        var result = await fixture.Analyze(options: new AnalysisOptions { InputPath = fixture.Root, ExcludedDirectories = ["bin", "obj", ".git", ".vs", "Archive"] });
        Equal(2, result.Files.Count, "除外ディレクトリ");
        var scoped = await fixture.Analyze(options: new AnalysisOptions { InputPath = fixture.Root, IncludedRelativePaths = ["Nested"] });
        Equal(1, scoped.Files.Count, "指定フォルダーに範囲を限定");
        Equal("Child", scoped.Types.Single().Name, "範囲内の型");
        var fileScoped = await fixture.Analyze(options: new AnalysisOptions { InputPath = fixture.Root, IncludedRelativePaths = ["Keep.cs"] });
        Equal(1, fileScoped.Files.Count, "指定ファイルに範囲を限定");
        await ThrowsAsync<ArgumentException>(() => fixture.Analyze(options: new AnalysisOptions { InputPath = fixture.Root, IncludedRelativePaths = ["../outside"] }));
        await ThrowsAsync<ArgumentException>(() => fixture.Analyze(options: new AnalysisOptions { InputPath = fixture.Root, IncludedRelativePaths = [fixture.Root] }));
    }

    private static async Task MalformedSyntax()
    {
        using var fixture = new Fixture();
        fixture.Write("Broken.cs", "namespace Test; public class Broken { public void Run( { }");
        fixture.Write("Good.cs", "namespace Test; public class Good { public int Value => 42; }");
        var result = await fixture.Analyze();
        True(result.Diagnostics.Any(d => d.FilePath.EndsWith("Broken.cs", StringComparison.Ordinal) && d.Line > 0), "構文診断のパスと行");
        True(result.Types.Any(t => t.Name == "Good"), "他の正常ファイルは解析できる");
    }

    private static async Task JapaneseEncoding()
    {
        using var fixture = new Fixture("日本語プロジェクト");
        fixture.Write("画面/顧客.cs", "// 日本語のコメント\nnamespace 架空.画面; public class 顧客 { public string 氏名 {get;set;} = \"\"; }");
        fixture.Write("業務/旧形式.cs", "// 日本語コメントの文字コード確認\nnamespace 架空.業務; public class 受注 { public void 登録() {} }", Encoding.GetEncoding(932));
        var result = await fixture.Analyze();
        True(result.Types.Any(t => t.Name == "顧客") && result.Types.Any(t => t.Name == "受注"), "UTF-8 と CP932 の識別子");
        True(result.Files.Any(f => f.RelativePath.Contains("旧形式")), "日本語ファイルパス");
        True(!result.Diagnostics.Any(d => d.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)), "文字化け由来の構文エラーなし");
    }

    private static async Task Cancellation()
    {
        using var fixture = new Fixture();
        fixture.Write("One.cs", "public class One {}");
        using var before = new CancellationTokenSource();
        before.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => fixture.Analyze(cancellationToken: before.Token));
        for (var index = 0; index < 250; index++)
            fixture.Write($"Many/{index:D3}.cs", $"public class C{index} {{ public void Run() {{}} }}");
        using var during = new CancellationTokenSource();
        var progress = new InlineProgress<AnalysisProgress>(p => { if (p.Completed >= 1) during.Cancel(); });
        await ThrowsAsync<OperationCanceledException>(() => fixture.Analyze(progress: progress, cancellationToken: during.Token));
        True(during.IsCancellationRequested, "進捗が発生しキャンセルされた");
        Throws<OperationCanceledException>(() => new ContextGenerator().Generate(ContextFixture(), new ContextOptions(), before.Token));
    }

    private static async Task ManyFilesReadOnly()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 400; index++)
            fixture.Write($"大規模/{index:D3}.cs", $"namespace Batch; public class Class{index} {{ public int Get(int id) => id; }}");
        fixture.Write("bin/Excluded.cs", "public class Excluded {}");
        var before = fixture.Hashes();
        var result = await fixture.Analyze();
        Equal(400, result.ClassCount, "400 クラス");
        Equal(400, result.MethodCount, "400 メソッド");
        var after = fixture.Hashes();
        Equal(before.Count, after.Count, "ファイルを追加・削除しない");
        foreach (var (path, hash) in before) Equal(hash, after[path], $"変更なし: {path}");
    }

    private static async Task WebApiConfigurationSettings()
    {
        using var fixture = new Fixture();
        var json = """
            {
              // 配布時に切り替える接続先
              "OrdersApi": {
                "BaseUrl": "https://orders.example.invalid/api/",
              },
              "TimeoutSeconds": 20
            }
            """;
        var xml = """
            <configuration>
              <appSettings>
                <add key="LegacyApiUrl" value="https://legacy.example.invalid/v1/" />
              </appSettings>
              <applicationSettings>
                <Example.Properties.Settings>
                  <setting name="CatalogServiceUrl" serializeAs="String">
                    <value>https://catalog.example.invalid/v2/</value>
                  </setting>
                </Example.Properties.Settings>
              </applicationSettings>
            </configuration>
            """;
        fixture.Write("appsettings.json", json);
        fixture.Write("App.config", xml);
        var result = await fixture.Analyze();
        var nested = result.WebApiSettings.Single(s => s.FilePath == "appsettings.json" && s.Key == "OrdersApi:BaseUrl");
        True(nested.Value.Contains("https://orders.example.invalid/api/"), "JSON の入れ子の接続先値を抽出");
        Equal(SourceLine(json, "\"BaseUrl\""), nested.Line, "JSON のキーの根拠行");
        Equal("Declared", nested.Confidence, "設定ファイルの値は宣言として表示");
        var legacy = result.WebApiSettings.Single(s => s.FilePath == "App.config" && s.Key == "LegacyApiUrl");
        True(legacy.Value.Contains("https://legacy.example.invalid/v1/"), "appSettings の接続先");
        Equal(SourceLine(xml, "<add key="), legacy.Line, "XML の add 宣言の根拠行");
        var catalog = result.WebApiSettings.Single(s => s.FilePath == "App.config" && s.Key == "CatalogServiceUrl");
        True(catalog.Value.Contains("https://catalog.example.invalid/v2/"), "Settings の name / value を対応付ける");
        Equal(SourceLine(xml, "<setting name="), catalog.Line, "XML の setting 宣言の根拠行");
        Equal(result.Files.Sum(f => f.WebApiSettings.Count), result.WebApiSettings.Count, "全体の設定一覧はファイル別結果の集約");
        True(!result.WebApiSettings.Any(s => s.Key == "TimeoutSeconds"), "一般の数値設定を接続先に混入しない");
        True(result.WebApiSettings.All(s => s.Line > 0 && !string.IsNullOrWhiteSpace(s.Kind)), "設定方式と 1 起点の根拠行を保持");
    }

    private static async Task WebApiCodeSettings()
    {
        using var fixture = new Fixture();
        var source = """
            using System;
            using System.Net.Http;
            using Microsoft.Extensions.Configuration;
            public class ConfiguredApi
            {
                private readonly HttpClient _client;
                public ConfiguredApi(HttpClient client, IConfiguration configuration)
                {
                    _client = client;
                    _client.BaseAddress = new Uri(configuration["OrdersApi:BaseUrl"]);
                }
                public void FromEnvironment()
                {
                    _client.BaseAddress = new Uri(Environment.GetEnvironmentVariable("ORDERS_API_URL"));
                }
                public void FromLiteral()
                {
                    _client.BaseAddress = new Uri("https://fixed.example.invalid/api/");
                }
            }
            """;
        fixture.Write("ConfiguredApi.cs", source);
        var result = await fixture.Analyze();
        True(result.WebApiSettings.Any(s => s.Value.Contains("OrdersApi:BaseUrl") || s.Key.Contains("OrdersApi:BaseUrl")), "構成キーを参照する接続先設定");
        True(result.WebApiSettings.Any(s => s.Value.Contains("ORDERS_API_URL") || s.Key.Contains("ORDERS_API_URL")), "環境変数名を保持し実行時の値を捏造しない");
        var literal = result.WebApiSettings.First(s => s.Value.Contains("https://fixed.example.invalid/api/"));
        Equal(SourceLine(source, "_client.BaseAddress = new Uri(\"https://fixed"), literal.Line, "BaseAddress 代入の行");
        True(result.WebApiSettings.All(s => s.FilePath == "ConfiguredApi.cs" && !string.IsNullOrWhiteSpace(s.Confidence)), "コード設定にもファイルと確度を付ける");
    }

    private static async Task WebApiRequestsAndProcessing()
    {
        using var fixture = new Fixture();
        var source = """
            using System;
            using System.Net.Http;
            using System.Net.Http.Json;
            using System.Threading;
            using System.Threading.Tasks;
            public class OrdersApi
            {
                private readonly HttpClient _http;
                public OrdersApi(HttpClient http) { _http = http; }
                public async Task<Order> Load(int id)
                {
                    var response = await _http.GetAsync($"orders/{id}");
                    response.EnsureSuccessStatusCode();
                    var order = await response.Content.ReadFromJsonAsync<Order>();
                    return order;
                }
                public async Task<string> Create(Order payload)
                {
                    try
                    {
                        var response = await _http.PostAsJsonAsync("orders", payload);
                        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("登録失敗");
                        var body = await response.Content.ReadAsStringAsync();
                        return body;
                    }
                    catch (HttpRequestException)
                    {
                        throw;
                    }
                }
                public Task<Order[]> List() => _http.GetFromJsonAsync<Order[]>("orders");
                public async Task<string> Patch(Order payload)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Patch, "orders/42");
                    request.Content = JsonContent.Create(payload);
                    var response = await _http.SendAsync(request);
                    return await response.Content.ReadAsStringAsync();
                }
                public async Task NamedArgs(Order payload, CancellationToken ct)
                {
                    await _http.PostAsJsonAsync(value: payload, requestUri: "orders");
                    await _http.GetAsync(cancellationToken: ct, requestUri: "search");
                }
                public async Task NamedRequest(Order payload, CancellationToken ct)
                {
                    using var message = new HttpRequestMessage(requestUri: "patch", method: HttpMethod.Patch)
                        { Content = JsonContent.Create(payload) };
                    await _http.SendAsync(cancellationToken: ct, request: message);
                }
            }
            public record Order(int Id);
            """;
        fixture.Write("OrdersApi.cs", source);
        var result = await fixture.Analyze();
        Equal(7, result.WebApiCalls.Count, "通常・名前付き引数の HttpClient 通信を抽出");
        var load = result.WebApiCalls.Single(c => c.MemberName == "Load");
        Equal("GET", load.HttpMethod, "GetAsync の HTTP メソッド");
        True(load.Address.Contains("orders/{id}"), "補間文字列の要求パスを保持");
        True(load.ResponseType.Contains("Order"), "応答変換先の DTO 型");
        True(load.Processing.Any(p => p.Description.Contains("EnsureSuccessStatusCode")), "成功コード確認の根拠");
        True(load.Processing.Any(p => p.Kind.Contains("JSON") && p.Line == SourceLine(source, "var order = await response.Content.ReadFromJsonAsync")), "応答の JSON 変換の根拠行");
        True(load.Processing.Any(p => p.Kind.Contains("戻り値")), "応答データを呼び出し元へ返す処理");
        Equal(SourceLine(source, "var response = await _http.GetAsync"), load.Line, "呼び出しの行番号");
        var create = result.WebApiCalls.Single(c => c.MemberName == "Create");
        Equal("POST", create.HttpMethod, "PostAsJsonAsync の HTTP メソッド");
        True(create.RequestData.Contains("payload"), "送信データの式");
        True(create.Processing.Any(p => p.Description.Contains("IsSuccessStatusCode")), "失敗時のステータス分岐");
        True(create.Processing.Any(p => p.Description.Contains("HttpRequestException") || p.Kind.Contains("例外")), "送信を囲む例外処理");
        var list = result.WebApiCalls.Single(c => c.MemberName == "List");
        Equal("GET", list.HttpMethod, "GetFromJsonAsync の HTTP メソッド");
        True(list.ResponseType.Contains("Order[]"), "直接 JSON 呼び出しの配列型");
        var patch = result.WebApiCalls.Single(c => c.MemberName == "Patch");
        Equal("PATCH", patch.HttpMethod, "HttpRequestMessage のメソッドを SendAsync へ対応付ける");
        True(patch.Address.Contains("orders/42"), "HttpRequestMessage のパス");
        True(patch.RequestData.Contains("payload"), "RequestMessage.Content の送信データ");
        var namedPost = result.WebApiCalls.Single(c => c.MemberName == "NamedArgs" && c.HttpMethod == "POST");
        True(namedPost.Address.Contains("orders") && !namedPost.Address.Contains("payload"), "並べ替えた requestUri 引数を POST のパスとして読む");
        True(namedPost.RequestData.Contains("payload"), "並べ替えた value 引数を POST の送信データとして読む");
        var namedGet = result.WebApiCalls.Single(c => c.MemberName == "NamedArgs" && c.HttpMethod == "GET");
        True(namedGet.Address.Contains("search") && !namedGet.Address.Contains("ct"), "キャンセルトークンを GET のパスと取り違えない");
        var namedRequest = result.WebApiCalls.Single(c => c.MemberName == "NamedRequest");
        Equal("PATCH", namedRequest.HttpMethod, "並べ替えた RequestMessage の method 引数");
        True(namedRequest.Address.Contains("patch") && !namedRequest.Address.Contains("ct"), "SendAsync の request 引数を名前で対応付ける");
        True(namedRequest.RequestData.Contains("payload"), "名前付き RequestMessage にも Content を対応付ける");
        True(result.WebApiCalls.All(c => c.FilePath == "OrdersApi.cs" && c.TypeName.Contains("OrdersApi") && c.Client.Contains("_http") && c.Line > 0 && !string.IsNullOrWhiteSpace(c.Confidence)), "通信のクラス・クライアント・位置・確度");
        True(result.WebApiCalls.SelectMany(c => c.Processing).All(p => p.Line > 0), "処理内容にも根拠行を持つ");
    }

    private static async Task WebApiFlowIsolation()
    {
        using var fixture = new Fixture();
        fixture.Write("PairApi.cs", """
            using System.Net.Http;
            using System.Net.Http.Json;
            using System.Threading.Tasks;
            public class PairApi
            {
                private readonly HttpClient _client = new HttpClient();
                public async Task<object> Load()
                {
                    // _client.GetAsync("comment-only");
                    var note = "_client.PostAsync(\"string-only\", content)";
                    var first = await _client.GetAsync("first");
                    first.EnsureSuccessStatusCode();
                    var firstData = await first.Content.ReadFromJsonAsync<FirstDto>();
                    var second = await _client.GetAsync("second");
                    var secondData = await second.Content.ReadFromJsonAsync<SecondDto>();
                    return new { firstData, secondData };
                }
                public Task<string> Cache(Cache cache) => cache.GetAsync("cache-only");
            }
            public class Cache { public Task<string> GetAsync(string key) => null; }
            public class FirstDto {}
            public class SecondDto {}
            """);
        var result = await fixture.Analyze();
        Equal(2, result.WebApiCalls.Count, "コメント・文字列・独自 GetAsync を通信と扱わない");
        var first = result.WebApiCalls.Single(c => c.Address.Contains("first"));
        var second = result.WebApiCalls.Single(c => c.Address.Contains("second"));
        True(first.ResponseType.Contains("FirstDto") && !first.ResponseType.Contains("SecondDto"), "1 件目の応答型に 2 件目を混ぜない");
        True(second.ResponseType.Contains("SecondDto") && !second.ResponseType.Contains("FirstDto"), "2 件目の応答型に 1 件目を混ぜない");
        True(first.Processing.Any(p => p.Description.Contains("EnsureSuccessStatusCode")), "1 件目のステータス確認");
        True(!second.Processing.Any(p => p.Description.Contains("EnsureSuccessStatusCode")), "1 件目のステータス確認を 2 件目へ転記しない");
        True(!first.Processing.Any(p => p.Description.Contains("SecondDto")) && !second.Processing.Any(p => p.Description.Contains("FirstDto")), "別の応答変換を混ぜない");
        using var shadows = new Fixture();
        shadows.Write("ShadowedClients.cs", """
            using System.Net.Http;
            using System.Threading.Tasks;
            public class CacheBase { protected Cache client = new Cache(); }
            public class HttpField : CacheBase
            {
                private readonly HttpClient client = new HttpClient();
                public async Task Work(Cache client)
                {
                    await this.client.GetAsync("HTTP_FIELD");
                    await client.GetAsync("CACHE_PARAMETER");
                    await base.client.GetAsync("BASE_CACHE");
                }
            }
            public class CacheField
            {
                private readonly Cache client = new Cache();
                public async Task Work(HttpClient client)
                {
                    await client.GetAsync("HTTP_PARAMETER");
                    await this.client.GetAsync("CACHE_FIELD");
                }
            }
            public class Cache { public Task<string> GetAsync(string key) => Task.FromResult(key); }
            """);
        var shadowed = await shadows.Analyze();
        Equal(2, shadowed.WebApiCalls.Count, "this 指定と同名引数の解決で通信を誤検出・重複しない");
        True(shadowed.WebApiCalls.Any(c => c.TypeName == "HttpField" && c.Address == "HTTP_FIELD" && c.Client == "this.client"), "HttpClient フィールドは Cache 引数に隠れていても this で識別する");
        True(shadowed.WebApiCalls.Any(c => c.TypeName == "CacheField" && c.Address == "HTTP_PARAMETER" && c.Client == "client"), "HttpClient 引数は同名 Cache フィールドと区別する");
        True(!shadowed.WebApiCalls.Any(c => c.Address is "CACHE_PARAMETER" or "CACHE_FIELD" or "BASE_CACHE"), "引数・フィールド・base の Cache 呼び出しを通信に混入しない");
    }

    private static async Task WebApiMalformedAndReadOnly()
    {
        using var fixture = new Fixture();
        fixture.Write("appsettings.json", "{\n  \"ApiUrl\": \"https://broken.example.invalid/\",\n");
        fixture.Write("App.config", "<configuration>\n<appSettings><add key=\"ApiUrl\" value=\"https://broken.example.invalid/\"></appSettings>");
        fixture.Write("GoodApi.cs", "using System.Net.Http; public class GoodApi { public void Go(HttpClient client) { client.GetAsync(\"https://good.example.invalid/\"); } }");
        var before = fixture.Hashes();
        var result = await fixture.Analyze();
        True(result.Diagnostics.Any(d => d.FilePath == "appsettings.json" && d.Line > 0), "JSON の構文不正を診断に残す");
        True(result.Diagnostics.Any(d => d.FilePath == "App.config" && d.Line > 0), "XML の構文不正を診断に残す");
        Equal(1, result.WebApiCalls.Count, "設定ファイルが壊れていてもコードの通信解析を継続");
        var after = fixture.Hashes();
        Equal(before.Count, after.Count, "WebAPI 解析で対象ファイル数を変更しない");
        foreach (var (path, hash) in before) Equal(hash, after[path], $"WebAPI 解析は読み取り専用: {path}");
        using var cancelled = new CancellationTokenSource();
        var progress = new InlineProgress<AnalysisProgress>(p => { if (p.Completed >= 1) cancelled.Cancel(); });
        await ThrowsAsync<OperationCanceledException>(() => fixture.Analyze(progress: progress, cancellationToken: cancelled.Token));
    }

    private static async Task WebApiContextSafety()
    {
        var fixture = ContextFixture();
        var worker = fixture.Files[0] with
        {
            WebApiSettings = [new WebApiSettingInfo("Worker.cs", "JSON", "API|[Key]", "https://visible.example.invalid/|[path]", 3, "Declared")],
            WebApiCalls = [new WebApiCallInfo("Worker.cs", "Example.Worker", "Run", "client|[name]", "GET", "items|[path]", "payload|[value]", "Dto|[name]",
                [new ApiProcessingInfo("処理|[種別]", "結果|[説明]", 8)], 5, "Inferred")]
        };
        var contract = fixture.Files[1] with
        {
            WebApiSettings = [new WebApiSettingInfo("Contract.cs", "JSON", "UNRELATED_API_KEY", "https://unrelated.example.invalid/", 2, "Declared")],
            WebApiCalls = [new WebApiCallInfo("Contract.cs", "Example.IContract", "Run", "otherClient", "POST", "UNRELATED_API_PATH", "", "", [], 3, "Inferred")]
        };
        var result = fixture with { Files = [worker, contract] };
        var generator = new ContextGenerator();
        var selected = generator.Generate(result, new ContextOptions { Kind = ContextKind.Feature, SelectedFiles = ["Worker.cs"], RelatedDirection = RelatedFileDirection.None });
        True(selected.Markdown.Contains("## WebAPI の接続設定と通信処理"), "WebAPI 資料の節");
        True(selected.Markdown.Contains("https://visible.example.invalid/\\|\\[path\\]"), "接続先の Markdown 記号をエスケープ");
        foreach (var expected in new[] { "API\\|\\[Key\\]", "client\\|\\[name\\]", "items\\|\\[path\\]", "payload\\|\\[value\\]", "Dto\\|\\[name\\]", "処理\\|\\[種別\\]", "結果\\|\\[説明\\]" })
            True(selected.Markdown.Contains(expected), $"WebAPI のデータ由来のセルをエスケープ: {expected}");
        True(!selected.Markdown.Contains("unrelated.example.invalid") && !selected.Markdown.Contains("UNRELATED\\_API"), "選択範囲外の設定・通信を資料へ漏らさない");
        True(!selected.Markdown.Contains("BODY_ONLY_123"), "WebAPI 資料でも本文は明示 opt-in");
        var related = generator.Generate(result, new ContextOptions { Kind = ContextKind.Feature, SelectedFiles = ["Worker.cs"], RelatedDirection = RelatedFileDirection.Outgoing });
        True(related.Markdown.Contains("unrelated.example.invalid"), "関連範囲に含むファイルの設定も掲載");
        using var secretFixture = new Fixture();
        secretFixture.Write("appsettings.json", "{\"ApiUrl\":\"https://secure.example.invalid/api?access_token=CONFIG_SECRET\"}");
        secretFixture.Write("SecretsApi.cs", """
            using System.Net.Http;
            using System.Net.Http.Headers;
            using System.Net.Http.Json;
            public class SecretsApi
            {
                public void Send(HttpClient client)
                {
                    // BODY_ONLY_SECRET_FIXTURE
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "AUTH_SECRET");
                    client.PostAsJsonAsync("https://secure.example.invalid/items?api_key=URL_SECRET", new { Password = "BODY_SECRET", Name = "sample" });
                }
                public void ReadWithExpressions(HttpClient client, int id)
                {
                    client.GetAsync($"orders?api_key=INTERP_SECRET&id={id}");
                    client.GetAsync("orders?api_key=" + "CONCAT_SECRET");
                    client.GetAsync("orders?api%5Fkey=" + "ENCODED_CONCAT_SECRET");
                }
            }
            """);
        var secrets = await secretFixture.Analyze();
        Equal(4, secrets.WebApiCalls.Count, "マスキング検証用の通常・補間・連結・エンコードされた通信を抽出できている");
        True(secrets.WebApiCalls.Single(c => c.MemberName == "Send").Processing.Any(p => p.Kind.Contains("ヘッダー") || p.Kind.Contains("認証")), "認証ヘッダーの設定が処理の根拠として表示される");
        True(secrets.WebApiSettings.Any(s => s.Value.Contains("secure.example.invalid")), "マスキングで接続先情報すべてを失わない");
        var metadata = string.Join("\n", secrets.WebApiSettings.Select(s => s.Value)
            .Concat(secrets.WebApiCalls.Select(c => c.Address + " " + c.RequestData))
            .Concat(secrets.WebApiCalls.SelectMany(c => c.Processing).Select(p => p.Description)));
        foreach (var secret in new[] { "CONFIG_SECRET", "AUTH_SECRET", "URL_SECRET", "BODY_SECRET", "INTERP_SECRET", "CONCAT_SECRET", "ENCODED_CONCAT_SECRET" })
            True(!metadata.Contains(secret), $"UI が使う抽出情報にも機密値を含めない: {secret}");
        foreach (var kind in new[] { ContextKind.Overview, ContextKind.Structure })
        {
            var document = generator.Generate(secrets, new ContextOptions { Kind = kind });
            foreach (var secret in new[] { "CONFIG_SECRET", "AUTH_SECRET", "URL_SECRET", "BODY_SECRET", "INTERP_SECRET", "CONCAT_SECRET", "ENCODED_CONCAT_SECRET", "BODY_ONLY_SECRET_FIXTURE" })
                True(!document.Markdown.Contains(secret) && !document.Markdown.Contains(secret.Replace("_", "\\_")), $"{kind} の自動資料に機密値・本文を含めない: {secret}");
            True(document.Markdown.Contains("secure.example.invalid"), "マスクした自動資料にも接続先のホストを残す");
        }
    }

    private static int SourceLine(string source, string marker)
    {
        var offset = source.IndexOf(marker, StringComparison.Ordinal);
        True(offset >= 0, $"行番号検証用の文字列が存在する: {marker}");
        return 1 + source[..offset].Count(c => c == '\n');
    }

    private static Task ContextKindsAndPrompts()
    {
        var result = ContextFixture();
        var generator = new ContextGenerator();
        foreach (var kind in new[] { ContextKind.Overview, ContextKind.Structure })
        {
            var document = generator.Generate(result, new ContextOptions { Kind = kind });
            True(document.Markdown.Contains("ExampleApp") && document.Markdown.Contains("Worker"), $"{kind} にプロジェクト・型");
            True(document.Markdown.Contains("Worker.cs"), $"{kind} にパス");
            Equal(document.Markdown.Length, document.CharacterCount, "文字数");
        }
        var prompts = Enum.GetValues<PromptPurpose>().Select(purpose => generator.Generate(result, new ContextOptions { Purpose = purpose, Request = "追加要件: 画面の応答を改善する" }).Markdown).ToArray();
        Equal(6, prompts.Distinct(StringComparer.Ordinal).Count(), "用途別に異なる指示文");
        True(prompts.All(text => text.Contains("追加要件: 画面の応答を改善する")), "利用者の追加指示");
        return Task.CompletedTask;
    }

    private static Task FeatureAndSource()
    {
        var generator = new ContextGenerator();
        var result = ContextFixture();
        var normal = generator.Generate(result, new ContextOptions { Kind = ContextKind.Structure });
        True(!normal.Markdown.Contains("BODY_ONLY_123") && !normal.Markdown.Contains("UNSELECTED_BODY_456"), "初期設定でソース本文を出さない");
        Throws<ArgumentException>(() => generator.Generate(result, new ContextOptions { Kind = ContextKind.Feature }));
        var selected = generator.Generate(result, new ContextOptions { Kind = ContextKind.Feature, SelectedFiles = ["Worker.cs"], IncludeSource = true });
        True(selected.Markdown.Contains("BODY_ONLY_123"), "選択ファイルだけ本文を含む");
        True(!selected.Markdown.Contains("UNSELECTED_BODY_456"), "関連ファイル本文を勝手に含めない");
        True(selected.Markdown.Contains("Contract.cs"), "関連ファイルの構造情報");
        var typeSelected = generator.Generate(result, new ContextOptions { Kind = ContextKind.Feature, SelectedTypes = ["Example.Worker"], IncludeSource = true });
        True(typeSelected.Markdown.Contains("BODY_ONLY_123") && !typeSelected.Markdown.Contains("UNSELECTED_BODY_456"), "型選択で定義ファイルの本文");
        return Task.CompletedTask;
    }

    private static Task FeatureSelectionDefaults()
    {
        var options = new ContextOptions();
        Equal(RelatedFileDirection.Both, options.RelatedDirection, "既定の方向は双方向");
        Equal(1, options.RelatedDepth, "既定は直接関連のみ");
        var result = RelationshipGraphFixture();
        var generator = new ContextGenerator();
        var defaults = generator.Generate(result, new ContextOptions { Kind = ContextKind.Feature, SelectedFiles = ["Graph/Seed.cs"] });
        var explicitDefaults = generator.Generate(result, new ContextOptions { Kind = ContextKind.Feature, SelectedFiles = ["Graph/Seed.cs"], RelatedDirection = RelatedFileDirection.Both, RelatedDepth = 1 });
        Equal(explicitDefaults.Markdown, defaults.Markdown, "未指定でも従来の双方向・1段と同じ資料");
        AssertFileScope(result, defaults, "Graph/Seed.cs", "Graph/Out1.cs", "Graph/In1.cs");
        var selectedOnly = generator.Generate(result, new ContextOptions { Kind = ContextKind.Feature, SelectedFiles = ["Graph/Seed.cs"], RelatedDirection = RelatedFileDirection.None, RelatedDepth = 3, IncludeSource = true });
        AssertFileScope(result, selectedOnly, "Graph/Seed.cs");
        True(selectedOnly.Markdown.Contains("SOURCE_Seed"), "選択ファイルの本文を含む");
        True(!selectedOnly.Markdown.Contains("SOURCE_Out1") && !selectedOnly.Markdown.Contains("SOURCE_In1"), "選択ファイルのみなら関連本文も含まない");
        Equal(1, SelectedScopeRows(selectedOnly).Count, "選択のみの範囲表は1ファイル");
        var split = generator.Generate(result, new ContextOptions { Kind = ContextKind.Feature, SelectedFiles = ["Graph/Seed.cs"], RelatedDirection = RelatedFileDirection.Both, RelatedDepth = 3, MaxPartCharacters = 1000 });
        True(split.Parts.Count > 1, "機能別資料を小さい文字数上限で分割");
        var reconstructed = new StringBuilder();
        foreach (var part in split.Parts)
        {
            True(part.Text.Length <= 1000, "機能別の範囲情報を含むヘッダー込みで文字数上限内");
            var bodyStart = part.Text.IndexOf(BodyDelimiter, StringComparison.Ordinal);
            True(bodyStart >= 0 && part.Text[..bodyStart].Contains(split.Scope), "各パートのヘッダーに機能別の範囲情報を保持");
            reconstructed.Append(part.Text[(bodyStart + BodyDelimiter.Length)..]);
        }
        Equal(split.Markdown, reconstructed.ToString(), "機能別の範囲表も分割後に欠落なく復元");
        return Task.CompletedTask;
    }

    private static Task FeatureDirectionDepths()
    {
        var result = RelationshipGraphFixture();
        var generator = new ContextGenerator();
        foreach (var direction in new[] { RelatedFileDirection.Outgoing, RelatedFileDirection.Incoming, RelatedFileDirection.Both })
        {
            for (var depth = 1; depth <= 3; depth++)
            {
                var expected = new List<string> { "Graph/Seed.cs" };
                if (direction is RelatedFileDirection.Outgoing or RelatedFileDirection.Both)
                    expected.AddRange(Enumerable.Range(1, depth).Select(i => $"Graph/Out{i}.cs"));
                if (direction is RelatedFileDirection.Incoming or RelatedFileDirection.Both)
                    expected.AddRange(Enumerable.Range(1, depth).Select(i => $"Graph/In{i}.cs"));
                var document = generator.Generate(result, new ContextOptions { Kind = ContextKind.Feature, SelectedFiles = ["Graph/Seed.cs"], RelatedDirection = direction, RelatedDepth = depth });
                AssertFileScope(result, document, expected.ToArray());
                var rows = SelectedScopeRows(document);
                Equal(expected.Count, rows.Count, $"{direction} / {depth}段: 循環・重複辺でファイルを増やさない");
                Equal(0, rows["Graph/Seed.cs"].Hops, "選択した起点は0段");
                foreach (var path in expected.Skip(1))
                    Equal(int.Parse(Path.GetFileNameWithoutExtension(path)[^1..]), rows[path].Hops, $"{path}の最短段数");
            }
        }
        return Task.CompletedTask;
    }

    private static Task FeatureScopeIsolation()
    {
        var result = RelationshipGraphFixture();
        var generator = new ContextGenerator();
        foreach (var direction in Enum.GetValues<RelatedFileDirection>())
        {
            var document = generator.Generate(result, new ContextOptions { Kind = ContextKind.Feature, SelectedFiles = ["Graph/Seed.cs"], RelatedDirection = direction, RelatedDepth = 3 });
            True(!document.Markdown.Contains("Graph/Missing.cs") && !document.Markdown.Contains("Graph/UnknownProject.csproj"), "未解析の端点を関係表へ出さない");
            True(!document.Markdown.Contains("Graph/MissingBridgeTarget.cs") && !document.Markdown.Contains("Graph/ProjectBridgeTarget.cs"), "未解析端点・プロジェクト宣言を介して別ファイルへ進まない");
            True(!document.Markdown.Contains("Graph/Unrelated.cs"), "無関係なファイルパスを含めない");
            True(!ContainsData(document, "EDGE_OUT_3_4") && !ContainsData(document, "EDGE_IN_4_3"), "片方だけ範囲内の辺は掲載しない");
            True(!ContainsData(document, "DIAG_UNRELATED"), "無関係な診断を掲載しない");
            True(ContainsData(document, "EDGE_SELF"), "両端が選択範囲内なら自己参照も掲載する");
        }
        // 全体資料は関連設定で範囲を変えず、プロジェクト参照宣言も従来どおり掲載する。
        foreach (var kind in new[] { ContextKind.Overview, ContextKind.Structure })
        {
            var document = generator.Generate(result, new ContextOptions { Kind = kind, RelatedDirection = RelatedFileDirection.None });
            AssertFileScope(result, document, result.Files.Select(f => f.RelativePath).ToArray());
            True(ContainsData(document, "EDGE_PROJECT_DECLARATION"), "全体資料にはプロジェクト参照宣言も残す");
        }
        return Task.CompletedTask;
    }

    private static Task FeatureNormalizedPaths()
    {
        var files = new[] { GraphFile("Graph/Seed.cs"), GraphFile(@"Graph\Child.cs"), GraphFile("Graph/Leaf.cs"), GraphFile("Graph/Unrelated.cs") };
        var result = GraphResult(files,
            [new FileRelationship(@"GRAPH\SEED.CS", "graph/child.cs", "Reference", "EDGE_NORMALIZED_CHILD", "Inferred"),
             new FileRelationship("GRAPH/CHILD.CS", @"graph\leaf.cs", "Reference", "EDGE_NORMALIZED_LEAF", "Inferred")],
            [new AnalysisDiagnostic(@"GRAPH\CHILD.CS", 1, "Info", "DIAG_NORMALIZED_CHILD"), new AnalysisDiagnostic("graph/unrelated.cs", 1, "Info", "DIAG_UNRELATED")]);
        var document = new ContextGenerator().Generate(result, new ContextOptions { Kind = ContextKind.Feature, SelectedFiles = [@"graph\seed.cs"], RelatedDirection = RelatedFileDirection.Outgoing, RelatedDepth = 2 });
        var rows = SelectedScopeRows(document);
        Equal(3, rows.Count, "区切りと大小文字が混在しても2段を展開");
        Equal(0, rows["Graph/Seed.cs"].Hops, "選択パスを正規化");
        Equal(1, rows[@"Graph\\Child.cs"].Hops, "元のパス表示を保持して1段の関連を判定");
        Equal(2, rows["Graph/Leaf.cs"].Hops, "混在する参照パスを正規化して次の段へ進む");
        True(ContainsData(document, "EDGE_NORMALIZED_CHILD") && ContainsData(document, "EDGE_NORMALIZED_LEAF"), "範囲内の正規化した辺を掲載");
        True(ContainsData(document, "DIAG_NORMALIZED_CHILD") && !ContainsData(document, "DIAG_UNRELATED"), "診断の範囲判定もパスを正規化");
        return Task.CompletedTask;
    }

    private static Task FeatureMultipleSelections()
    {
        var result = RelationshipGraphFixture();
        var document = new ContextGenerator().Generate(result, new ContextOptions
        {
            Kind = ContextKind.Feature, SelectedFiles = ["Graph/Seed.cs", @"GRAPH\SEED.CS", "Graph/Out2.cs"],
            RelatedDirection = RelatedFileDirection.Outgoing, RelatedDepth = 2, IncludeSource = true
        });
        AssertFileScope(result, document, "Graph/Seed.cs", "Graph/Out1.cs", "Graph/Out2.cs", "Graph/Out3.cs", "Graph/Out4.cs");
        var rows = SelectedScopeRows(document);
        Equal(5, rows.Count, "重複選択・重複辺を1ファイルへまとめる");
        foreach (var path in new[] { "Graph/Seed.cs", "Graph/Out2.cs" })
        {
            Equal(0, rows[path].Hops, "明示選択は他の起点から距離があっても0段");
            Equal("明示選択", rows[path].Selection, "明示選択を補足から区別する");
        }
        Equal(1, rows["Graph/Out3.cs"].Hops, "全起点からの最短距離を採用");
        Equal(2, rows["Graph/Out4.cs"].Hops, "補足起点の次の段を展開");
        Equal("関連による補足", rows["Graph/Out3.cs"].Selection, "自動追加を明示する");
        True(document.Markdown.Contains("SOURCE_Seed") && document.Markdown.Contains("SOURCE_Out2"), "明示した複数ファイルの本文を含む");
        True(!document.Markdown.Contains("SOURCE_Out1") && !document.Markdown.Contains("SOURCE_Out3") && !document.Markdown.Contains("SOURCE_Out4"), "深い関連でも補足の本文は自動追加しない");
        return Task.CompletedTask;
    }

    private static async Task FeatureAnalyzedReferences()
    {
        using var fixture = new Fixture();
        fixture.Write("Service.cs", "namespace Example; public partial class Service : Contract { /* PART_ONE_BODY */ }");
        fixture.Write("Service.Part.cs", "namespace Example; public partial class Service { /* PART_TWO_BODY */ }");
        fixture.Write("Contract.cs", "namespace Example; public class Contract { /* CONTRACT_BODY */ }");
        fixture.Write("Consumer.cs", "namespace Example; public class Consumer : Service { /* CONSUMER_BODY */ }");
        fixture.Write("Unrelated.cs", "namespace Example; public class Unrelated {}");
        var result = await fixture.Analyze();
        True(result.Relationships.Any(r => r.FromFile == "Service.cs" && r.ToFile == "Contract.cs"), "解析器が参照先への辺を生成");
        True(result.Relationships.Any(r => r.FromFile == "Consumer.cs" && r.ToFile == "Service.Part.cs"), "部分型の各宣言へ参照を解決");
        var generator = new ContextGenerator();
        var outgoing = generator.Generate(result, new ContextOptions { Kind = ContextKind.Feature, SelectedFiles = ["Service.cs"], RelatedDirection = RelatedFileDirection.Outgoing });
        AssertFileScope(result, outgoing, "Service.cs", "Contract.cs");
        var incoming = generator.Generate(result, new ContextOptions { Kind = ContextKind.Feature, SelectedFiles = ["Service.cs"], RelatedDirection = RelatedFileDirection.Incoming });
        AssertFileScope(result, incoming, "Service.cs", "Consumer.cs");
        var partial = generator.Generate(result, new ContextOptions { Kind = ContextKind.Feature, SelectedTypes = ["Example.Service"], RelatedDirection = RelatedFileDirection.Both, IncludeSource = true });
        AssertFileScope(result, partial, "Service.cs", "Service.Part.cs", "Contract.cs", "Consumer.cs");
        var rows = SelectedScopeRows(partial);
        Equal(0, rows["Service.cs"].Hops, "型選択の1番目の宣言も起点");
        Equal(0, rows["Service.Part.cs"].Hops, "部分型の別ファイルも明示起点");
        True(partial.Markdown.Contains("PART_ONE_BODY") && partial.Markdown.Contains("PART_TWO_BODY"), "型の全定義ファイルの本文を含む");
        True(!partial.Markdown.Contains("CONTRACT_BODY") && !partial.Markdown.Contains("CONSUMER_BODY"), "型選択でも関連本文は含まない");
    }

    private static Task FeatureOptionsValidation()
    {
        var result = RelationshipGraphFixture();
        var generator = new ContextGenerator();
        var valid = new ContextOptions { Kind = ContextKind.Feature, SelectedFiles = ["Graph/Seed.cs"] };
        foreach (var depth in new[] { int.MinValue, -1, 0, 4, int.MaxValue })
            Throws<ArgumentOutOfRangeException>(() => generator.Generate(result, valid with { RelatedDepth = depth }));
        foreach (var direction in new[] { (RelatedFileDirection)(-1), (RelatedFileDirection)4, (RelatedFileDirection)int.MaxValue })
            Throws<ArgumentOutOfRangeException>(() => generator.Generate(result, valid with { RelatedDirection = direction }));
        Throws<ArgumentException>(() => generator.Generate(result, valid with { SelectedFiles = ["Graph/Missing.cs"] }));
        // 設定は機能別資料にだけ適用し、概要・構造資料の既存呼び出しを妨げない。
        foreach (var kind in new[] { ContextKind.Overview, ContextKind.Structure })
            generator.Generate(result, new ContextOptions { Kind = kind, RelatedDepth = 0, RelatedDirection = (RelatedFileDirection)(-1) });
        using var before = new CancellationTokenSource();
        before.Cancel();
        Throws<OperationCanceledException>(() => generator.Generate(result, valid, before.Token));
        using var during = new CancellationTokenSource();
        var cancelling = result with { Relationships = new CancellingRelationships(result.Relationships, during) };
        Throws<OperationCanceledException>(() => generator.Generate(cancelling, valid with { RelatedDepth = 3 }, during.Token));
        True(during.IsCancellationRequested, "関係の列挙が始まった後のキャンセルも伝播");
        return Task.CompletedTask;
    }

    private static Task MarkdownEscaping()
    {
        var source = "public class Worker { /* BODY_ONLY_123\n```\n``````\n# 偽物の見出し\n*/ }";
        var result = ContextFixture(source: source, path: "資料|[日本語].cs");
        var generator = new ContextGenerator();
        var document = generator.Generate(result, new ContextOptions { Kind = ContextKind.Structure, IncludeSource = true });
        True(document.Markdown.Contains("資料\\|\\[日本語\\].cs"), "表中のパイプと角括弧をエスケープ");
        True(document.Markdown.Contains("```````csharp") || document.Markdown.Contains("```````cs"), "本文より長いバッククォートで囲む");
        True(document.Markdown.Contains(source), "ソース本文を改変しない");
        var fakeLink = generator.Generate(result with { ProjectName = "[リンク](https://example.invalid) *強調*" }, new ContextOptions());
        True(fakeLink.Markdown.Contains("\\[リンク\\](https://example.invalid) \\*強調\\*"), "データ由来のリンク・強調構文を無効にする");
        return Task.CompletedTask;
    }

    private static Task SplitLossless()
    {
        var source = string.Concat(Enumerable.Range(0, 1000).Select(i => $"// {i:D4} 日本語 🧪 解析を確認する\n"));
        var generator = new ContextGenerator();
        var document = generator.Generate(ContextFixture(source: source), new ContextOptions { Kind = ContextKind.Structure, IncludeSource = true, MaxPartCharacters = 1000 });
        True(document.Parts.Count > 2, "大きなコンテキストを分割");
        var reconstructed = new StringBuilder();
        for (var index = 0; index < document.Parts.Count; index++)
        {
            var part = document.Parts[index];
            Equal(index + 1, part.Number, "1 起点の連番");
            Equal(document.Parts.Count, part.Total, "総パート数");
            True(part.Text.Length <= 1000, "ヘッダーを含め上限内");
            var bodyStart = part.Text.IndexOf(BodyDelimiter, StringComparison.Ordinal);
            True(bodyStart >= 0, "本文開始マーカー");
            True(part.Text[..bodyStart].Contains("ExampleApp") && part.Text[..bodyStart].Contains(document.Scope), "各パートにプロジェクト名・範囲");
            var body = part.Text[(bodyStart + BodyDelimiter.Length)..];
            True(body.Length == 0 || !char.IsLowSurrogate(body[0]) && !char.IsHighSurrogate(body[^1]), "サロゲートペアを境界で分断しない");
            reconstructed.Append(body);
        }
        Equal(document.Markdown, reconstructed.ToString(), "分割後も元の Markdown を一字も失わない");
        Throws<ArgumentOutOfRangeException>(() => generator.Generate(ContextFixture(), new ContextOptions { MaxPartCharacters = 999 }));
        // 改行がない行も分割でき、連番の桁数が増えても文字数を超えない。
        var singleLine = generator.Generate(ContextFixture(source: string.Concat(Enumerable.Repeat("🧪", 15000))), new ContextOptions { IncludeSource = true, MaxPartCharacters = 1000 });
        Equal(singleLine.Markdown, string.Concat(singleLine.Parts.Select(p => p.Text[(p.Text.IndexOf(BodyDelimiter, StringComparison.Ordinal) + BodyDelimiter.Length)..])), "長い単一行も欠落しない");
        True(singleLine.Parts.All(p => p.Text.Length <= 1000), "2 桁以上の連番でも上限内");
        return Task.CompletedTask;
    }

    private static async Task ExportSafety()
    {
        using var fixture = new Fixture();
        using var output = new Fixture("出力");
        fixture.Write("Worker.cs", "public class Worker {}");
        var document = new ContextGenerator().Generate(ContextFixture(), new ContextOptions());
        var exporter = new ContextExporter();
        var markdownPaths = await exporter.ExportAsync(document, output.Root, ".md", fixture.Root);
        Equal(document.Parts.Count, markdownPaths.Count, "全パートを保存");
        for (var index = 0; index < markdownPaths.Count; index++)
        {
            True(Path.GetFullPath(markdownPaths[index]).StartsWith(output.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "指定出力先に限定");
            Equal(document.Parts[index].Text, await File.ReadAllTextAsync(markdownPaths[index]), "UTF-8 Markdown 内容");
        }
        var textPaths = await exporter.ExportAsync(document, output.Root, ".txt", fixture.Root);
        True(textPaths.All(p => p.EndsWith(".txt", StringComparison.Ordinal)), "テキスト出力");
        var savedHashes = output.Hashes();
        try { await exporter.ExportAsync(document, output.Root, ".md", fixture.Root); }
        catch (IOException) { /* 同名時にエラー、または一意名への保存のどちらでも原本は維持する。 */ }
        foreach (var (path, hash) in savedHashes) Equal(hash, output.Hashes()[path], "既存出力を上書きしない");
        await ThrowsAnyAsync(() => exporter.ExportAsync(document, Path.Combine(fixture.Root, "Export"), ".md", fixture.Root));
        await ThrowsAnyAsync(() => exporter.ExportAsync(document, fixture.Root, ".md", fixture.Root));
        // 解析ルートがドライブ全体の場合も、区切り文字の重複で範囲判定をすり抜けない。
        await ThrowsAnyAsync(() => exporter.ExportAsync(document, output.Root, ".md", Path.GetPathRoot(output.Root)!));
        await ThrowsAnyAsync(() => exporter.ExportAsync(document, @"\\invalid-server\share\output", ".md", fixture.Root));
        await ThrowsAnyAsync(() => exporter.ExportAsync(document, "relative-output", ".md", fixture.Root));
        await ThrowsAnyAsync(() => exporter.ExportAsync(document, output.Root, ".exe", fixture.Root));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var cancellationOutput = new Fixture();
        await ThrowsAsync<OperationCanceledException>(() => exporter.ExportAsync(document, cancellationOutput.Root, ".md", fixture.Root, cancelled.Token));
        Equal(0, Directory.GetFiles(cancellationOutput.Root, "*", SearchOption.AllDirectories).Length, "キャンセル後に途中生成物を残さない");
    }

    private static AnalysisResult ContextFixture(string? source = null, string path = "Worker.cs")
    {
        var worker = new TypeInfo("Worker", "Example.Worker", "Example", "Class", "public", ["IContract"],
            [new MemberInfo("Run", "Method", "string", [new ParameterInfo("id", "int", "", null)], "public", 2)], path, 1);
        var contract = new TypeInfo("IContract", "Example.IContract", "Example", "Interface", "public", [],
            [new MemberInfo("Run", "Method", "string", [new ParameterInfo("id", "int", "", null)], "", 2)], "Contract.cs", 1);
        return new AnalysisResult("ExampleApp", Path.GetTempPath(),
            [new ProjectInfo("ExampleApp", "ExampleApp.csproj", "net10.0-windows", "WPF", ["../Core/Core.csproj"], [])],
            [new SourceFileInfo(path, Path.Combine(Path.GetTempPath(), path), "ExampleApp", "C#", "業務処理", ["Example"], ["System"], [worker], [], source ?? "public class Worker { /* BODY_ONLY_123 */ }"),
             new SourceFileInfo("Contract.cs", Path.Combine(Path.GetTempPath(), "Contract.cs"), "ExampleApp", "C#", "契約", ["Example"], [], [contract], [], "public interface IContract { /* UNSELECTED_BODY_456 */ }")],
            [new FileRelationship(path, "Contract.cs", "Inheritance", "IContract", "Estimated")], []);
    }

    private static AnalysisResult RelationshipGraphFixture()
    {
        var files = new[] { "Seed", "Out1", "Out2", "Out3", "Out4", "In1", "In2", "In3", "In4", "Unrelated", "MissingBridgeTarget", "ProjectBridgeTarget" }
            .Select(name => GraphFile($"Graph/{name}.cs")).ToArray();
        FileRelationship Edge(string from, string to, string evidence, string kind = "Reference") =>
            new($"Graph/{from}.cs", $"Graph/{to}.cs", kind, evidence, "Inferred");
        return GraphResult(files,
            [Edge("Seed", "Out1", "EDGE_OUT_0_1"), Edge("Out1", "Out2", "EDGE_OUT_1_2"), Edge("Out2", "Out3", "EDGE_OUT_2_3"), Edge("Out3", "Out4", "EDGE_OUT_3_4"),
             Edge("In1", "Seed", "EDGE_IN_1_0"), Edge("In2", "In1", "EDGE_IN_2_1"), Edge("In3", "In2", "EDGE_IN_3_2"), Edge("In4", "In3", "EDGE_IN_4_3"),
             Edge("Out2", "Out1", "EDGE_OUT_CYCLE"), Edge("In1", "In2", "EDGE_IN_CYCLE"), Edge("Seed", "Out1", "EDGE_DUPLICATE"), Edge("Seed", "Seed", "EDGE_SELF"),
             Edge("Seed", "Missing", "EDGE_MISSING"), Edge("Missing", "MissingBridgeTarget", "EDGE_MISSING_BRIDGE"),
             new FileRelationship("Graph/Seed.cs", "Graph/UnknownProject.csproj", "ProjectReferenceDeclaration", "EDGE_PROJECT_DECLARATION", "Confirmed"),
             new FileRelationship("Graph/UnknownProject.csproj", "Graph/ProjectBridgeTarget.cs", "Reference", "EDGE_PROJECT_BRIDGE", "Inferred")],
            [new AnalysisDiagnostic("Graph/Unrelated.cs", 1, "Info", "DIAG_UNRELATED")]);
    }

    private static SourceFileInfo GraphFile(string path)
    {
        var name = path.Replace('\\', '/').Split('/').Last().Replace(".cs", "", StringComparison.Ordinal);
        var type = new TypeInfo(name, "FeatureGraph." + name, "FeatureGraph", "Class", "public", [], [], path, 1);
        return new SourceFileInfo(path, Path.Combine(Path.GetTempPath(), path), "FeatureGraph", "C#", "fixture role", ["FeatureGraph"], [], [type], [], $"public class {name} {{ /* SOURCE_{name} */ }}");
    }

    private static AnalysisResult GraphResult(IReadOnlyList<SourceFileInfo> files, IReadOnlyList<FileRelationship> relationships, IReadOnlyList<AnalysisDiagnostic> diagnostics) =>
        new("FeatureGraph", Path.GetTempPath(), [new ProjectInfo("FeatureGraph", "FeatureGraph.csproj", "net10.0", "Library", [], [])], files, relationships, diagnostics);

    private static void AssertFileScope(AnalysisResult result, ContextDocument document, params string[] expected)
    {
        var expectedPaths = expected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in result.Files)
        {
            var heading = "### " + file.RelativePath.Replace("\\", "\\\\") + Environment.NewLine;
            Equal(expectedPaths.Contains(file.RelativePath), document.Markdown.Contains(heading, StringComparison.Ordinal), $"構造資料の範囲: {file.RelativePath}");
        }
    }

    private static bool ContainsData(ContextDocument document, string text) =>
        document.Markdown.Contains(text.Replace("_", "\\_"), StringComparison.Ordinal);

    private static Dictionary<string, (string Selection, int Hops)> SelectedScopeRows(ContextDocument document)
    {
        var rows = new Dictionary<string, (string Selection, int Hops)>(StringComparer.OrdinalIgnoreCase);
        var inSection = false;
        foreach (var line in document.Markdown.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            if (line == "## 選択範囲と関連ファイル") { inSection = true; continue; }
            if (!inSection) continue;
            if (line.StartsWith("## ", StringComparison.Ordinal)) break;
            var cells = line.Split('|').Select(cell => cell.Trim()).ToArray();
            if (cells.Length >= 5 && int.TryParse(cells[3], out var hops))
                True(rows.TryAdd(cells[1], (cells[2], hops)), "同じファイルを範囲表へ重複掲載しない");
        }
        True(inSection, "選択と補足の内訳を示す節がある");
        return rows;
    }

    private static string ProjectXml(string framework, string extras = "", string? projectReference = null) =>
        $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{framework}</TargetFramework>{extras}</PropertyGroup>" +
        (projectReference is null ? "" : $"<ItemGroup><ProjectReference Include=\"{projectReference}\" /></ItemGroup>") + "</Project>";

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected <{expected}> / actual <{actual}>");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"{typeof(T).Name} が必要です。");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"{typeof(T).Name} が必要です。");
    }

    private static async Task ThrowsAnyAsync(Func<Task> action)
    {
        try { await action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        catch (IOException) { return; }
        throw new InvalidOperationException("安全ではない保存先・拡張子を拒否する必要があります。");
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class CancellingRelationships(IReadOnlyList<FileRelationship> relationships, CancellationTokenSource cancellation) : IReadOnlyList<FileRelationship>
    {
        public int Count => relationships.Count;
        public FileRelationship this[int index] => relationships[index];
        public IEnumerator<FileRelationship> GetEnumerator()
        {
            for (var index = 0; index < relationships.Count; index++)
            {
                if (index == 1) cancellation.Cancel();
                yield return relationships[index];
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public Fixture(string name = "fixture")
        {
            Root = Path.Combine(Path.GetTempPath(), "ProjectLens.Tests", Guid.NewGuid().ToString("N"), name);
            Directory.CreateDirectory(Root);
        }

        public void Write(string relativePath, string text, Encoding? encoding = null)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        }

        public Task<AnalysisResult> Analyze(string? input = null, AnalysisOptions? options = null,
            IProgress<AnalysisProgress>? progress = null, CancellationToken cancellationToken = default) =>
            new ProjectAnalyzer().AnalyzeAsync(options ?? new AnalysisOptions { InputPath = input is null ? Root : Path.Combine(Root, input) }, progress, cancellationToken);

        public Dictionary<string, string> Hashes() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(Root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

        public void Dispose()
        {
            // 削除先はこのテストが生成した一意のテンポラリーディレクトリだけ。
            var parent = Path.GetDirectoryName(Root)!;
            if (parent.StartsWith(Path.Combine(Path.GetTempPath(), "ProjectLens.Tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(parent, recursive: true);
        }
    }
}
