using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using TilesetEditor.App.Modules.Tilesets;
using TilesetEditor.Core.Project;
using TilesetEditor.Export;

namespace TilesetEditor.App.Shell;

/// <summary>Shared write path of the modules: file list preview → confirm → transactional apply (with rollback).</summary>
public static class ProjectWriter
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Planned modification of a text file; the current hash is the precondition, so a concurrent change aborts.</summary>
    public static PlannedFile ModifyText(string root, string rel, Func<string, string> change, string description)
    {
        var full = ProjectPaths.Combine(root, rel);
        var original = File.ReadAllBytes(full);
        var updated = change(Utf8.GetString(original));
        return new PlannedFile(rel, PlannedAction.Modify, Utf8.GetBytes(updated), description, Hashing.Sha256(original));
    }

    /// <summary>Shows the plan; on confirmation writes it. Returns true if written.</summary>
    public static bool Apply(string root, string title, string folder, IReadOnlyList<PlannedFile> files, params string[] notes)
    {
        if (files.Count == 0) return false;
        var plan = ExportPlan.ForFiles(root, title, folder, files);
        plan.Notes.AddRange(notes);
        if (new ExportPlanWindow(plan) { Owner = Application.Current.MainWindow }.ShowDialog() != true) return false;
        var result = new ExportApplier().Apply(plan);
        if (result.Success) return true;
        MessageBox.Show(Application.Current.MainWindow, "Error: " + result.Error + (result.RolledBack ? "\nAll changes were rolled back." : ""),
            title, MessageBoxButton.OK, MessageBoxImage.Error);
        return false;
    }

    public static void Warn(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
}

/// <summary>Small dialog with labelled text fields (built in code, no XAML).</summary>
public sealed class InputDialog : Window
{
    private readonly List<TextBox> _boxes = [];

    public InputDialog(string title, string intro, params (string Label, string Value)[] fields)
    {
        Title = title;
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(14), MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 0, 0, 10) });
        foreach (var (label, value) in fields)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 4, 0, 2) });
            var box = new TextBox { Text = value };
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            _boxes.Add(box);
            panel.Children.Add(box);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 });
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { if (_boxes.Count > 0) { _boxes[0].Focus(); _boxes[0].SelectAll(); } };
    }

    public IReadOnlyList<string> Values => _boxes.Select(b => b.Text.Trim()).ToList();
}
