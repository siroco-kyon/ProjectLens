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
            ("概要・構造・6 用途の指示文", ContextKindsAndPrompts),
            ("機能選択とソース本文の明示 opt-in", FeatureAndSource),
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
