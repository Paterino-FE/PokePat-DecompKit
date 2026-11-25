using System.Windows;
using System.Windows.Controls;
using TilesetEditor.App.Infrastructure;

namespace TilesetEditor.App.Shell;

/// <summary>A tool in the toolkit's navigation. Modules share the <see cref="ProjectContext"/>.</summary>
public interface IToolkitModule
{
    string Title { get; }
    string Description { get; }
    /// <summary>False for modules that are announced but not implemented yet.</summary>
    bool IsAvailable { get; }
    FrameworkElement View { get; }
    /// <summary>Work that exists only in the toolkit and is not yet written to the project.</summary>
    bool HasUnsavedChanges { get; }
    string? UnsavedChangesDescription { get; }
}

/// <summary>Placeholder for modules of later stages, so the navigation shows the planned structure.</summary>
public sealed class PlannedModule(string title, string description, string plan) : ObservableObject, IToolkitModule
{
    private FrameworkElement? _view;

    public string Title => title;
    public string Description => description;
    public bool IsAvailable => false;
    public override string ToString() => Title;
    public bool HasUnsavedChanges => false;
    public string? UnsavedChangesDescription => null;

    public FrameworkElement View => _view ??= new ScrollViewer
    {
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SectionTitle"] },
                new TextBlock { Text = "Planned – not implemented in this version yet.", FontStyle = FontStyles.Italic, Margin = new Thickness(0, 0, 0, 10) },
                new TextBlock { Text = plan, Style = (Style)Application.Current.Resources["Hint"], MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left },
            },
        },
    };

    public static IEnumerable<PlannedModule> Roadmap() =>
    [
    ];
}
