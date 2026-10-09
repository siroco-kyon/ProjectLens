namespace ProjectLens.Core.Models;

public enum ContextKind { Overview, Structure, Feature }
public enum PromptPurpose { UnderstandProject, AnalyzeFeature, ImproveFeature, AssessImpact, Refactor, InvestigateBug }
public sealed record ContextOptions
{
    public ContextKind Kind { get; init; } = ContextKind.Overview;
    public PromptPurpose Purpose { get; init; } = PromptPurpose.UnderstandProject;
    public IReadOnlyList<string> SelectedFiles { get; init; } = [];
    public IReadOnlyList<string> SelectedTypes { get; init; } = [];
    public bool IncludeSource { get; init; }
    public string Request { get; init; } = "";
    public int MaxPartCharacters { get; init; } = 12000;
}
public sealed record ContextPart(int Number, int Total, string Text)
{
    public string Label => $"{Number} / {Total} ({Text.Length:N0} 文字)";
}
public sealed record ContextDocument(string ProjectName, string Scope, string Markdown, IReadOnlyList<ContextPart> Parts)
{
    public int CharacterCount => Markdown.Length;
}
