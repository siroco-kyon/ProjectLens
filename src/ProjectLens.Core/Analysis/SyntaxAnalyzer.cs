using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ProjectLens.Core.Models;
using TypeInfo = ProjectLens.Core.Models.TypeInfo;

namespace ProjectLens.Core.Analysis;

internal sealed record TypeDeclarationInfo(string Key, string SimpleKey, TypeInfo Type);
internal sealed record TypeReferenceInfo(string Key, string Kind, string Evidence);
internal sealed record ParsedSource(SourceFileInfo File, IReadOnlyList<TypeDeclarationInfo> Declarations,
    IReadOnlyList<TypeReferenceInfo> References, IReadOnlyList<string> ImportedNamespaces,
    IReadOnlyDictionary<string, string> Aliases, string? XamlClass = null);

internal static class SyntaxAnalyzer
{
    public static ParsedSource Parse(string text, string fullPath, string relativePath, string projectName, CancellationToken cancellationToken)
    {
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.CSharp14), fullPath, cancellationToken: cancellationToken);
        var root = tree.GetCompilationUnitRoot(cancellationToken);
        var diagnostics = tree.GetDiagnostics(cancellationToken).Select(d => new AnalysisDiagnostic(relativePath,
            d.Location.IsInSource ? d.Location.GetLineSpan().StartLinePosition.Line + 1 : 0,
            d.Severity.ToString(), $"{d.Id}: {d.GetMessage(System.Globalization.CultureInfo.GetCultureInfo("ja-JP"))}")).ToArray();
        var declarations = new List<TypeDeclarationInfo>();
        var references = new HashSet<TypeReferenceInfo>();
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        var usings = new HashSet<string>(StringComparer.Ordinal);
        var imports = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);

        // 一度の走査で構造と参照候補を収集する。実行・ビルド・外部参照の解決は行わない。
        foreach (var node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case BaseNamespaceDeclarationSyntax namespaceDeclaration:
                    namespaces.Add(NamespaceOf(namespaceDeclaration, includeSelf: true));
                    break;
                case UsingDirectiveSyntax usingDirective:
                    usings.Add(Display(usingDirective));
                    if (usingDirective.Name is not null)
                    {
                        if (usingDirective.Alias is not null)
                            aliases.TryAdd(usingDirective.Alias.Name.Identifier.ValueText, TypeKey(usingDirective.Name));
                        else if (usingDirective.StaticKeyword.IsKind(SyntaxKind.None))
                            imports.Add(TypeKey(usingDirective.Name));
                        else AddReference(usingDirective.Name, "TypeUse", "using static", references);
                    }
                    break;
                case BaseTypeDeclarationSyntax typeDeclaration:
                    declarations.Add(DescribeType(typeDeclaration, relativePath));
                    if (typeDeclaration.BaseList is not null)
                        foreach (var baseType in typeDeclaration.BaseList.Types)
                            AddReference(baseType.Type, "Inheritance", "基底型・実装インターフェースの構文", references);
                    break;
                case DelegateDeclarationSyntax delegateDeclaration:
                    declarations.Add(DescribeDelegate(delegateDeclaration, relativePath));
                    AddReference(delegateDeclaration.ReturnType, "TypeUse", "デリゲートの戻り値", references);
                    break;
                case MethodDeclarationSyntax method:
                    AddReference(method.ReturnType, "TypeUse", "メソッドの戻り値", references);
                    break;
                case ParameterSyntax parameter when parameter.Type is not null:
                    AddReference(parameter.Type, "TypeUse", "引数の型", references);
                    break;
                case VariableDeclarationSyntax variable:
                    AddReference(variable.Type, "TypeUse", "変数・フィールドの型", references);
                    break;
                case PropertyDeclarationSyntax property:
                    AddReference(property.Type, "TypeUse", "プロパティの型", references);
                    break;
                case IndexerDeclarationSyntax indexer:
                    AddReference(indexer.Type, "TypeUse", "インデクサーの型", references);
                    break;
                case EventDeclarationSyntax eventDeclaration:
                    AddReference(eventDeclaration.Type, "TypeUse", "イベントの型", references);
                    break;
                case ObjectCreationExpressionSyntax creation:
                    AddReference(creation.Type, "TypeUse", "new 式の型", references);
                    break;
                case CastExpressionSyntax cast:
                    AddReference(cast.Type, "TypeUse", "キャストの型", references);
                    break;
                case TypeOfExpressionSyntax typeOf:
                    AddReference(typeOf.Type, "TypeUse", "typeof の型", references);
                    break;
                case SizeOfExpressionSyntax sizeOf:
                    AddReference(sizeOf.Type, "TypeUse", "sizeof の型", references);
                    break;
                case DefaultExpressionSyntax defaultExpression:
                    AddReference(defaultExpression.Type, "TypeUse", "default の型", references);
                    break;
                case DeclarationPatternSyntax pattern:
                    AddReference(pattern.Type, "TypeUse", "型パターン", references);
                    break;
                case RecursivePatternSyntax pattern when pattern.Type is not null:
                    AddReference(pattern.Type, "TypeUse", "型パターン", references);
                    break;
                case TypePatternSyntax pattern:
                    AddReference(pattern.Type, "TypeUse", "型パターン", references);
                    break;
                case CatchDeclarationSyntax catchDeclaration:
                    AddReference(catchDeclaration.Type, "TypeUse", "catch の型", references);
                    break;
                case ForEachStatementSyntax forEach:
                    AddReference(forEach.Type, "TypeUse", "foreach の型", references);
                    break;
                case TupleElementSyntax tupleElement:
                    AddReference(tupleElement.Type, "TypeUse", "タプル要素の型", references);
                    break;
                case TypeConstraintSyntax constraint:
                    AddReference(constraint.Type, "TypeUse", "型引数の制約", references);
                    break;
            }
        }

        if (declarations.Any(d => d.Type.Namespace.Length == 0)) namespaces.Add("(global)");
        var file = new SourceFileInfo(relativePath, fullPath, projectName, "CSharp", RoleHint(relativePath, declarations.Select(d => d.Type)),
            namespaces.Order(StringComparer.Ordinal).ToArray(), usings.Order(StringComparer.Ordinal).ToArray(),
            declarations.Select(d => d.Type).ToArray(), diagnostics, text);
        return new ParsedSource(file, declarations, references.OrderBy(r => r.Key, StringComparer.Ordinal).ThenBy(r => r.Kind).ToArray(),
            imports.ToArray(), aliases);
    }

    public static string RoleHint(string relativePath, IEnumerable<TypeInfo>? types = null)
    {
        var name = Path.GetFileName(relativePath);
        if (name.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)) return "推定: WPF画面のコードビハインド";
        if (name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) return "推定: WPF画面・リソース定義";
        if (name.Contains("ViewModel", StringComparison.OrdinalIgnoreCase)) return "推定: 画面の状態・操作を扱うViewModel";
        if (name.Contains("Controller", StringComparison.OrdinalIgnoreCase)) return "推定: Webリクエストを扱うController";
        if (name.Contains("Service", StringComparison.OrdinalIgnoreCase)) return "推定: 業務処理・サービス";
        if (name.Contains("Repository", StringComparison.OrdinalIgnoreCase)) return "推定: データへのアクセス";
        if (name is "App.cs" or "Program.cs" or "Startup.cs") return "推定: アプリケーションの起動・構成";
        if (types?.Any(t => t.Kind == "Interface") == true) return "推定: インターフェース・型定義";
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".cs" => "役割未整理: C#ソース",
            ".razor" or ".cshtml" => "推定: Web画面・テンプレート",
            ".resx" => "推定: リソース定義",
            ".json" or ".config" or ".xml" => "推定: 設定・データ定義",
            _ => "役割未整理"
        };
    }

    private static TypeDeclarationInfo DescribeType(BaseTypeDeclarationSyntax syntax, string relativePath)
    {
        var name = syntax.Identifier.ValueText;
        var kind = syntax switch
        {
            ClassDeclarationSyntax => "Class",
            InterfaceDeclarationSyntax => "Interface",
            StructDeclarationSyntax => "Struct",
            RecordDeclarationSyntax record => record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "RecordStruct" : "Record",
            EnumDeclarationSyntax => "Enum",
            _ => "Type"
        };
        var members = syntax is TypeDeclarationSyntax typeDeclaration
            ? typeDeclaration.Members.SelectMany(DescribeMember).ToList() : [];
        ParameterListSyntax? primaryConstructor = syntax switch
        {
            RecordDeclarationSyntax record => record.ParameterList,
            ClassDeclarationSyntax @class => @class.ParameterList,
            StructDeclarationSyntax @struct => @struct.ParameterList,
            _ => null
        };
        if (primaryConstructor is not null)
            members.Insert(0, new MemberInfo(name, "Constructor", "", Parameters(primaryConstructor.Parameters), "primary", Line(syntax)));
        var typeInfo = new TypeInfo(name, FullName(syntax, includeTypeParameters: true), NamespaceOf(syntax), kind, Modifiers(syntax.Modifiers),
            syntax.BaseList?.Types.Select(t => Display(t.Type)).ToArray() ?? [], members, relativePath, Line(syntax));
        return new TypeDeclarationInfo(FullName(syntax, includeTypeParameters: false), DeclarationKey(syntax), typeInfo);
    }

    private static TypeDeclarationInfo DescribeDelegate(DelegateDeclarationSyntax syntax, string relativePath)
    {
        var typeInfo = new TypeInfo(syntax.Identifier.ValueText, FullName(syntax, includeTypeParameters: true), NamespaceOf(syntax),
            "Delegate", Modifiers(syntax.Modifiers), [],
            [new MemberInfo("Invoke", "Method", Display(syntax.ReturnType), Parameters(syntax.ParameterList.Parameters), "", Line(syntax))],
            relativePath, Line(syntax));
        return new TypeDeclarationInfo(FullName(syntax, includeTypeParameters: false), DeclarationKey(syntax), typeInfo);
    }

    private static IEnumerable<MemberInfo> DescribeMember(MemberDeclarationSyntax member)
    {
        switch (member)
        {
            case MethodDeclarationSyntax method:
                yield return new MemberInfo($"{(method.ExplicitInterfaceSpecifier is null ? "" : Display(method.ExplicitInterfaceSpecifier))}{method.Identifier.ValueText}{method.TypeParameterList}",
                    "Method", Display(method.ReturnType), Parameters(method.ParameterList.Parameters), Modifiers(method.Modifiers), Line(method));
                break;
            case ConstructorDeclarationSyntax constructor:
                yield return new MemberInfo(constructor.Identifier.ValueText, "Constructor", "", Parameters(constructor.ParameterList.Parameters), Modifiers(constructor.Modifiers), Line(constructor));
                break;
            case PropertyDeclarationSyntax property:
                yield return new MemberInfo($"{(property.ExplicitInterfaceSpecifier is null ? "" : Display(property.ExplicitInterfaceSpecifier))}{property.Identifier.ValueText}",
                    "Property", Display(property.Type), [], Modifiers(property.Modifiers), Line(property));
                break;
            case IndexerDeclarationSyntax indexer:
                yield return new MemberInfo("this[" + string.Join(", ", Parameters(indexer.ParameterList.Parameters).Select(p => p.Display)) + "]", "Property", Display(indexer.Type),
                    Parameters(indexer.ParameterList.Parameters), Modifiers(indexer.Modifiers), Line(indexer));
                break;
            case OperatorDeclarationSyntax op:
                yield return new MemberInfo("operator " + op.OperatorToken.Text, "Method", Display(op.ReturnType), Parameters(op.ParameterList.Parameters), Modifiers(op.Modifiers), Line(op));
                break;
            case ConversionOperatorDeclarationSyntax conversion:
                yield return new MemberInfo(conversion.ImplicitOrExplicitKeyword.Text + " operator", "Method", Display(conversion.Type), Parameters(conversion.ParameterList.Parameters), Modifiers(conversion.Modifiers), Line(conversion));
                break;
            case EventDeclarationSyntax @event:
                yield return new MemberInfo(@event.Identifier.ValueText, "Event", Display(@event.Type), [], Modifiers(@event.Modifiers), Line(@event));
                break;
        }
    }

    private static IReadOnlyList<ParameterInfo> Parameters(SeparatedSyntaxList<ParameterSyntax> parameters) => parameters
        .Select(p => new ParameterInfo(p.Identifier.ValueText, p.Type is null ? "" : Display(p.Type), Modifiers(p.Modifiers), p.Default is null ? null : Display(p.Default.Value))).ToArray();

    private static void AddReference(TypeSyntax type, string kind, string evidence, ISet<TypeReferenceInfo> references)
    {
        // 名前だけでは意味的に型を確定できないため、ここでは候補にとどめる。
        var key = TypeKey(type);
        if (key.Length > 0) references.Add(new TypeReferenceInfo(key, kind, $"{evidence}: {Display(type)} (行 {Line(type)})"));
        foreach (var generic in type.DescendantNodesAndSelf().OfType<GenericNameSyntax>())
            foreach (var argument in generic.TypeArgumentList.Arguments)
            {
                var argumentKey = TypeKey(argument);
                if (argumentKey.Length > 0) references.Add(new TypeReferenceInfo(argumentKey, "TypeUse", $"型引数: {Display(argument)} (行 {Line(argument)})"));
            }
        if (type is TupleTypeSyntax tuple)
            foreach (var element in tuple.Elements) AddReference(element.Type, "TypeUse", "タプル要素", references);
    }

    internal static string TypeKey(TypeSyntax type) => type switch
    {
        GenericNameSyntax generic => generic.Identifier.ValueText + "`" + generic.TypeArgumentList.Arguments.Count,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax qualified => TypeKey(qualified.Left) + "." + TypeKey(qualified.Right),
        AliasQualifiedNameSyntax alias when alias.Alias.Identifier.ValueText == "global" => TypeKey(alias.Name),
        AliasQualifiedNameSyntax alias => alias.Alias.Identifier.ValueText + "::" + TypeKey(alias.Name),
        NullableTypeSyntax nullable => TypeKey(nullable.ElementType),
        ArrayTypeSyntax array => TypeKey(array.ElementType),
        PointerTypeSyntax pointer => TypeKey(pointer.ElementType),
        _ => ""
    };

    private static string FullName(SyntaxNode syntax, bool includeTypeParameters)
    {
        var parentNames = syntax.Ancestors().OfType<TypeDeclarationSyntax>().Reverse()
            .Select(t => includeTypeParameters ? t.Identifier.ValueText + t.TypeParameterList : DeclarationKey(t));
        var ownName = includeTypeParameters ? syntax switch
        {
            TypeDeclarationSyntax type => type.Identifier.ValueText + type.TypeParameterList,
            BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
            DelegateDeclarationSyntax type => type.Identifier.ValueText + type.TypeParameterList,
            _ => ""
        } : DeclarationKey(syntax);
        var namespaceName = NamespaceOf(syntax);
        return string.Join(".", (namespaceName.Length == 0 ? Array.Empty<string>() : [namespaceName]).Concat(parentNames).Append(ownName));
    }

    private static string DeclarationKey(SyntaxNode syntax) => syntax switch
    {
        TypeDeclarationSyntax type => type.Identifier.ValueText + (type.TypeParameterList is { Parameters.Count: > 0 } parameters ? "`" + parameters.Parameters.Count : ""),
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        DelegateDeclarationSyntax type => type.Identifier.ValueText + (type.TypeParameterList is { Parameters.Count: > 0 } parameters ? "`" + parameters.Parameters.Count : ""),
        _ => ""
    };

    private static string NamespaceOf(SyntaxNode syntax, bool includeSelf = false) => string.Join(".",
        (includeSelf ? syntax.AncestorsAndSelf() : syntax.Ancestors()).OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => Display(n.Name)));

    private static string Modifiers(SyntaxTokenList tokens) => string.Join(" ", tokens.Select(t => t.ValueText));
    private static string Display(SyntaxNode syntax) => syntax.WithoutTrivia().NormalizeWhitespace().ToFullString();
    private static int Line(SyntaxNode syntax) => syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
