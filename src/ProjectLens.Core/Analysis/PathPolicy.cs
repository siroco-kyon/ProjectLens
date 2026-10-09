using ProjectLens.Core.Models;

namespace ProjectLens.Core.Analysis;

// 解析範囲の判定を一か所に集め、シンボリックリンク経由の意図しない読み取りを防ぐ。
internal sealed class PathPolicy
{
    private readonly HashSet<string> _excludedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _excludedPaths = [];
    private readonly List<string> _includedPaths = [];

    public string Root { get; }

    public PathPolicy(string root, AnalysisOptions options)
    {
        Root = Path.TrimEndingDirectorySeparator(root);
        foreach (var exclusion in options.ExcludedDirectories)
        {
            var value = exclusion.Trim().Replace('\\', '/').TrimEnd('/');
            if (value.Length == 0) continue;
            if (Path.IsPathRooted(value)) throw new ArgumentException("除外範囲には解析ルートからの相対パスを指定してください。");
            if (!value.Contains('/')) _excludedNames.Add(value);
            else _excludedPaths.Add(ResolveRelative(value));
        }

        foreach (var inclusion in options.IncludedRelativePaths)
        {
            if (string.IsNullOrWhiteSpace(inclusion)) continue;
            _includedPaths.Add(ResolveRelative(inclusion));
        }
    }

    public static string GetLocalInputPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || !Path.IsPathFullyQualified(input))
            throw new ArgumentException("解析対象にはローカルの絶対パスを指定してください。", nameof(input));
        var fullPath = Path.GetFullPath(input);
        EnsureLocal(fullPath);
        if (HasReparseAncestor(fullPath))
            throw new ArgumentException("解析対象のパスにリンクまたは再解析ポイントが含まれています。通常のローカルフォルダーを指定してください。", nameof(input));
        return fullPath;
    }

    public string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    public bool Contains(string path) => IsWithin(path, Root);

    public void ValidateForRead(string path)
    {
        if (!Contains(path)) throw new ArgumentException("読み取り対象は解析ルート内に限定してください。");
        EnsureLocal(path);
        if (HasReparseAncestor(path)) throw new ArgumentException("読み取り対象にリンクまたは再解析ポイントが含まれています。");
    }

    public bool ShouldRead(string path) => !IsExcluded(path)
        && (_includedPaths.Count == 0 || _includedPaths.Any(included => IsWithin(path, included)));

    public bool ShouldTraverse(string path) => !IsExcluded(path)
        && (_includedPaths.Count == 0 || _includedPaths.Any(included => IsWithin(path, included) || IsWithin(included, path)));

    public string ResolveDeclaration(string declaringDirectory, string relativePath)
    {
        // GetFullPath は文字列の解決だけを行う。範囲外や UNC の先を確認するためにアクセスしない。
        var normalized = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(normalized, declaringDirectory);
        if (!Contains(fullPath)) throw new ArgumentException("解析ルート外への参照です。");
        EnsureLocal(fullPath);
        if (HasReparseAncestor(fullPath)) throw new ArgumentException("参照先にリンクまたは再解析ポイントが含まれています。");
        return fullPath;
    }

    public static bool IsWithin(string candidate, string parent)
    {
        candidate = Path.TrimEndingDirectorySeparator(candidate);
        parent = Path.TrimEndingDirectorySeparator(parent);
        var boundary = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return candidate.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(boundary, StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveRelative(string value)
    {
        if (Path.IsPathRooted(value) || value.StartsWith('\\'))
            throw new ArgumentException("対象範囲・除外範囲には解析ルートからの相対パスを指定してください。");
        var path = Path.GetFullPath(value.Replace('/', Path.DirectorySeparatorChar), Root);
        if (!Contains(path)) throw new ArgumentException("対象範囲・除外範囲は解析ルート内で指定してください。");
        return path;
    }

    private bool IsExcluded(string path)
    {
        if (!Contains(path)) return true;
        var relative = Relative(path);
        if (relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(_excludedNames.Contains)) return true;
        return _excludedPaths.Any(excluded => IsWithin(path, excluded));
    }

    private static void EnsureLocal(string fullPath)
    {
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("UNC・ネットワークパスは解析できません。ローカルディスクを指定してください。");
        if (OperatingSystem.IsWindows())
        {
            var drive = new DriveInfo(Path.GetPathRoot(fullPath)!);
            if (drive.DriveType == DriveType.Network)
                throw new ArgumentException("ネットワークドライブは解析できません。ローカルディスクを指定してください。");
        }
    }

    private static bool HasReparseAncestor(string path)
    {
        var ancestors = new Stack<string>();
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            ancestors.Push(current);
        // 親を先に検査する。末端から調べると、親のリンクを発見する前にネットワーク上のリンク先へアクセスし得る。
        while (ancestors.TryPop(out var current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        return false;
    }
}
