using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ProjectLens.Core.Models;

namespace ProjectLens.Core.Analysis;

// 記述された構文を証拠として収集する。対象の実行、通信、設定値の評価はしない。
internal static class WebApiAnalyzer
{
    internal sealed record Findings(IReadOnlyList<WebApiSettingInfo> Settings, IReadOnlyList<WebApiCallInfo> Calls);

    public static Findings AnalyzeCSharp(CompilationUnitSyntax root, string path, CancellationToken cancellationToken)
    {
        var nodes = new List<SyntaxNode>();
        foreach (var node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            nodes.Add(node);
        }
        var settings = new List<WebApiSettingInfo>();
        var calls = new List<WebApiCallInfo>();
        var invocations = nodes.OfType<InvocationExpressionSyntax>().ToArray();
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is AssignmentExpressionSyntax assignment && IsBaseAddress(assignment, nodes))
                settings.Add(new(path, "BaseAddress", Display(assignment.Left), Address(assignment.Right), Line(assignment), "Inferred"));
            else if (node is ElementAccessExpressionSyntax access && IsConfigurationAccess(access, nodes)
                && access.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } key)
            {
                var name = Literal(key) ?? Display(key);
                if (!Sensitive(name)) settings.Add(new(path, "設定参照", name, Display(access.Expression) + "（実行時の値は未解決）", Line(access), "Inferred"));
            }
            else if (node is InvocationExpressionSyntax environment && MethodName(environment) == "GetEnvironmentVariable"
                && environment.Expression is MemberAccessExpressionSyntax { Expression: var target }
                && (Display(target) is "Environment" or "System.Environment" or "global::System.Environment")
                && environment.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } variable)
            {
                var name = Literal(variable) ?? Display(variable);
                if (!Sensitive(name)) settings.Add(new(path, "環境変数", name, "実行時の値は未解決", Line(environment), "Inferred"));
            }
            else if (node is MemberAccessExpressionSyntax property && Display(property.Expression).EndsWith("Settings.Default", StringComparison.Ordinal))
            {
                var name = property.Name.Identifier.ValueText;
                if (!Sensitive(name)) settings.Add(new(path, "Settings参照", name, "実行時の値は未解決", Line(property), "Inferred"));
            }
        }

        foreach (var invocation in invocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryCall(invocation, nodes, out var receiver, out var method, out var address, out var request)) continue;
            var owner = Callable(invocation);
            var bodyNodes = nodes.Where(n => n.SpanStart >= (owner?.SpanStart ?? 0) && n.Span.End <= (owner?.Span.End ?? root.Span.End)
                && ReferenceEquals(Callable(n), owner)).ToArray();
            var processing = Processing(invocation, bodyNodes, cancellationToken, out var responseType).ToList();
            AddHeaders(invocation, receiver, nodes, processing, cancellationToken);
            AddInputEvidence(invocation, bodyNodes, processing);
            var type = invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            var namespaceName = string.Join(".", invocation.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => Display(n.Name)));
            var typeName = type is null ? "(トップレベル)" : string.Join(".",
                type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText));
            if (namespaceName.Length > 0) typeName = namespaceName + "." + typeName;
            calls.Add(new(path, typeName, MemberName(owner), SafeExpression(receiver), method, address, request, responseType,
                processing.Distinct().OrderBy(p => p.Line).ToArray(), Line(invocation), "Inferred"));
        }
        return new(settings.Distinct().OrderBy(s => s.Line).ToArray(), calls.OrderBy(c => c.Line).ToArray());
    }

    public static IReadOnlyList<WebApiSettingInfo> AnalyzeConfiguration(string text, string path,
        ICollection<AnalysisDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var settings = new List<WebApiSettingInfo>();
        var extension = Path.GetExtension(path);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var lineStarts = new List<long> { 0 };
                for (var i = 0; i < bytes.Length; i++)
                {
                    if (i % 4096 == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (bytes[i] == (byte)'\n') lineStarts.Add(i + 1);
                }
                if (!reader.Read()) throw new JsonException("JSONのルート値がありません。");
                ReadJson(ref reader, [], path, lineStarts, settings, cancellationToken);
                // 複数のルート値など、末尾の不正なJSONも診断する。
                if (reader.Read()) throw new JsonException("ルート値の後に追加の内容があります。");
            }
            catch (JsonException ex)
            {
                settings.Clear();
                diagnostics.Add(new(path, (int)(ex.LineNumber ?? 0) + 1, "Warning", "WebAPI設定のJSONを読み取れません（不正な形式または深さの上限超過）。"));
            }
        }
        else if (extension.Equals(".config", StringComparison.OrdinalIgnoreCase) || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".settings", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var options = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = text.Length + 1L };
                using var reader = XmlReader.Create(new StringReader(text), options);
                var xml = XDocument.Load(reader, LoadOptions.SetLineInfo);
                foreach (var element in xml.Descendants())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = (string?)element.Attribute("key") ?? (string?)element.Attribute("name") ?? (string?)element.Attribute("Name") ?? element.Name.LocalName;
                    if (Sensitive(name) || element.Ancestors().Any(a => Sensitive((string?)a.Attribute("name") ?? (string?)a.Attribute("Name") ?? a.Name.LocalName))) continue;
                    if ((string?)element.Attribute("value") is { } value && Candidate(name, value))
                        settings.Add(new(path, "XML", name, SafeUrl(value), XmlLine(element), "Declared"));
                    if ((string?)element.Attribute("address") is { } endpoint && Candidate("endpoint", endpoint))
                        settings.Add(new(path, "XML", name == "endpoint" ? "endpoint:address" : name + ":address", SafeUrl(endpoint), XmlLine(element), "Declared"));
                    var valueElement = element.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("value", StringComparison.OrdinalIgnoreCase));
                    if (valueElement is not null && Candidate(name, valueElement.Value))
                        settings.Add(new(path, "XML", name, SafeUrl(valueElement.Value), XmlLine(element), "Declared"));
                    else if (!element.HasElements && element.Value.Trim().Length > 0 && element.Name.LocalName is not ("value" or "Value") && Candidate(name, element.Value))
                        settings.Add(new(path, "XML", name, SafeUrl(element.Value), XmlLine(element), "Declared"));
                }
            }
            catch (XmlException ex)
            {
                diagnostics.Add(new(path, ex.LineNumber, "Warning", "WebAPI設定のXMLを読み取れません（不正な形式または禁止されたDTD）。"));
            }
        }
        return settings.Distinct().ToArray();
    }

    private static void ReadJson(ref Utf8JsonReader reader, List<string> keys, string path, List<long> lineStarts,
        ICollection<WebApiSettingInfo> settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
                var name = reader.GetString() ?? "";
                if (!reader.Read()) throw new JsonException();
                keys.Add(name);
                ReadJson(ref reader, keys, path, lineStarts, settings, cancellationToken);
                keys.RemoveAt(keys.Count - 1);
            }
        }
        else if (reader.TokenType == JsonTokenType.StartArray)
        {
            var index = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                keys.Add((index++).ToString(System.Globalization.CultureInfo.InvariantCulture));
                ReadJson(ref reader, keys, path, lineStarts, settings, cancellationToken);
                keys.RemoveAt(keys.Count - 1);
            }
        }
        else if (reader.TokenType == JsonTokenType.String && !keys.Any(Sensitive))
        {
            var value = reader.GetString() ?? "";
            var key = string.Join(":", keys);
            if (!Candidate(key, value)) return;
            var index = lineStarts.BinarySearch(reader.TokenStartIndex);
            var line = index >= 0 ? index + 1 : ~index;
            settings.Add(new(path, "JSON", key, SafeUrl(value), line, "Declared"));
        }
    }

    private static bool TryCall(InvocationExpressionSyntax invocation, IReadOnlyList<SyntaxNode> nodes,
        out ExpressionSyntax receiver, out string method, out string address, out string request)
    {
        receiver = invocation;
        method = "未解決";
        address = "実行時の値は未解決";
        request = "なし";
        var name = MethodName(invocation);
        method = name switch
        {
            "GetAsync" or "GetStringAsync" or "GetByteArrayAsync" or "GetStreamAsync" or "GetFromJsonAsync" => "GET",
            "PostAsync" or "PostAsJsonAsync" => "POST",
            "PutAsync" or "PutAsJsonAsync" => "PUT",
            "PatchAsync" or "PatchAsJsonAsync" => "PATCH",
            "DeleteAsync" or "DeleteFromJsonAsync" => "DELETE",
            "SendAsync" => "未解決",
            _ => ""
        };
        if (method.Length == 0) return false;
        if (invocation.Expression is not MemberAccessExpressionSyntax access || !IsClient(access.Expression, invocation, nodes)) return false;
        receiver = access.Expression;
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count == 0) return false;
        if (name == "SendAsync")
        {
            var expression = Argument(arguments, 0, "request");
            if (expression is null)
            {
                request = "要求オブジェクトは未解決";
                return true;
            }
            var creation = ResolveExpression(expression, invocation, nodes) as ObjectCreationExpressionSyntax;
            if (creation is not null && TypeIs(creation.Type, "HttpRequestMessage"))
            {
                var requestArguments = creation.ArgumentList?.Arguments;
                if (requestArguments is { } constructorArguments)
                {
                    if (Argument(constructorArguments, 0, "method") is { } declaredMethod) method = HttpMethod(declaredMethod);
                    if (Argument(constructorArguments, 1, "requestUri") is { } declaredAddress) address = ResolvedAddress(declaredAddress, invocation, nodes);
                }
                var content = creation.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>().FirstOrDefault(a => Display(a.Left) == "Content")?.Right;
                if (content is not null) request = SafeExpression(content);
            }
            var requestName = ReceiverName(expression);
            if (requestName is not null)
                foreach (var assignment in nodes.OfType<AssignmentExpressionSyntax>().Where(a => a.SpanStart < invocation.SpanStart && ReferenceEquals(Callable(a), Callable(invocation))
                    && a.Left is MemberAccessExpressionSyntax member && SameReceiver(member.Expression, a, expression, invocation, nodes)))
                {
                    var member = (MemberAccessExpressionSyntax)assignment.Left;
                    switch (member.Name.Identifier.ValueText)
                    {
                        case "Method": method = HttpMethod(assignment.Right); break;
                        case "RequestUri": address = ResolvedAddress(assignment.Right, invocation, nodes); break;
                        case "Content": request = SafeExpression(assignment.Right); break;
                    }
                }
            if (address == "実行時の値は未解決") address = SafeExpression(expression) + "（RequestUriは未解決）";
        }
        else
        {
            if (Argument(arguments, 0, "requestUri") is { } declaredAddress) address = ResolvedAddress(declaredAddress, invocation, nodes);
            if (method is "POST" or "PUT" or "PATCH")
            {
                var content = Argument(arguments, 1, name.Contains("AsJson", StringComparison.Ordinal) ? "value" : "content");
                request = content is null ? "送信データは未解決" : SafeExpression(content);
            }
        }
        return true;
    }

    private static IReadOnlyList<ApiProcessingInfo> Processing(InvocationExpressionSyntax call, IReadOnlyList<SyntaxNode> nodes,
        CancellationToken cancellationToken, out string responseType)
    {
        var result = new List<ApiProcessingInfo>();
        var name = MethodName(call);
        responseType = call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic }
            && name is "GetFromJsonAsync" or "DeleteFromJsonAsync" ? Display(generic.TypeArgumentList) : name switch
            {
                "GetStringAsync" => "string", "GetByteArrayAsync" => "byte[]", "GetStreamAsync" => "Stream", _ => "HttpResponseMessage（型未解決）"
            };
        if (responseType.StartsWith('<')) responseType = responseType[1..^1];
        if (name.Contains("AsJson", StringComparison.Ordinal)) result.Add(new("送信JSON", "引数をJSONとして送信する記述", Line(call)));
        if (name.Contains("FromJson", StringComparison.Ordinal)) result.Add(new("JSON変換", "応答JSONを " + responseType + " に読み込む記述", Line(call)));

        // 代入された応答変数から、同一のメソッド内で関連する式だけをたどる。
        var tracked = new HashSet<string>(StringComparer.Ordinal);
        var assigned = AssignedName(call);
        if (assigned is not null) tracked.Add(assigned);
        foreach (var node in nodes.OrderBy(n => n.SpanStart))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.SpanStart <= call.SpanStart || node.Ancestors().Any(a => ReferenceEquals(a, call))) continue;
            if (node is VariableDeclaratorSyntax variable && variable.Initializer?.Value is { } initial)
            {
                if (Uses(initial, tracked) && !ContainsOtherApiCall(initial, call, nodes)) tracked.Add(variable.Identifier.ValueText);
                else if (variable.SpanStart > call.SpanStart) tracked.Remove(variable.Identifier.ValueText);
            }
            else if (node is AssignmentExpressionSyntax assignment)
            {
                var target = ReceiverName(assignment.Left);
                if (target is not null)
                {
                    if (Uses(assignment.Right, tracked) && !ContainsOtherApiCall(assignment.Right, call, nodes)) tracked.Add(target);
                    else if (assignment.Left is IdentifierNameSyntax) tracked.Remove(target);
                }
            }
            if (node is InvocationExpressionSyntax invocation && Uses(invocation, tracked))
            {
                var method = MethodName(invocation);
                if (method is "ReadFromJsonAsync" or "Deserialize" or "DeserializeObject" or "DeserializeAsync")
                {
                    var dto = invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax g } ? Display(g.TypeArgumentList)[1..^1] : "型未解決";
                    result.Add(new("JSON変換", method + "で応答を " + dto + " に変換する記述", Line(invocation)));
                    if (dto != "型未解決") responseType = dto;
                }
                else if (method == "EnsureSuccessStatusCode") result.Add(new("ステータス判定", "EnsureSuccessStatusCodeによる成功判定", Line(invocation)));
                else if (method is "ReadAsStringAsync" or "ReadAsByteArrayAsync" or "ReadAsStreamAsync")
                    result.Add(new("応答読込", method + "による応答内容の読み込み", Line(invocation)));
                else if (method is "Select" or "Where" or "ToList" or "ToArray" or "OrderBy" or "GroupBy")
                    result.Add(new("データ変換", SafeExpression(invocation), Line(invocation)));
                else result.Add(new("データ利用", SafeExpression(invocation), Line(invocation)));
            }
            else if (node is IfStatementSyntax conditional && Uses(conditional.Condition, tracked))
                result.Add(new(HasStatus(conditional.Condition) ? "ステータス判定" : "データ分岐", SafeExpression(conditional.Condition), Line(conditional)));
            else if (node is SwitchStatementSyntax @switch && Uses(@switch.Expression, tracked))
                result.Add(new("データ分岐", SafeExpression(@switch.Expression), Line(@switch)));
            else if (node is ReturnStatementSyntax @return && @return.Expression is { } returned && (Uses(returned, tracked) || returned.DescendantNodesAndSelf().Contains(call)))
                result.Add(new("戻り値", SafeExpression(returned), Line(@return)));
        }
        if (call.Ancestors().OfType<ReturnStatementSyntax>().FirstOrDefault() is { } directReturn)
            result.Add(new("戻り値", "API呼び出しの結果を返す記述", Line(directReturn)));
        foreach (var @try in call.Ancestors().OfType<TryStatementSyntax>())
        {
            if (!ReferenceEquals(Callable(@try), Callable(call)) || !@try.Block.Span.Contains(call.Span)) continue;
            foreach (var clause in @try.Catches)
                result.Add(new("例外処理", clause.Declaration is null ? "catch（対応する例外は実行時に未確認）" : Display(clause.Declaration.Type) + "をcatchする記述", Line(clause)));
        }
        return result.Distinct().OrderBy(p => p.Line).ToArray();
    }

    private static bool IsBaseAddress(AssignmentExpressionSyntax assignment, IReadOnlyList<SyntaxNode> nodes) =>
        assignment.Left is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "BaseAddress" && IsClient(member.Expression, assignment, nodes)
        || assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "BaseAddress" }
            && assignment.Ancestors().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault() is { } creation && TypeIs(creation.Type, "HttpClient");

    private static bool IsConfigurationAccess(ElementAccessExpressionSyntax access, IReadOnlyList<SyntaxNode> nodes)
    {
        var expression = Display(access.Expression);
        if (expression.EndsWith("ConfigurationManager.AppSettings", StringComparison.Ordinal)) return true;
        var receiver = ReceiverName(access.Expression);
        if (receiver is null) return false;
        var declaration = Declaration(access.Expression, access, nodes);
        if (declaration is ParameterSyntax parameter && TypeIs(parameter.Type, "IConfiguration")) return true;
        if (declaration is VariableDeclaratorSyntax variable && variable.Parent is VariableDeclarationSyntax variables && TypeIs(variables.Type, "IConfiguration")) return true;
        if (declaration is PropertyDeclarationSyntax property && TypeIs(property.Type, "IConfiguration")) return true;
        return expression is "configuration" or "Configuration" or "config" or "_configuration" or "_config"
            && access.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal && Candidate(Literal(literal) ?? "", "");
    }

    private static bool IsClient(ExpressionSyntax expression, SyntaxNode usage, IReadOnlyList<SyntaxNode> nodes, int depth = 0)
    {
        if (depth > 4) return false;
        if (expression is ObjectCreationExpressionSyntax creation) return TypeIs(creation.Type, "HttpClient");
        if (expression is ParenthesizedExpressionSyntax parenthesized) return IsClient(parenthesized.Expression, usage, nodes, depth + 1);
        if (expression is CastExpressionSyntax cast) return TypeIs(cast.Type, "HttpClient");
        if (expression is InvocationExpressionSyntax invocation && MethodName(invocation) == "CreateClient"
            && invocation.Expression is MemberAccessExpressionSyntax factory)
        {
            var declaration = Declaration(factory.Expression, usage, nodes);
            return declaration is ParameterSyntax parameter && TypeIs(parameter.Type, "IHttpClientFactory")
                || declaration is VariableDeclaratorSyntax variable && variable.Parent is VariableDeclarationSyntax variables && TypeIs(variables.Type, "IHttpClientFactory")
                || declaration is PropertyDeclarationSyntax property && TypeIs(property.Type, "IHttpClientFactory");
        }
        var name = ReceiverName(expression);
        if (name is null) return false;
        var candidate = Declaration(expression, usage, nodes);
        if (candidate is ParameterSyntax p)
        {
            if (TypeIs(p.Type, "HttpClient")) return true;
            // AddHttpClient(..., client => client.BaseAddress = ...) の構成用引数。
            var lambda = p.Ancestors().OfType<LambdaExpressionSyntax>().FirstOrDefault();
            if (lambda?.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault() is { } configure && MethodName(configure) == "AddHttpClient") return true;
        }
        if (candidate is PropertyDeclarationSyntax prop) return TypeIs(prop.Type, "HttpClient");
        if (candidate is VariableDeclaratorSyntax v && v.Parent is VariableDeclarationSyntax declarationType)
        {
            if (TypeIs(declarationType.Type, "HttpClient")) return true;
            if (v.Initializer?.Value is { } value) return IsClient(value, v, nodes, depth + 1);
        }
        return false;
    }

    private static SyntaxNode? Declaration(ExpressionSyntax expression, SyntaxNode usage, IReadOnlyList<SyntaxNode> nodes)
    {
        // this.x はローカル変数や引数で隠されない。base.x の基底型宣言はここでは未解決。
        if (expression is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax }) return null;
        var name = ReceiverName(expression);
        if (name is null) return null;
        return Declaration(name, usage, nodes, expression is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax });
    }

    private static SyntaxNode? Declaration(string name, SyntaxNode usage, IReadOnlyList<SyntaxNode> nodes, bool membersOnly)
    {
        var callable = Callable(usage);
        var type = usage.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var candidates = nodes.Where(n => n switch
        {
            VariableDeclaratorSyntax v => v.Identifier.ValueText == name && (v.Parent?.Parent is FieldDeclarationSyntax
                ? ReferenceEquals(v.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault(), type)
                : !membersOnly && v.SpanStart < usage.SpanStart && ReferenceEquals(Callable(v), callable) && InScope(v, usage)),
            ParameterSyntax p => !membersOnly && p.Identifier.ValueText == name && usage.Ancestors().Contains(p.Parent?.Parent),
            PropertyDeclarationSyntax p => p.Identifier.ValueText == name && ReferenceEquals(p.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault(), type),
            _ => false
        }).ToArray();
        return candidates.OrderByDescending(n => n is ParameterSyntax || n is VariableDeclaratorSyntax { Parent.Parent: not FieldDeclarationSyntax })
            .ThenByDescending(n => n.SpanStart).FirstOrDefault();
    }

    private static bool InScope(SyntaxNode declaration, SyntaxNode usage)
    {
        var block = declaration.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
        return block is null || usage.Ancestors().Contains(block);
    }

    private static ExpressionSyntax ResolveExpression(ExpressionSyntax expression, SyntaxNode usage, IReadOnlyList<SyntaxNode> nodes)
    {
        return Declaration(expression, usage, nodes) is VariableDeclaratorSyntax variable && variable.Initializer?.Value is { } initial ? initial : expression;
    }

    private static bool SameReceiver(ExpressionSyntax first, SyntaxNode firstUsage, ExpressionSyntax second, SyntaxNode secondUsage, IReadOnlyList<SyntaxNode> nodes)
    {
        var firstDeclaration = Declaration(first, firstUsage, nodes);
        var secondDeclaration = Declaration(second, secondUsage, nodes);
        return firstDeclaration is not null && secondDeclaration is not null ? ReferenceEquals(firstDeclaration, secondDeclaration)
            : firstDeclaration is null && secondDeclaration is null && Display(first) == Display(second);
    }

    private static string? AssignedName(SyntaxNode expression)
    {
        var parent = expression.Parent;
        while (parent is AwaitExpressionSyntax or ParenthesizedExpressionSyntax || parent is MemberAccessExpressionSyntax or InvocationExpressionSyntax)
            parent = parent.Parent;
        return parent switch
        {
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable } => variable.Identifier.ValueText,
            AssignmentExpressionSyntax assignment when assignment.Left is IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null
        };
    }

    private static bool Uses(SyntaxNode expression, IReadOnlySet<string> names) => expression.DescendantNodesAndSelf()
        .OfType<IdentifierNameSyntax>().Any(i => names.Contains(i.Identifier.ValueText)
            && (i.Parent is not MemberAccessExpressionSyntax member || !ReferenceEquals(member.Name, i)));

    private static bool ContainsOtherApiCall(SyntaxNode expression, InvocationExpressionSyntax call, IReadOnlyList<SyntaxNode> nodes) =>
        expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(i => !ReferenceEquals(i, call)
            && TryCall(i, nodes, out _, out _, out _, out _));

    private static string ResolvedAddress(ExpressionSyntax expression, SyntaxNode usage, IReadOnlyList<SyntaxNode> nodes)
    {
        var initial = ResolveExpression(expression, usage, nodes);
        return ReferenceEquals(initial, expression) ? Address(expression)
            : SafeExpression(expression) + " ← " + Address(initial) + "（初期値の候補・変更は未追跡）";
    }

    private static void AddInputEvidence(InvocationExpressionSyntax call, IReadOnlyList<SyntaxNode> nodes, ICollection<ApiProcessingInfo> result)
    {
        var inputs = call.ArgumentList.Arguments.SelectMany(a => a.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            .Select(i => i.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        // パス・送信オブジェクトを作った式は候補として表示し、本文は省略する。
        foreach (var variable in nodes.OfType<VariableDeclaratorSyntax>().Where(v => v.SpanStart < call.SpanStart && inputs.Contains(v.Identifier.ValueText)))
        {
            if (variable.Initializer?.Value is not { } initial) continue;
            result.Add(new("送信前処理", variable.Identifier.ValueText + " の初期値: " + SafeExpression(initial), Line(variable)));
            foreach (var id in initial.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()) inputs.Add(id.Identifier.ValueText);
        }
        foreach (var guard in nodes.OfType<InvocationExpressionSyntax>().Where(i => i.SpanStart < call.SpanStart
            && MethodName(i).StartsWith("ThrowIf", StringComparison.Ordinal) && Uses(i, inputs)))
            result.Add(new("入力確認", SafeExpression(guard), Line(guard)));
    }

    private static void AddHeaders(InvocationExpressionSyntax call, ExpressionSyntax receiver, IReadOnlyList<SyntaxNode> nodes,
        ICollection<ApiProcessingInfo> result, CancellationToken cancellationToken)
    {
        var request = MethodName(call) == "SendAsync" ? Argument(call.ArgumentList.Arguments, 0, "request") : null;
        var type = call.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault(), type)) continue;
            if (ReferenceEquals(Callable(node), Callable(call)) && node.SpanStart >= call.SpanStart) continue;
            ExpressionSyntax? target = node switch
            {
                AssignmentExpressionSyntax assignment => assignment.Left,
                InvocationExpressionSyntax invocation when MethodName(invocation) is "Add" or "TryAddWithoutValidation" => invocation.Expression,
                _ => null
            };
            if (target is null) continue;
            var members = target.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().ToArray();
            var headers = members.FirstOrDefault(m => m.Name.Identifier.ValueText == "DefaultRequestHeaders"
                && SameReceiver(m.Expression, node, receiver, call, nodes) && IsClient(m.Expression, node, nodes)
                || m.Name.Identifier.ValueText == "Headers" && request is not null && SameReceiver(m.Expression, node, request, call, nodes)
                    && ReferenceEquals(Callable(node), Callable(call)));
            if (headers is null) continue;
            var header = target is MemberAccessExpressionSyntax outer && outer.Name.Identifier.ValueText is not ("Add" or "TryAddWithoutValidation")
                ? outer.Name.Identifier.ValueText : node is InvocationExpressionSyntax add && add.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } key
                    ? Limit(Literal(key) ?? "名前未解決") : "名前未解決";
            result.Add(new("認証・ヘッダー設定", header + " ヘッダー設定の候補（値は非表示・適用は未確認）", Line(node)));
        }
    }

    private static bool HasStatus(SyntaxNode expression) => expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
        .Any(i => i.Identifier.ValueText is "StatusCode" or "IsSuccessStatusCode");
    private static ExpressionSyntax? Argument(SeparatedSyntaxList<ArgumentSyntax> arguments, int position, params string[] names)
    {
        foreach (var argument in arguments)
            if (argument.NameColon is { } named && names.Contains(named.Name.Identifier.ValueText, StringComparer.Ordinal)) return argument.Expression;
        // 順序を変えた名前付き引数を別パラメーターの値として扱わない。
        return position < arguments.Count && arguments[position].NameColon is null ? arguments[position].Expression : null;
    }
    private static string HttpMethod(ExpressionSyntax expression) => Literal(expression)?.ToUpperInvariant()
        ?? (expression is MemberAccessExpressionSyntax member && Display(member.Expression).EndsWith("HttpMethod", StringComparison.Ordinal)
            ? member.Name.Identifier.ValueText.ToUpperInvariant() : SafeExpression(expression) + "（未解決）");
    private static string Address(ExpressionSyntax expression) => Literal(expression) is { } literal ? SafeUrl(literal) : SafeExpression(expression, preserveAddress: true) + "（実行時の値は未解決）";
    private static string? Literal(ExpressionSyntax expression) => expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal.Token.ValueText : null;
    private static string? ReceiverName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax member when member.Expression is ThisExpressionSyntax => member.Name.Identifier.ValueText,
        _ => null
    };
    private static bool TypeIs(TypeSyntax? type, string name) => type is not null && (Display(type) == name || Display(type).EndsWith("." + name, StringComparison.Ordinal));
    private static string MethodName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => ""
    };
    private static SyntaxNode? Callable(SyntaxNode node) => node.Ancestors().FirstOrDefault(n => n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
    private static string MemberName(SyntaxNode? owner) => owner switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
        LocalFunctionStatementSyntax local => local.Identifier.ValueText + "（ローカル関数）",
        AnonymousFunctionExpressionSyntax => "(ラムダ・匿名関数)",
        AccessorDeclarationSyntax accessor => accessor.Keyword.ValueText,
        _ => "(トップレベル・初期化子)"
    };
    private static bool Candidate(string key, string value)
    {
        var lower = key.ToLowerInvariant();
        return value.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("baseurl", StringComparison.Ordinal) || lower.Contains("baseuri", StringComparison.Ordinal)
            || lower.Contains("apiurl", StringComparison.Ordinal) || lower.Contains("apiuri", StringComparison.Ordinal)
            || lower.Contains("endpoint", StringComparison.Ordinal) || lower.EndsWith(":url", StringComparison.Ordinal)
            || lower.EndsWith(":uri", StringComparison.Ordinal);
    }
    private static bool Sensitive(string key)
    {
        var compact = new string(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return compact.Contains("password", StringComparison.Ordinal) || compact.Contains("passwd", StringComparison.Ordinal)
            || compact.Contains("secret", StringComparison.Ordinal) || compact.Contains("token", StringComparison.Ordinal)
            || compact.Contains("authorization", StringComparison.Ordinal) || compact.Contains("credential", StringComparison.Ordinal)
            || compact.Contains("apikey", StringComparison.Ordinal) || compact.Contains("accesskey", StringComparison.Ordinal)
            || compact.Contains("connectionstring", StringComparison.Ordinal) || compact is "key" or "pwd";
    }
    private static bool SensitiveQueryKey(string key)
    {
        try { key = Uri.UnescapeDataString(key); } catch (UriFormatException) { }
        return Sensitive(key);
    }

    private static string SafeUrl(string value)
    {
        var result = value.Trim();
        var scheme = result.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var authorityEnd = result.IndexOfAny(['/', '?', '#'], scheme + 3);
            if (authorityEnd < 0) authorityEnd = result.Length;
            var at = result.LastIndexOf('@', authorityEnd - 1, authorityEnd - scheme - 3);
            if (at >= scheme + 3) result = result[..(scheme + 3)] + "[認証情報非表示]@" + result[(at + 1)..];
        }
        var question = result.IndexOf('?');
        if (question >= 0)
        {
            var fragment = result.IndexOf('#', question);
            var queryEnd = fragment < 0 ? result.Length : fragment;
            var parts = result[(question + 1)..queryEnd].Split('&');
            for (var i = 0; i < parts.Length; i++)
            {
                var equal = parts[i].IndexOf('=');
                if (equal < 0) continue;
                var key = parts[i][..equal];
                if (SensitiveQueryKey(key)) parts[i] = parts[i][..(equal + 1)] + "[非表示]";
            }
            result = result[..(question + 1)] + string.Join("&", parts) + result[queryEnd..];
        }
        return Limit(result);
    }

    private static string SafeExpression(SyntaxNode node, bool preserveAddress = false)
    {
        // 連結・補間をまたぐ機密クエリを検出した場合、文字列部分は保守的に省略する。
        var texts = node.DescendantNodesAndSelf().Where(n => n is LiteralExpressionSyntax or InterpolatedStringTextSyntax)
            .OrderBy(n => n.SpanStart).Select(n => n is LiteralExpressionSyntax l ? Literal(l) ?? "" : ((InterpolatedStringTextSyntax)n).TextToken.ValueText);
        var joined = string.Concat(texts);
        var sensitiveQuery = joined.IndexOf('?') is var question && question >= 0
            && joined[(question + 1)..].Split('&').Any(p => SensitiveQueryKey(p.Split('=')[0]));
        var sanitized = new ExpressionRedactor(preserveAddress, sensitiveQuery).Visit(node)!;
        return Limit(Display(sanitized));
    }

    private sealed class ExpressionRedactor(bool preserveAddress, bool sensitiveQuery) : CSharpSyntaxRewriter
    {
        private string Text(string value)
        {
            if (!preserveAddress) return "[文字列非表示]";
            if (!sensitiveQuery) return SafeUrl(value);
            var question = value.IndexOf('?');
            return question >= 0 ? SafeUrl(value[..question]) + "?[クエリ値非表示]" : "[非表示]";
        }
        public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node) => node.IsKind(SyntaxKind.StringLiteralExpression)
            ? SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(Text(node.Token.ValueText))) : base.VisitLiteralExpression(node);
        public override SyntaxNode? VisitInterpolatedStringText(InterpolatedStringTextSyntax node) => SyntaxFactory.InterpolatedStringText(
            SyntaxFactory.Token(default, SyntaxKind.InterpolatedStringTextToken, Text(node.TextToken.ValueText), Text(node.TextToken.ValueText), default));
        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => SyntaxFactory.ParseExpression("value => default /* 関数本文省略 */");
        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => SyntaxFactory.ParseExpression("() => default /* 関数本文省略 */");
        public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => SyntaxFactory.ParseExpression("() => default /* 関数本文省略 */");
        public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node) => Sensitive(Display(node.Left))
            ? node.WithRight(SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)) : base.VisitAssignmentExpression(node);
        public override SyntaxNode? VisitAnonymousObjectMemberDeclarator(AnonymousObjectMemberDeclaratorSyntax node) => node.NameEquals is not null && Sensitive(Display(node.NameEquals.Name))
            ? node.WithExpression(SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)) : base.VisitAnonymousObjectMemberDeclarator(node);
    }
    private static string Limit(string value) => value.Length > 240 ? value[..240] + " …" : value;
    private static string Display(SyntaxNode node) => node.WithoutTrivia().NormalizeWhitespace().ToFullString();
    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    private static int XmlLine(XElement element) => element is IXmlLineInfo line && line.HasLineInfo() ? line.LineNumber : 0;
}
