using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ProjectLens.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smokeTest = e.Args.Contains("--smoke-test", StringComparer.Ordinal);
        if (!smokeTest)
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            return;
        }

        // 開発時の起動検証は画面表示を隠す。通常起動時には自動保存・自動解析を実行しない。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow? smokeWindow = null;
        try
        {
            smokeWindow = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
            MainWindow = smokeWindow;
            smokeWindow.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var sample = ArgumentValue(e.Args, "--sample") ?? ArgumentValue(e.Args, "--input");
            if (sample is not null) await smokeWindow.ViewModel.RunSmokeTestAsync(sample);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var originalWidth = smokeWindow.Width;
            var originalHeight = smokeWindow.Height;
            smokeWindow.Width = smokeWindow.MinWidth;
            smokeWindow.Height = smokeWindow.MinHeight;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            smokeWindow.MainContent.UpdateLayout();
            if (smokeWindow.PreviewBox.ActualHeight < 60 || smokeWindow.ContextSettingsScroll.ViewportHeight < 30
                || smokeWindow.GenerateButton.ActualHeight < 25)
                throw new InvalidOperationException("最小ウィンドウサイズで設定・生成・プレビューの表示領域が不足しています。");
            smokeWindow.Width = originalWidth;
            smokeWindow.Height = originalHeight;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var screenshot = ArgumentValue(e.Args, "--screenshot");
            if (screenshot is not null) SaveDiagnosticScreenshot(smokeWindow, screenshot);
            smokeWindow.Close();
            Shutdown(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WPF smoke test failed: {ex.Message}");
            smokeWindow?.Close();
            Shutdown(1);
        }
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"{name} の値が必要です。");
        return args[index + 1];
    }

    private static void SaveDiagnosticScreenshot(MainWindow window, string filePath)
    {
        // 明示された開発用オプションの場合だけ、指定PNGを作成する。既存ファイルは上書きしない。
        if (!Path.IsPathFullyQualified(filePath)) throw new ArgumentException("画面診断の保存先には絶対パスを指定してください。");
        var path = Path.GetFullPath(filePath);
        if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("画面診断の保存先には .png を指定してください。");
        ProjectLens.Core.Export.ContextExporter.ValidateOutputDirectory(Path.GetDirectoryName(path)!,
            string.IsNullOrEmpty(window.ViewModel.RootPath) ? null : window.ViewModel.RootPath);
        window.MainContent.UpdateLayout();
        // Renderは親内でのオフセットも含むため、外側余白まで確保しないと右端・下端が欠ける。
        var offset = VisualTreeHelper.GetOffset(window.MainContent);
        var width = (int)Math.Ceiling(offset.X + window.MainContent.ActualWidth + window.MainContent.Margin.Right);
        var height = (int)Math.Ceiling(offset.Y + window.MainContent.ActualHeight + window.MainContent.Margin.Bottom);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(window.MainContent);
        var dpi = VisualTreeHelper.GetDpi(window.MainContent);
        Console.Out.WriteLine($"WPF layout: {window.MainContent.ActualWidth:0}x{window.MainContent.ActualHeight:0}, offset {offset.X:0},{offset.Y:0}, bitmap {width}x{height}, DPI {dpi.PixelsPerInchX:0}x{dpi.PixelsPerInchY:0}");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }
}
