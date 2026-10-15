using System.Collections.ObjectModel;
using ProjectLens.Core.Models;

namespace ProjectLens.App.ViewModels;

public sealed record Choice<T>(string Label, T Value);

public sealed class FileItem(SourceFileInfo file, Action changed) : ObservableObject
{
    private bool _inContext;
    private string _roleNote = file.RoleHint;
    public SourceFileInfo File { get; } = file;
    public string RelativePath => File.RelativePath;
    public string ProjectName => File.ProjectName;
    public string Kind => File.Kind;
    public string NamespacesText => string.Join("\n", File.Namespaces);
    public string UsingsText => string.Join("\n", File.Usings);
    public string TypesText => string.Join("\n", File.Types.Select(t => t.FullName));
    public bool InContext
    {
        get => _inContext;
        set { if (Set(ref _inContext, value)) changed(); }
    }
    public string RoleNote
    {
        get => _roleNote;
        set { if (Set(ref _roleNote, value)) changed(); }
    }
}

public sealed class TypeItem(TypeInfo type, Action changed) : ObservableObject
{
    private bool _inContext;
    public TypeInfo Type { get; } = type;
    public string FullName => Type.FullName;
    public string Namespace => Type.Namespace;
    public string Kind => Type.Kind;
    public string FilePath => Type.FilePath;
    public int Line => Type.Line;
    public string Bases => string.Join(", ", Type.BaseTypes);
    public string KindLabel => Labels.MemberKind(Type.Kind);
    public bool InContext
    {
        get => _inContext;
        set { if (Set(ref _inContext, value)) changed(); }
    }
}

public sealed record MemberItem(string TypeName, string FilePath, MemberInfo Member)
{
    public string Name => Member.Name;
    public string KindLabel => Labels.MemberKind(Member.Kind);
    public string Signature => Member.Signature;
    public string ReturnType => Member.ReturnType;
    public string Parameters => string.Join(", ", Member.Parameters.Select(p => p.Display));
    public int Line => Member.Line;
}

public sealed record ProjectItem(ProjectInfo Project)
{
    public string Name => Project.Name;
    public string RelativePath => Project.RelativePath;
    public string Framework => Project.Framework;
    public string AppKind => Project.AppKind;
    public string References => string.Join("\n", Project.ProjectReferences);
    public string Packages => string.Join("\n", Project.PackageReferences);
}

public sealed record RelationshipItem(FileRelationship Relationship)
{
    public string FromFile => Relationship.FromFile;
    public string ToFile => Relationship.ToFile;
    public string Kind => Relationship.Kind;
    public string Evidence => Relationship.Evidence;
    public string Confidence => Relationship.Confidence;
}

public sealed record WebApiCallItem(WebApiCallInfo Call)
{
    public string FilePath => Call.FilePath;
    public int Line => Call.Line;
    public string Caller => string.Join(".", new[] { Call.TypeName, Call.MemberName }.Where(value => value.Length > 0));
    public string Client => Call.Client;
    public string HttpMethod => Call.HttpMethod;
    public string Address => Call.Address;
    public string RequestData => Call.RequestData;
    public string ResponseType => Call.ResponseType;
    public string Confidence => Labels.ApiConfidence(Call.Confidence);
    public string Location => $"{FilePath}:{Line}";
    public string RequestText => string.IsNullOrWhiteSpace(RequestData) ? "記述から特定できません。" : RequestData;
    public string ResponseText => string.IsNullOrWhiteSpace(ResponseType) ? "記述から特定できません。" : ResponseType;
    public IReadOnlyList<ApiProcessingInfo> Processing => Call.Processing;
    public string ProcessingText => string.Join("\n", Processing.Select(item => $"{item.Kind} {item.Description}"));
    public string ProcessingHint => Processing.Count == 0 ? "周辺処理の記述を検出できませんでした。" : "同じメソッド内などの記述です。各呼び出しとの対応は確認が必要です。";
}

public sealed record WebApiSettingItem(WebApiSettingInfo Setting)
{
    public string FilePath => Setting.FilePath;
    public int Line => Setting.Line;
    public string Kind => Setting.Kind;
    public string Key => Setting.Key;
    public string Value => Setting.Value;
    public string Confidence => Labels.ApiConfidence(Setting.Confidence);
    public string Location => $"{FilePath}:{Line}";
}

public sealed class TreeNode(string name, string path, FileItem? file = null)
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public FileItem? File { get; } = file;
    public bool HasFile => File is not null;
    public string Icon => HasFile ? "◇" : "▸";
    public bool IsExpanded { get; set; } = true;
    public ObservableCollection<TreeNode> Children { get; } = [];
}

internal static class Labels
{
    public static string ApiConfidence(string confidence) => confidence switch
    {
        "Declared" => "設定記述", "Inferred" => "構文候補", _ => "要確認"
    };

    public static string MemberKind(string kind) => kind switch
    {
        "Class" => "クラス", "Record" => "レコード", "Struct" => "構造体",
        "RecordStruct" => "レコード構造体", "Interface" => "インターフェース",
        "Enum" => "列挙型", "Delegate" => "デリゲート", "Method" => "メソッド",
        "Constructor" => "コンストラクター", "Property" => "プロパティ",
        "Indexer" => "インデクサー", "Event" => "イベント", "Field" => "フィールド",
        "Operator" => "演算子", _ => kind
    };
}
