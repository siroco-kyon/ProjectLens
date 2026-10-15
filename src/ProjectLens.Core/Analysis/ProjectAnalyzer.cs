using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ProjectLens.Core.Models;

namespace ProjectLens.Core.Analysis;

/// <summary>
/// ローカルファイルだけを読み取り、Roslyn の構文木からプロジェクト構造を作る。
/// MSBuild の評価・実行や NuGet 復元は、対象プロジェクトに含まれる処理を実行しないために行わない。
/// </summary>
public sealed class ProjectAnalyzer : IProjectAnalyzer
{
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".xaml", ".razor", ".cshtml", ".json", ".config", ".resx", ".xml", ".settings" };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex SolutionProject = new("^\\s*Project\\([^\\r\\n]*?\\)\\s*=\\s*\"[^\"]*\"\\s*,\\s*\"(?<path>[^\"]+\\.csproj)\"",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public Task<AnalysisResult> AnalyzeAsync(AnalysisOptions options, IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxFileBytes <= 0 || options.MaxFileBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), "ファイルサイズの上限は 1 ～ 2,147,483,647 バイトで指定してください。");
        // ファイル探索と構文解析は UI スレッドから分離する。キャンセルは内側の各処理にも渡す。
        return Task.Run(() => AnalyzeCoreAsync(options, progress, cancellationToken), cancellationToken);
    }

    private static async Task<AnalysisResult> AnalyzeCoreAsync(AnalysisOptions options, IProgress<AnalysisProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = PathPolicy.GetLocalInputPath(options.InputPath);
        var directoryInput = Directory.Exists(input);
        if (!directoryInput && !File.Exists(input)) throw new FileNotFoundException("解析対象が見つかりません。", input);
        var extension = directoryInput ? "" : Path.GetExtension(input).ToLowerInvariant();
        if (!directoryInput && extension is not (".sln" or ".slnx" or ".csproj"))
            throw new ArgumentException("フォルダー、.sln、.slnx、.csproj のいずれかを指定してください。", nameof(options));
        var root = directoryInput ? input : Path.GetDirectoryName(input)!;
        var policy = new PathPolicy(root, options);
        var diagnostics = new List<AnalysisDiagnostic>();
        var declaredProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        progress?.Report(new AnalysisProgress(0, 0, "対象範囲とプロジェクトを確認しています…"));

        if (directoryInput) sourceRoots.Add(root);
        else if (extension == ".csproj")
        {
            declaredProjects.Add(input);
            sourceRoots.Add(root);
        }
        else
        {
            var manifest = await ReadTextAsync(input, policy, options.MaxFileBytes, diagnostics, cancellationToken).ConfigureAwait(false);
            IEnumerable<string> projectPaths;
            if (extension == ".sln")
                projectPaths = SolutionProject.Matches(manifest).Select(match => match.Groups["path"].Value).ToArray();
            else
            {
                var xml = ParseXml(manifest);
                projectPaths = xml.Descendants().Where(e => e.Name.LocalName == "Project")
                    .Select(e => (string?)e.Attribute("Path")).OfType<string>().Where(p => Path.GetExtension(p).Equals(".csproj", StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            foreach (var path in projectPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (ContainsMsBuildExpression(path)) throw new ArgumentException("変数を含むプロジェクトパスは評価できません。");
                    var projectPath = policy.ResolveDeclaration(root, path);
                    if (!policy.ShouldTraverse(Path.GetDirectoryName(projectPath)!)) continue;
                    if (!File.Exists(projectPath))
                    {
                        diagnostics.Add(new AnalysisDiagnostic(policy.Relative(input), 0, "Warning", $"プロジェクトファイルが見つかりません: {path}"));
                        continue;
                    }
                    declaredProjects.Add(projectPath);
                    sourceRoots.Add(Path.GetDirectoryName(projectPath)!);
                }
                catch (Exception ex) when (IsFileFailure(ex))
                {
                    diagnostics.Add(new AnalysisDiagnostic(policy.Relative(input), 0, "Warning", $"プロジェクトを範囲外または安全に読めないため省略しました: {path} ({ex.Message})"));
                }
            }
            if (declaredProjects.Count == 0)
                diagnostics.Add(new AnalysisDiagnostic(policy.Relative(input), 0, "Warning", "選択範囲内に解析可能な C# プロジェクトの宣言がありません。"));
        }

        var declaredDirectories = directoryInput ? null : declaredProjects.Select(Path.GetDirectoryName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var discovered = Discover(sourceRoots, policy, diagnostics, progress, cancellationToken, declaredDirectories);
        if (directoryInput)
            declaredProjects.UnionWith(discovered.Where(path => Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase)));
        var projects = new List<ProjectDefinition>();
        var relationships = new List<FileRelationship>();
        foreach (var projectPath in declaredProjects.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // ファイルの明示範囲がプロジェクトの一部なら、範囲外の csproj 本文を読まず名前だけを付ける。
            if (!policy.ShouldRead(projectPath) && !projectPath.Equals(input, StringComparison.OrdinalIgnoreCase))
            {
                projects.Add(ProjectDefinition.Placeholder(projectPath, policy, "対象範囲外のため未読み取り"));
                continue;
            }
            try
            {
                var text = await ReadTextAsync(projectPath, policy, options.MaxFileBytes, diagnostics, cancellationToken).ConfigureAwait(false);
                projects.Add(ReadProject(projectPath, text, policy, diagnostics, relationships, cancellationToken));
            }
            catch (Exception ex) when (IsFileFailure(ex) || ex is XmlException or DecoderFallbackException)
            {
                diagnostics.Add(new AnalysisDiagnostic(policy.Relative(projectPath), ex is XmlException xml ? xml.LineNumber : 0,
                    "Error", $"プロジェクト定義を読み取れません: {ex.Message}"));
                projects.Add(ProjectDefinition.Placeholder(projectPath, policy, "読み取り失敗"));
            }
        }

        var sourcePaths = discovered.Where(path => SourceExtensions.Contains(Path.GetExtension(path))).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var projectDirectories = projects.GroupBy(p => p.Directory, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Info.Name, StringComparer.OrdinalIgnoreCase);
        var files = new List<ParsedSource>(sourcePaths.Length);
        progress?.Report(new AnalysisProgress(0, sourcePaths.Length, $"{sourcePaths.Length:N0} ファイルを解析します…"));
        for (var index = 0; index < sourcePaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = sourcePaths[index];
            var relative = policy.Relative(path);
            var projectName = FindProjectName(path, root, projectDirectories);
            try
            {
                var text = await ReadTextAsync(path, policy, options.MaxFileBytes, diagnostics, cancellationToken).ConfigureAwait(false);
                var parsed = Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                    ? SyntaxAnalyzer.Parse(text, path, relative, projectName, cancellationToken)
                    : DescribeOtherFile(text, path, relative, projectName, cancellationToken);
                files.Add(parsed);
                diagnostics.AddRange(parsed.File.Diagnostics);
            }
            catch (Exception ex) when (IsFileFailure(ex) || ex is DecoderFallbackException or XmlException)
            {
                var diagnostic = new AnalysisDiagnostic(relative, ex is XmlException xml ? xml.LineNumber : 0,
                    "Error", $"ファイルを解析できません: {ex.Message}");
                diagnostics.Add(diagnostic);
                files.Add(new ParsedSource(new SourceFileInfo(relative, path, projectName,
                    Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase) ? "CSharp" : "Other",
                    "読み取り・解析失敗", [], [], [], [diagnostic], null), [], [], [], new Dictionary<string, string>()));
            }
            progress?.Report(new AnalysisProgress(index + 1, sourcePaths.Length, $"解析済み {index + 1:N0}/{sourcePaths.Length:N0}: {relative}"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new AnalysisProgress(sourcePaths.Length, sourcePaths.Length, "構文からファイル間の関連候補を整理しています…"));
        BuildRelationships(files, relationships, diagnostics, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        diagnostics.Add(new AnalysisDiagnostic("", 0, "Info",
            "構文解析のみです。型・呼び出し先の意味的な解決、MSBuild条件、Compile項目、Directory.Build.props、外部依存の評価は行いません。ファイル間の型・XAML関連は推定で、ProjectReferenceの確定は宣言が存在することだけを意味します。"));
        var name = directoryInput ? Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) : Path.GetFileNameWithoutExtension(input);
        if (string.IsNullOrWhiteSpace(name)) name = root;
        return new AnalysisResult(name, root, projects.Select(p => p.Info).ToArray(), files.Select(f => f.File).ToArray(),
            relationships.OrderBy(r => r.FromFile, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ToFile, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Kind).ToArray(), diagnostics);
    }

    private static List<string> Discover(IEnumerable<string> sourceRoots, PathPolicy policy, ICollection<AnalysisDiagnostic> diagnostics,
        IProgress<AnalysisProgress>? progress, CancellationToken cancellationToken, IReadOnlySet<string>? declaredDirectories)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(sourceRoots.OrderDescending(StringComparer.OrdinalIgnoreCase));
        var visitedCount = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(directory) || !policy.ShouldTraverse(directory)) continue;
            try
            {
                policy.ValidateForRead(directory);
                var entries = new List<FileSystemInfo>();
                var unlistedProjectBoundary = false;
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (declaredDirectories is not null && !declaredDirectories.Contains(directory)
                        && entry is FileInfo && entry.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                    {
                        unlistedProjectBoundary = true;
                        break;
                    }
                    entries.Add(entry);
                }
                if (unlistedProjectBoundary)
                {
                    // 明示されたプロジェクトの配下でも、別プロジェクトの本文を自動で範囲に加えない。
                    diagnostics.Add(new AnalysisDiagnostic(policy.Relative(directory), 0, "Warning",
                        "未選択のプロジェクト定義を含むため、このフォルダー以下を省略しました。必要なら対象のソリューションに含めるか、フォルダー入力で範囲を明示してください。"));
                    continue;
                }
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        diagnostics.Add(new AnalysisDiagnostic(policy.Relative(entry.FullName), 0, "Warning", "リンク・再解析ポイントを省略しました。リンク先にはアクセスしていません。"));
                        continue;
                    }
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if (policy.ShouldTraverse(entry.FullName)) pending.Push(entry.FullName);
                    }
                    else if (policy.ShouldRead(entry.FullName) && (SourceExtensions.Contains(entry.Extension) || entry.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)))
                        result.Add(entry.FullName);
                }
            }
            catch (Exception ex) when (IsFileFailure(ex))
            {
                diagnostics.Add(new AnalysisDiagnostic(policy.Relative(directory), 0, "Warning", $"フォルダーを検索できません: {ex.Message}"));
            }
            if (++visitedCount % 25 == 0)
                progress?.Report(new AnalysisProgress(0, 0, $"検索済み {visitedCount:N0} フォルダー / 候補 {result.Count:N0} ファイル…"));
        }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string FindProjectName(string path, string root, IReadOnlyDictionary<string, string> projectDirectories)
    {
        // 各ファイルで全プロジェクトを走査せず、親ディレクトリをたどって所属候補を見つける。
        for (var directory = Path.GetDirectoryName(path); directory is not null && PathPolicy.IsWithin(directory, root); directory = Path.GetDirectoryName(directory))
            if (projectDirectories.TryGetValue(directory, out var name)) return name;
        return Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
    }

    private static ProjectDefinition ReadProject(string path, string text, PathPolicy policy, ICollection<AnalysisDiagnostic> diagnostics,
        ICollection<FileRelationship> relationships, CancellationToken cancellationToken)
    {
        var xml = ParseXml(text);
        var relative = policy.Relative(path);
        var directory = Path.GetDirectoryName(path)!;
        var frameworks = xml.Descendants().Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            .Select(e => e.Value.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var framework = frameworks.Length > 0 ? string.Join("; ", frameworks) : "不明（プロパティの評価なし）";
        var sdk = (string?)xml.Root?.Attribute("Sdk") ?? "";
        var appKind = HasTrueProperty(xml, "UseWPF") ? "WPF" : HasTrueProperty(xml, "UseWindowsForms") ? "Windows Forms"
            : sdk.Contains("Web", StringComparison.OrdinalIgnoreCase) ? "ASP.NET" : "C# / .NET";
        var references = new List<string>();
        foreach (var reference in xml.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var include = (string?)reference.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include)) continue;
            references.Add(include.Replace('\\', '/'));
            if (ContainsMsBuildExpression(include) || include.Contains('*') || include.Contains(';'))
            {
                diagnostics.Add(new AnalysisDiagnostic(relative, XmlLine(reference), "Info", $"ProjectReferenceを評価せず宣言だけを保持しました: {include}"));
                continue;
            }
            try
            {
                var resolved = policy.ResolveDeclaration(directory, include);
                relationships.Add(new FileRelationship(relative, policy.Relative(resolved), "ProjectReferenceDeclaration",
                    $"csproj の ProjectReference Include=\"{include}\"（条件未評価・存在や実際の依存を保証しません）", "Confirmed"));
            }
            catch (Exception ex) when (IsFileFailure(ex))
            {
                // 宣言文字列を表示することと参照先を読み取ることは区別する。
                diagnostics.Add(new AnalysisDiagnostic(relative, XmlLine(reference), "Warning", $"参照宣言は保持しましたが、参照先を解析しません: {include} ({ex.Message})"));
            }
        }
        var packages = xml.Descendants().Where(e => e.Name.LocalName == "PackageReference").Select(e =>
        {
            var name = (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update") ?? "";
            var version = (string?)e.Attribute("Version") ?? e.Elements().FirstOrDefault(v => v.Name.LocalName == "Version")?.Value;
            return string.IsNullOrWhiteSpace(version) ? name : $"{name} ({version})";
        }).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new ProjectDefinition(new ProjectInfo(Path.GetFileNameWithoutExtension(path), relative, framework, appKind,
            references.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), packages), directory);
    }

    private static ParsedSource DescribeOtherFile(string text, string fullPath, string relative, string projectName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isXaml = Path.GetExtension(fullPath).Equals(".xaml", StringComparison.OrdinalIgnoreCase);
        string? xamlClass = null;
        var diagnostics = new List<AnalysisDiagnostic>();
        if (isXaml)
        {
            try
            {
                var xml = ParseXml(text);
                xamlClass = (string?)xml.Root?.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"));
            }
            catch (XmlException ex) { diagnostics.Add(new AnalysisDiagnostic(relative, ex.LineNumber, "Warning", $"XAMLを構造として読み取れません: {ex.Message}")); }
        }
        var webApiSettings = WebApiAnalyzer.AnalyzeConfiguration(text, relative, diagnostics, cancellationToken);
        return new ParsedSource(new SourceFileInfo(relative, fullPath, projectName, isXaml ? "XAML" : "Other",
            SyntaxAnalyzer.RoleHint(relative), [], [], [], diagnostics, text) { WebApiSettings = webApiSettings }, [], [], [], new Dictionary<string, string>(), xamlClass);
    }

    private static void BuildRelationships(IReadOnlyList<ParsedSource> files, ICollection<FileRelationship> relationships,
        ICollection<AnalysisDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var declarations = files.SelectMany(f => f.Declarations).ToArray();
        var fullIndex = declarations.GroupBy(d => d.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var simpleIndex = declarations.GroupBy(d => d.SimpleKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var added = new HashSet<(string From, string To, string Kind)>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in file.References)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidates = ResolveCandidates(file, reference.Key, fullIndex, simpleIndex);
                if (candidates.Select(c => c.Key).Distinct(StringComparer.Ordinal).Take(2).Count() > 1)
                {
                    ambiguous.Add(reference.Key);
                    continue;
                }
                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = candidate.Type.FilePath;
                    if (target.Equals(file.File.RelativePath, StringComparison.OrdinalIgnoreCase) || !added.Add((file.File.RelativePath, target, reference.Kind))) continue;
                    relationships.Add(new FileRelationship(file.File.RelativePath, target, reference.Kind,
                        reference.Evidence + " → " + candidate.Type.FullName + "（構文名の一致・意味解決なし）", "Inferred"));
                }
            }
            if (ambiguous.Count > 0)
                diagnostics.Add(new AnalysisDiagnostic(file.File.RelativePath, 0, "Info",
                    $"同名の型候補が複数あるため関連を作成しませんでした: {string.Join(", ", ambiguous.Order(StringComparer.Ordinal).Take(20))}{(ambiguous.Count > 20 ? " …" : "")}"));
            if (file.XamlClass is { Length: > 0 } xamlClass && fullIndex.TryGetValue(xamlClass, out var xamlTypes))
                foreach (var candidate in xamlTypes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (added.Add((file.File.RelativePath, candidate.Type.FilePath, "XamlCodeBehind")))
                        relationships.Add(new FileRelationship(file.File.RelativePath, candidate.Type.FilePath, "XamlCodeBehind",
                            "XAML の x:Class=\"" + xamlClass + "\" と C# 型名の一致（ビルドによる検証なし）", "Inferred"));
                }
        }
    }

    private static TypeDeclarationInfo[] ResolveCandidates(ParsedSource file, string key,
        IReadOnlyDictionary<string, TypeDeclarationInfo[]> fullIndex, IReadOnlyDictionary<string, TypeDeclarationInfo[]> simpleIndex)
    {
        var firstSeparator = key.IndexOf('.');
        var aliasName = firstSeparator < 0 ? key : key[..firstSeparator];
        if (file.Aliases.TryGetValue(aliasName, out var aliasTarget)) key = aliasTarget + (firstSeparator < 0 ? "" : key[firstSeparator..]);
        if (key.Contains('.') && fullIndex.TryGetValue(key, out var exact)) return exact;
        var matches = new List<TypeDeclarationInfo>();
        foreach (var namespaceName in file.File.Namespaces.Where(n => n != "(global)").Concat(file.ImportedNamespaces).Distinct(StringComparer.Ordinal))
            if (fullIndex.TryGetValue(namespaceName + "." + key, out var scoped)) matches.AddRange(scoped);
        if (matches.Count > 0) return matches.Distinct().ToArray();
        // 修飾名が一致しない場合、末尾の名前だけで別名前空間へ結び付けない。
        return !key.Contains('.') && !key.Contains("::", StringComparison.Ordinal) && simpleIndex.TryGetValue(key, out var simple) ? simple : [];
    }

    private static async Task<string> ReadTextAsync(string path, PathPolicy policy, long maxBytes, ICollection<AnalysisDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        policy.ValidateForRead(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maxBytes) throw new IOException($"ファイルサイズが上限 {maxBytes:N0} バイトを超えています。");
        using var buffer = new MemoryStream((int)stream.Length);
        var chunk = new byte[65536];
        int count;
        // 読み取り中にファイルが大きくなっても、上限を越えてメモリを確保しない。
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length + count > maxBytes) throw new IOException($"ファイルサイズが上限 {maxBytes:N0} バイトを超えています。");
            buffer.Write(chunk, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return StrictUtf8.GetString(bytes[3..]);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })) return new UTF32Encoding(false, true, true).GetString(bytes[4..]);
        if (bytes.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF })) return new UTF32Encoding(true, true, true).GetString(bytes[4..]);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) return new UnicodeEncoding(false, true, true).GetString(bytes[2..]);
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) return new UnicodeEncoding(true, true, true).GetString(bytes[2..]);
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException)
        {
            // 古い Visual Studio の BOM なし日本語ファイルに対応する。曖昧な文字コードを無音で置換しない。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var text = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes);
            diagnostics.Add(new AnalysisDiagnostic(policy.Relative(path), 0, "Info", "UTF-8 として無効なため、BOM なし CP932 として読み取りました。"));
            return text;
        }
    }

    private static XDocument ParseXml(string text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = text.Length + 1L };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        return XDocument.Load(reader, LoadOptions.SetLineInfo);
    }

    private static bool HasTrueProperty(XDocument xml, string name) => xml.Descendants().Any(e => e.Name.LocalName == name && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
    private static int XmlLine(XElement element) => ((IXmlLineInfo)element).HasLineInfo() ? ((IXmlLineInfo)element).LineNumber : 0;
    private static bool ContainsMsBuildExpression(string text) => text.Contains("$(", StringComparison.Ordinal) || text.Contains("@(", StringComparison.Ordinal) || text.Contains("%(", StringComparison.Ordinal);
    private static bool IsFileFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException or NotSupportedException;

    private sealed record ProjectDefinition(ProjectInfo Info, string Directory)
    {
        public static ProjectDefinition Placeholder(string path, PathPolicy policy, string reason) => new(
            new ProjectInfo(Path.GetFileNameWithoutExtension(path), policy.Relative(path), reason, "不明", [], []), Path.GetDirectoryName(path)!);
    }
}
