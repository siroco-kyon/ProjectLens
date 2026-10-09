namespace ProjectLens.Core.Models;

// UIと解析エンジンの間で共有する読み取り専用の結果。対象コードへの書き込み機能は持たせない。
public sealed record AnalysisOptions
{
    public required string InputPath { get; init; }
    public IReadOnlyList<string> ExcludedDirectories { get; init; } = ["bin", "obj", ".git", ".vs", "node_modules", "packages"];
    public IReadOnlyList<string> IncludedRelativePaths { get; init; } = [];
    public long MaxFileBytes { get; init; } = 4 * 1024 * 1024;
}

public sealed record AnalysisProgress(int Completed, int Total, string Message);
public sealed record AnalysisDiagnostic(string FilePath, int Line, string Severity, string Message);
public sealed record ParameterInfo(string Name, string Type, string Modifier, string? DefaultValue)
{
    public string Display => $"{Modifier} {Type} {Name}{(DefaultValue is null ? "" : " = " + DefaultValue)}".Trim();
}
public sealed record MemberInfo(string Name, string Kind, string ReturnType, IReadOnlyList<ParameterInfo> Parameters, string Modifiers, int Line)
{
    public string Signature => Kind == "Property" ? $"{Modifiers} {ReturnType} {Name}".Trim()
        : $"{Modifiers} {ReturnType} {Name}({string.Join(", ", Parameters.Select(p => p.Display))})".Trim();
}
public sealed record TypeInfo(string Name, string FullName, string Namespace, string Kind, string Modifiers,
    IReadOnlyList<string> BaseTypes, IReadOnlyList<MemberInfo> Members, string FilePath, int Line);
public sealed record SourceFileInfo(string RelativePath, string FullPath, string ProjectName, string Kind, string RoleHint,
    IReadOnlyList<string> Namespaces, IReadOnlyList<string> Usings, IReadOnlyList<TypeInfo> Types,
    IReadOnlyList<AnalysisDiagnostic> Diagnostics, string? SourceText);
public sealed record ProjectInfo(string Name, string RelativePath, string Framework, string AppKind,
    IReadOnlyList<string> ProjectReferences, IReadOnlyList<string> PackageReferences);
public sealed record FileRelationship(string FromFile, string ToFile, string Kind, string Evidence, string Confidence);
public sealed record AnalysisResult(string ProjectName, string RootPath, IReadOnlyList<ProjectInfo> Projects,
    IReadOnlyList<SourceFileInfo> Files, IReadOnlyList<FileRelationship> Relationships, IReadOnlyList<AnalysisDiagnostic> Diagnostics)
{
    public IReadOnlyList<TypeInfo> Types => Files.SelectMany(f => f.Types).ToArray();
    // partial宣言が複数ファイルに分かれていても、同一プロジェクトの同じ型は一つとして数える。
    public int ClassCount => Files.SelectMany(f => f.Types.Where(t => t.Kind is "Class" or "Record")
        .Select(t => (f.ProjectName, t.FullName))).Distinct().Count();
    public int MethodCount => Types.Sum(t => t.Members.Count(m => m.Kind is "Method" or "Constructor"));
}
