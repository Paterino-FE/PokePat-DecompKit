using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace TilesetEditor.App;

public partial class App : Application
{
    public static string CrashLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TilesetEditor", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => LogCrash("AppDomain", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { LogCrash("Task", a.Exception); a.SetObserved(); };

        var window = new MainWindow();
        MainWindow = window;
        int shotsAt = Array.IndexOf(e.Args, "--screenshots");
        string? shotsDir = shotsAt >= 0 && shotsAt + 1 < e.Args.Length ? e.Args[shotsAt + 1] : null;
        if (shotsDir != null)
        {
            // Rendered off-screen and never activated, so it does not disturb whoever is using the PC.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
        }
        window.Show();

        // Optional command line (shortcuts, scripted smoke tests):
        //   TilesetEditor.exe [<project-folder>] [--import <png>]...
        var imports = new List<string>();
        string? project = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--screenshots") { i++; continue; }
            if (e.Args[i] == "--import" && i + 1 < e.Args.Length) imports.Add(e.Args[++i]);
            else project ??= e.Args[i];
        }
        if (project != null) window.ViewModel.OpenProject(project);
        else if (window.ViewModel.NeedsSetup) window.Dispatcher.BeginInvoke(window.ViewModel.RunSetup, DispatcherPriority.ApplicationIdle);
        if (imports.Count > 0) window.ViewModel.ImportIntoTilesetEditor(imports);
        if (shotsDir != null) _ = TakeScreenshotsAsync(window, shotsDir);
    }

    /// <summary>--screenshots &lt;dir&gt;: renders every module to a PNG (for docs and design checks) and exits.</summary>
    private async Task TakeScreenshotsAsync(MainWindow window, string dir)
    {
        Directory.CreateDirectory(dir);
        await Task.Delay(4000);
        foreach (var module in window.ViewModel.Modules.ToList())
        {
            window.ViewModel.SelectedModule = module;
            await Task.Delay(3500);
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(content);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            var name = new string(module.Title.Where(char.IsLetterOrDigit).ToArray());
            using var fs = File.Create(Path.Combine(dir, $"{window.ViewModel.Modules.IndexOf(module):00}-{name}.png"));
            enc.Save(fs);
        }
        Shutdown();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("Dispatcher", e.Exception);
        MessageBox.Show($"Unexpected error:\n\n{e.Exception.Message}\n\n{e.Exception.GetType().Name}\n\nDetails: {CrashLogPath}",
            "PokéPat DecompKit", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, $"{DateTime.Now:O} [{source}] {ex}\n");
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
    }
}
