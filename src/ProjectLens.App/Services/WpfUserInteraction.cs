using Microsoft.Win32;
using System.Windows;

namespace ProjectLens.App.Services;

public sealed class WpfUserInteraction : IUserInteraction
{
    public string? ChooseInputFolder() => ChooseFolder("解析するプロジェクトフォルダーを選択");
    public string? ChooseOutputFolder() => ChooseFolder("生成資料の保存先を選択（解析対象の外）");

    public string? ChooseInputFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "ソリューションまたはプロジェクトを選択",
            Filter = "Visual Studio プロジェクト|*.sln;*.slnx;*.csproj|すべてのファイル|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    private static string? ChooseFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    }

    // クリップボードへ渡す処理は、この明示操作でのみ実行する。
    public void CopyText(string text) => Clipboard.SetText(text, TextDataFormat.UnicodeText);
}
