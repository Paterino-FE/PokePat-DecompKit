using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Platinum;
using TilesetEditor.Core.Project;
using TilesetEditor.Export;

namespace TilesetEditor.App.Shell;

/// <summary>A change of the backup list.</summary>
public sealed class BackupItemVm(BackupEntry entry, bool thisProject)
{
    public BackupEntry Entry { get; } = entry;
    public bool ThisProject { get; } = thisProject;
    public string Title => Entry.Title;
    public string When => Entry.Time.ToString("ddd dd.MM. HH:mm:ss");
    public string Details => $"{Entry.Files.Count} file(s)" + (Entry.HasManifest ? ThisProject ? "" : " · other project" : " · older backup (no details)");
    public double Opacity => ThisProject || !Entry.HasManifest ? 1 : 0.5;
}

/// <summary>
/// Every change PokéPat made, newest first: what it was, which files it created, changed or deleted – and the way back:
/// undo one change, or go back to the state before a change (undoing every later one too). Undoing makes a backup itself.
/// </summary>
public sealed class BackupsViewModel : ObservableObject, IToolkitModule
{
    private readonly ProjectContext _ctx;
    private readonly DeferredLoad _reload;
    private readonly ListBox _list = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _onlyThis = new() { Content = "Only changes of the open project", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) };
    private readonly StackPanel _detail = new();
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private List<BackupEntry> _all = [];

    public BackupsViewModel(ProjectContext ctx)
    {
        _ctx = ctx;
        _reload = new DeferredLoad(Load);
        _ctx.AnyProjectChanged += (_, _) => _reload.Request();
        View = BuildView();
        _reload.Attach(View);
    }

    public string Title => "Backups";
    public string Description => "Every change – undo it";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => false;
    public string? UnsavedChangesDescription => null;

    private FrameworkElement BuildView()
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(Ui.Title("Changes"));
        top.Children.Add(_status);
        top.Children.Add(_onlyThis);
        _onlyThis.Checked += (_, _) => Fill();
        _onlyThis.Unchecked += (_, _) => Fill();
        DockPanel.SetDock(top, Dock.Top);
        left.Children.Add(top);
        var bottom = Ui.Row(
            Ui.Button("⟳ Refresh", Load),
            Ui.Button("Delete backups older than 30 days", DeleteOld, "Frees disk space; newer backups stay."),
            Ui.Button("Open folder", () => { Directory.CreateDirectory(ExportApplier.DefaultBackupRoot); Process.Start("explorer.exe", $"\"{ExportApplier.DefaultBackupRoot}\""); }));
        DockPanel.SetDock(bottom, Dock.Bottom);
        left.Children.Add(bottom);
        _list.ItemTemplate = Template();
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) => ShowDetail();
        left.Children.Add(_list);
        grid.Children.Add(left);
        var right = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(16, 0, 0, 0) };
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        _detail.Children.Add(Ui.Hint("Choose a change on the left. Every time PokéPat writes to your project it keeps the old files – here you can see what changed and take it back."));
        return grid;
    }

    private static DataTemplate Template()
    {
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 3));
        stack.SetBinding(UIElement.OpacityProperty, new System.Windows.Data.Binding(nameof(BackupItemVm.Opacity)));
        var t = new FrameworkElementFactory(typeof(TextBlock));
        t.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(BackupItemVm.Title)));
        t.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        t.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        stack.AppendChild(t);
        var d = new FrameworkElementFactory(typeof(TextBlock));
        d.SetBinding(TextBlock.TextProperty, new System.Windows.Data.MultiBinding
        {
            StringFormat = "{0}  ·  {1}",
            Bindings = { new System.Windows.Data.Binding(nameof(BackupItemVm.When)), new System.Windows.Data.Binding(nameof(BackupItemVm.Details)) },
        });
        d.SetValue(TextBlock.FontSizeProperty, 11.0);
        d.SetValue(TextBlock.ForegroundProperty, Brushes.Gray);
        stack.AppendChild(d);
        return new DataTemplate { VisualTree = stack };
    }

    private void Load()
    {
        try { _all = BackupHistory.List(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _all = []; _status.Text = ex.Message; }
        Fill();
    }

    private void Fill()
    {
        var root = _ctx.Root;
        var items = _all.Select(e => new BackupItemVm(e, root != null && BackupHistory.SameProject(e, root)))
            .Where(i => _onlyThis.IsChecked != true || i.ThisProject || !i.Entry.HasManifest).ToList();
        var keep = (_list.SelectedItem as BackupItemVm)?.Entry.Folder;
        _list.ItemsSource = items;
        _list.SelectedItem = items.FirstOrDefault(i => i.Entry.Folder == keep);
        long bytes = 0;
        try { bytes = Directory.Exists(ExportApplier.DefaultBackupRoot) ? Directory.EnumerateFiles(ExportApplier.DefaultBackupRoot, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _status.Text = $"{_all.Count} backups ({bytes / 1e6:0.0} MB) in {ExportApplier.DefaultBackupRoot}";
    }

    private void ShowDetail()
    {
        _detail.Children.Clear();
        if (_list.SelectedItem is not BackupItemVm item) return;
        var e = item.Entry;
        var root = _ctx.Root;
        _detail.Children.Add(Ui.Title(e.Title));
        _detail.Children.Add(new TextBlock { Text = $"{e.Time:dddd, dd.MM.yyyy HH:mm:ss}" + (e.Project != null ? $"  ·  {e.Project}" : ""), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        if (!e.HasManifest)
            _detail.Children.Add(Ui.Hint("An older backup (from before PokéPat described its changes): it keeps the old content of the files below, but not which project they belong to or which files were new. \"Undo\" puts these files back into the open project.", 760));
        else if (root != null && !item.ThisProject)
            _detail.Children.Add(Ui.Hint("This change belongs to another project – open that project to undo it.", 760));

        foreach (var f in e.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var state = "";
            if (root != null && e.HasManifest && item.ThisProject)
            {
                var full = ProjectPaths.Combine(root, f.Path);
                bool exists = File.Exists(full);
                state = f.Action == PlannedAction.Delete ? (exists ? " · exists again" : "")
                    : !exists ? " · missing now"
                    : f.WrittenSha256 != null && Hashing.Sha256(File.ReadAllBytes(full)) != f.WrittenSha256 ? " · changed again since" : " · unchanged since";
            }
            var icon = f.Action switch { PlannedAction.Create => "＋", PlannedAction.Delete => "－", _ => "✎" };
            _detail.Children.Add(new TextBlock
            {
                Text = $"{icon} {f.Path}{state}", FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Foreground = state.Contains("again") || state.Contains("missing") ? (Brush)Application.Current.FindResource("WarnTextBrush") : (Brush)Application.Current.FindResource("InkBrush"),
                ToolTip = f.Action switch { PlannedAction.Create => "Added by this change", PlannedAction.Delete => "Deleted by this change", _ => "Changed by this change" },
            });
        }
        if (root == null) return;
        var buttons = Ui.Row(
            Ui.Button("↩ Undo this change", () => Undo(e), "Takes back only this change (the files get the content they had before it)."),
            Ui.Button("Show backup folder", () => Process.Start("explorer.exe", $"\"{e.Folder}\"")));
        if (e.HasManifest && item.ThisProject)
        {
            int later = _all.Count(x => x.HasManifest && BackupHistory.SameProject(x, root) && x.Time > e.Time);
            if (later > 0)
                buttons.Children.Insert(1, Ui.Button($"⏪ Back to before this ({later} later change(s) too)", () => BackTo(e),
                    "The project goes back to the state before this change: it and every later change are undone."));
        }
        else if (e.HasManifest) buttons.Children[0].IsEnabled = false;
        _detail.Children.Add(new Border { Height = 10 });
        _detail.Children.Add(buttons);
    }

    private void Undo(BackupEntry e)
    {
        if (_ctx.Root is not { } root) return;
        if (!e.HasManifest && AppDialog.Show(Application.Current.MainWindow,
                $"This older backup does not say which project it belongs to. Put its {e.Files.Count} file(s) back into the open project?\n\n{root}",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var (plan, conflicts) = BackupHistory.PlanUndo(e, root);
        Apply(plan, conflicts);
    }

    private void BackTo(BackupEntry e)
    {
        if (_ctx.Root is not { } root) return;
        var (plan, conflicts, undone) = BackupHistory.PlanBackTo(e, _all, root);
        if (AppDialog.Show(Application.Current.MainWindow,
                $"Undo these {undone.Count} change(s), newest first?\n\n• " + string.Join("\n• ", undone.Take(15).Select(u => $"{u.Time:dd.MM. HH:mm} {u.Title}")) + (undone.Count > 15 ? "\n• …" : ""),
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Apply(plan, conflicts);
    }

    private void Apply(ExportPlan plan, List<BackupConflict> conflicts)
    {
        if (plan.Files.Count == 0) { ProjectWriter.Warn(Title, "Nothing to do – the files are already as before." + (conflicts.Count > 0 ? "\n\n" + string.Join("\n", conflicts.Select(c => $"{c.Path}: {c.Reason}")) : "")); return; }
        if (conflicts.Count > 0 && AppDialog.Show(Application.Current.MainWindow,
                "Some files were changed again after this change – undoing replaces those later changes too:\n\n• " + string.Join("\n• ", conflicts.Select(c => $"{c.Path}: {c.Reason}")) + "\n\nContinue?",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (ProjectWriter.Apply(plan.ProjectRoot, plan.TilesetName, "", plan.Files.ToList(), "Undoing is a change itself: it has its own backup and can be undone again.", "Build the ROM afterwards."))
            _ctx.Reload();
        Load();
    }

    private void DeleteOld()
    {
        int old = _all.Count(e => e.Time < DateTime.Now.AddDays(-30));
        if (old == 0) { ProjectWriter.Warn(Title, "No backup is older than 30 days."); return; }
        if (AppDialog.Show(Application.Current.MainWindow, $"Delete {old} backup(s) older than 30 days? They cannot be undone afterwards.", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var n = BackupHistory.DeleteOlderThan(30);
        ProjectWriter.Warn(Title, $"{n} backup(s) deleted.");
        Load();
    }
}
