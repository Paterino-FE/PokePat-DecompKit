using System.Windows;
using TilesetEditor.Export;

namespace TilesetEditor.App.Modules.Tilesets;

/// <summary>Lists every file the export will create or change. Nothing is written until the user confirms.</summary>
public partial class ExportPlanWindow : Window
{
    public ExportPlanWindow(ExportPlan plan)
    {
        InitializeComponent();
        Header.Text = $"{plan.TilesetName} → {plan.TilesetFolder}/";
        SubHeader.Text = $"Project: {plan.ProjectRoot}\n{plan.Created.Count()} new files, {plan.Modified.Count()} modified files. " +
                         "The changes were checked in advance. When applying, modified files are " +
                         "backed up first; if a write fails, the previous state is restored.";
        Files.ItemsSource = plan.Files;
        NotesList.ItemsSource = plan.Notes;
    }

    private void OnApply(object sender, RoutedEventArgs e) => DialogResult = true;
}
