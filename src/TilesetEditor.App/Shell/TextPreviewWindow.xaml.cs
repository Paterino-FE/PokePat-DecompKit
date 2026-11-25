using System.Windows;

namespace TilesetEditor.App.Shell;

/// <summary>Shows planned file changes as text; nothing is written unless the user clicks "Apply".</summary>
public partial class TextPreviewWindow : Window
{
    public TextPreviewWindow(string title, string intro, string body, string applyText = "Apply")
    {
        InitializeComponent();
        Title = title;
        Intro.Text = intro;
        Body.Text = body;
        ApplyButton.Content = applyText;
    }

    private void OnApply(object sender, RoutedEventArgs e) => DialogResult = true;
}
