using TilesetEditor.App.Infrastructure;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using TilesetEditor.Core.Events;
using TilesetEditor.Core.Maps;
using TilesetEditor.Core.Project;

namespace TilesetEditor.App.Modules.Events;

/// <summary>Read-only view of a map's existing scripts (MapScriptReader).</summary>
public partial class MapScriptsWindow : Window
{
    public sealed record LabelRow(ScriptLabel Label)
    {
        public string Name => Label.Name;
        public string KindText => Label.KindText;
        public string UsedByText => Label.UsedBy.Count > 0 ? "used by " + string.Join(", ", Label.UsedBy) : "";
    }

    public sealed record LineRow(ScriptLine Line)
    {
        public string Icon => Line.Icon;
        public string Text => Line.Text;
        public string Raw => $"line {Line.Line}: {Line.Raw}";
        public Visibility JumpVisibility => Line.Jump != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private readonly string _root;
    private readonly string _map;
    private ProjectInfo? _project;
    private readonly MapScriptReader.Result _result;

    public MapScriptsWindow(string root, string map, ProjectInfo? project = null, string? select = null)
    {
        InitializeComponent();
        _root = root;
        _map = map;
        _project = project;
        _result = MapScriptReader.Read(root, map);
        Title = $"Existing scripts – {map}";
        Header.Text = $"{map}  ·  {_result.File}  ·  {_result.Labels.Count(l => l.Kind == ScriptLabelKind.Script)} scripts, " +
                      $"{_result.Labels.Count(l => l.Kind == ScriptLabelKind.Movement)} movements, {_result.Labels.Count(l => l.Kind == ScriptLabelKind.Text)} texts";
        if (_result.Note != null) Note.Text = _result.Note + "  " + Note.Text;
        Fill(select);
    }

    private void Fill(string? select = null)
    {
        if (Labels == null) return;
        bool Show(ScriptLabel l) => l.Kind switch
        {
            ScriptLabelKind.Movement => ShowMovements.IsChecked == true,
            ScriptLabelKind.Text => ShowTexts.IsChecked == true,
            _ => ShowScripts.IsChecked == true,
        };
        var rows = _result.Labels.Where(Show).Select(l => new LabelRow(l)).ToList();
        Labels.ItemsSource = rows;
        Labels.SelectedItem = rows.FirstOrDefault(r => r.Name == select) ?? rows.FirstOrDefault(r => r.Label.UsedBy.Count > 0) ?? rows.FirstOrDefault();
        if (Labels.SelectedItem != null) Labels.ScrollIntoView(Labels.SelectedItem);
    }

    private void FilterChanged(object sender, RoutedEventArgs e) => Fill((Labels?.SelectedItem as LabelRow)?.Name);

    private void LabelChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var label = (Labels.SelectedItem as LabelRow)?.Label;
        Lines.ItemsSource = label?.Lines.Select(l => new LineRow(l)).ToList();
        ShowOnMapButton.IsEnabled = label is { Kind: ScriptLabelKind.Script or ScriptLabelKind.Movement };
    }

    /// <summary>Opens the map preview with the movement phases of the selected label.</summary>
    private void ShowOnMap(object sender, RoutedEventArgs e)
    {
        if (Labels.SelectedItem is not LabelRow row) return;
        try
        {
            _project ??= ProjectLoader.Load(_root);
            var phases = ScriptMovementPhases.Read(_root, _map, row.Name);
            new MovementPreviewWindow(_project, _map, phases.Phases, $"{row.Name}" + (row.Label.UsedBy.Count > 0 ? $"  ← {string.Join(", ", row.Label.UsedBy)}" : ""),
                row.Name, phases.Warnings) { Owner = this }.Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectLoadException or FormatException)
        {
            AppDialog.Show(this, $"The map could not be shown:\n{ex.Message}", "Show movements on map", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Jump()
    {
        if (Lines.SelectedItem is not LineRow { Line.Jump: { } target }) return;
        var label = _result.Labels.FirstOrDefault(l => l.Name == target);
        if (label == null) return;
        if (label.Kind == ScriptLabelKind.Movement) ShowMovements.IsChecked = true;
        else if (label.Kind == ScriptLabelKind.Text) ShowTexts.IsChecked = true;
        else ShowScripts.IsChecked = true;
        Fill(target);
    }

    private void LineActivated(object sender, MouseButtonEventArgs e) => Jump();

    private void LineKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Jump();
    }

    private void OpenFile(object sender, RoutedEventArgs e)
    {
        var full = Path.Combine(_root, _result.File.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) return;
        try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { Process.Start("explorer.exe", $"/select,\"{full}\""); }
    }
}
