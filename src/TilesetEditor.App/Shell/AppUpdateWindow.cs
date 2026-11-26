using TilesetEditor.App.Infrastructure;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TilesetEditor.Toolkit.Install;

namespace TilesetEditor.App.Shell;

/// <summary>
/// "PokéPat x.y is available": what is new (the release text), then download, check and swap the files – PokéPat closes,
/// a small script replaces the files (backup first, restored on failure) and starts the new version.
/// </summary>
public sealed class AppUpdateWindow : Window
{
    private readonly AppRelease _release;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ProgressBar _progress = new() { Height = 6, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _update = new() { Content = "⬆ Update now and restart", IsDefault = true, FontWeight = FontWeights.SemiBold, MinWidth = 190 };
    private readonly CancellationTokenSource _cancel = new();
    private readonly IReadOnlyList<string> _unsaved;

    public AppUpdateWindow(AppRelease release, string current, IReadOnlyList<string> unsaved)
    {
        _release = release;
        _unsaved = unsaved;
        Title = "Update PokéPat";
        Width = 640;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("PanelBrush");

        var root = new DockPanel { Margin = new Thickness(16) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = $"PokéPat {release.Version} is available", FontSize = 20, FontWeight = FontWeights.SemiBold });
        head.Children.Add(new TextBlock { Text = $"You have {current}. Download: {release.Package.Name} ({release.Package.Size / 1e6:0} MB)" +
                                                 (release.Package.Digest != null ? ", checked with SHA-256" : ""), Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 10) });
        head.Children.Add(new TextBlock { Text = "What is new", FontWeight = FontWeights.SemiBold });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        bottom.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("MutedBrush"),
            Text = "PokéPat closes, the new files replace the old ones (a backup is kept in the program folder under .update-backup and put back if anything fails) " +
                   "and PokéPat starts again. Your settings, your own mods (Documents\\PokePat Mods) and your projects are not touched.",
        });
        if (unsaved.Count > 0)
            bottom.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("AccentDarkBrush"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0),
                Text = "Not saved yet – save first, or it is lost:\n• " + string.Join("\n• ", unsaved),
            });
        bottom.Children.Add(_status);
        bottom.Children.Add(_progress);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var page = new Button { Content = "Release page", MinWidth = 110 };
        page.Click += (_, _) => Process.Start(new ProcessStartInfo(release.PageUrl) { UseShellExecute = true });
        var later = new Button { Content = "Later", IsCancel = true, MinWidth = 80 };
        later.Click += (_, _) => _cancel.Cancel();
        _update.Click += async (_, _) => await UpdateAsync();
        buttons.Children.Add(page);
        buttons.Children.Add(_update);
        buttons.Children.Add(later);
        bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        root.Children.Add(new TextBox
        {
            Text = release.Notes.Length > 0 ? release.Notes.Replace("\r\n", "\n") : "(The release has no description – see the release page.)",
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13, Padding = new Thickness(6),
        });
        Content = root;
    }

    private async Task UpdateAsync()
    {
        var exe = Environment.ProcessPath;
        var installDir = exe != null ? Path.GetDirectoryName(exe) : null;
        if (exe == null || installDir == null || !string.Equals(Path.GetFileName(exe), AppSelfUpdate.ExeName, StringComparison.OrdinalIgnoreCase))
        {
            ProjectWriter.Warn(Title, $"Only the packaged PokéPat ({AppSelfUpdate.ExeName}) can update itself – this one runs as {Path.GetFileName(exe ?? "?")} (a development build). Use the release page.");
            return;
        }
        if (!AppSelfUpdate.CanWrite(installDir))
        {
            ProjectWriter.Warn(Title, $"PokéPat cannot write to its folder ({installDir}). Move PokéPat to a folder of your own (e.g. Documents) or update from the release page.");
            return;
        }
        if (_unsaved.Count > 0 && AppDialog.Show(this, "Some changes are not saved yet and will be lost. Update anyway?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        _update.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        try
        {
            var work = Path.Combine(Path.GetTempPath(), "PokePat-update");
            var progress = new Progress<string>(m => _status.Text = m.Trim());
            var newDir = await Task.Run(() => AppSelfUpdate.DownloadAsync(_release, work, ((IProgress<string>)progress).Report, _cancel.Token));
            var log = Path.Combine(work, "update.log");
            var script = Path.Combine(work, "swap.ps1");
            await File.WriteAllTextAsync(script, AppSelfUpdate.SwapScript(newDir, installDir, Environment.ProcessId, log), new System.Text.UTF8Encoding(true));
            _status.Text = "Ready – PokéPat restarts now …";
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
            });
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) { _status.Text = "Cancelled – nothing was changed."; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.Http.HttpRequestException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _status.Text = "The update did not work: " + ex.Message + " Nothing was changed.";
        }
        finally
        {
            _progress.Visibility = Visibility.Collapsed;
            _update.IsEnabled = true;
        }
    }
}
