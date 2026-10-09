using System.Text;
using ProjectLens.Core.Models;

namespace ProjectLens.Core.Export;

/// <summary>利用者が指定したローカルフォルダーにのみ保存する。対象ソースの配下と既存ファイルは保護する。</summary>
public sealed class ContextExporter
{
    public async Task<IReadOnlyList<string>> ExportAsync(ContextDocument document, string outputDirectory,
        string extension, string analysisRootPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        if (extension is not (".md" or ".txt")) throw new ArgumentException("出力形式は.mdまたは.txtです。", nameof(extension));
        if (document.Parts.Count == 0) throw new ArgumentException("保存するパートがありません。", nameof(document));
        var output = ValidateOutputDirectory(outputDirectory, analysisRootPath);

        // 指定した出力先以外へ一時ファイルを置かない。途中失敗時は今回作成したファイルだけを片付ける。
        Directory.CreateDirectory(output);
        ValidateLocalDirectory(output);
        var safeName = SafeName(document.ProjectName);
        var batch = $"ProjectLens-{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var staged = new List<(string Temporary, string Final)>();
        var committed = new List<string>();
        try
        {
            foreach (var part in document.Parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateLocalDirectory(output);
                var final = Path.Combine(output, $"{batch}-part-{part.Number:D3}-of-{document.Parts.Count:D3}{extension}");
                var temporary = final + ".tmp";
                staged.Add((temporary, final));
                await using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await writer.WriteAsync(part.Text.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }
            foreach (var (temporary, final) in staged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateLocalDirectory(output);
                File.Move(temporary, final, overwrite: false);
                committed.Add(final);
            }
            return committed;
        }
        catch
        {
            foreach (var item in staged) TryDelete(item.Temporary);
            foreach (var file in committed) TryDelete(file);
            throw;
        }
    }

    public static string ValidateOutputDirectory(string outputDirectory, string? analysisRootPath = null)
    {
        var output = ValidateLocalDirectory(outputDirectory);
        if (analysisRootPath is not null && IsWithin(output, ValidateLocalDirectory(analysisRootPath)))
            throw new ArgumentException("解析対象を保護するため、出力先は解析ルートの外に指定してください。", nameof(outputDirectory));
        return output;
    }

    private static string ValidateLocalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("ローカルフォルダーの絶対パスを指定してください。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("ネットワーク・デバイスパスは使用できません。");
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.DriveType == DriveType.Network) throw new ArgumentException("ネットワークドライブは使用できません。");
        // 先に親を確認する。子から確認すると、親リンクの先にあるネットワークへ触れてしまう。
        var driveRoot = Path.GetPathRoot(full)!;
        var current = driveRoot;
        ValidateNotReparse(current);
        foreach (var segment in full[driveRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            ValidateNotReparse(current);
        }
        return full;
    }
    private static void ValidateNotReparse(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("リンク・ジャンクションを含む保存先は使用できません。");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    private static bool IsWithin(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).Take(60).ToArray()).TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(text) ? "Project" : text;
    }
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* 元の保存エラーを優先する。削除できない一時ファイルは指定先内に留まる。 */ }
        catch (UnauthorizedAccessException) { }
    }
}
