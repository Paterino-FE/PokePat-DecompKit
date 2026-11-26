using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TilesetEditor.App.Infrastructure;

/// <summary>
/// Message boxes in the look of the app (theme colours, the game's header colour, light/dark), with the same calls and
/// results as <see cref="MessageBox.Show(string)"/>. The text can be selected and copied (e.g. the path of a crash log).
/// Falls back to the system message box if the app's resources are not there (very early or very broken states).
/// </summary>
public static class AppDialog
{
    public static MessageBoxResult Show(string text) => Show(null, text, "PokéPat DecompKit", MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(string text, string caption) => Show(null, text, caption, MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button) => Show(null, text, caption, button, MessageBoxImage.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon) => Show(null, text, caption, button, icon);
    public static MessageBoxResult Show(Window? owner, string text) => Show(owner, text, "PokéPat DecompKit", MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption) => Show(owner, text, caption, MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button) => Show(owner, text, caption, button, MessageBoxImage.None);

    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        if (Application.Current?.TryFindResource("SurfaceBrush") is not Brush surface || Application.Current.Dispatcher.HasShutdownStarted)
            return owner != null ? MessageBox.Show(owner, text, caption, button, icon) : MessageBox.Show(text, caption, button, icon);
        if (!Application.Current.Dispatcher.CheckAccess())
            return Application.Current.Dispatcher.Invoke(() => Show(owner, text, caption, button, icon));
        try { return ShowThemed(owner, text, caption, button, icon, surface); }
        catch (Exception ex) when (ex is InvalidOperationException or ResourceReferenceKeyNotFoundException)
        {
            return owner != null ? MessageBox.Show(owner, text, caption, button, icon) : MessageBox.Show(text, caption, button, icon);
        }
    }

    private static Brush Res(string key, Brush fallback) => Application.Current.TryFindResource(key) as Brush ?? fallback;

    private static MessageBoxResult ShowThemed(Window? owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon, Brush surface)
    {
        owner ??= Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
        if (owner is { IsLoaded: false }) owner = null;
        var result = button switch
        {
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK,
        };
        var (symbol, accent) = icon switch
        {
            MessageBoxImage.Error => ("✖", Res("ErrorBrush", Brushes.IndianRed)),
            MessageBoxImage.Warning => ("⚠", Res("WarnTextBrush", Brushes.DarkOrange)),
            MessageBoxImage.Question => ("?", Res("AccentBrush", Brushes.SteelBlue)),
            MessageBoxImage.Information => ("ℹ", Res("AccentBrush", Brushes.SteelBlue)),
            _ => ("", Res("AccentBrush", Brushes.SteelBlue)),
        };

        var w = new Window
        {
            Title = caption,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner == null,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            MinWidth = 380,
            MaxWidth = 720,
            Background = surface,
        };

        // Header in the app's roof colour (purple for Platinum, green for Emerald), with the symbol and the caption.
        var header = new Border { Background = Res("RoofBrush", Res("AccentBrush", Brushes.SteelBlue)), Padding = new Thickness(18, 12, 18, 12) };
        var headRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (symbol.Length > 0)
            headRow.Children.Add(new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Brushes.White, Margin = new Thickness(0, 0, 12, 0),
                Child = new TextBlock { Text = symbol, FontSize = 17, FontWeight = FontWeights.Bold, Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
        headRow.Children.Add(new TextBlock { Text = caption, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 620 });
        header.Child = headRow;

        // The text: selectable, so error details and paths can be copied.
        var body = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Foreground = Res("InkBrush", Brushes.Black), FontSize = 13.5, Margin = new Thickness(20, 16, 20, 12), MaxHeight = 460,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Cursor = Cursors.Arrow, Focusable = true,
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 4, 20, 16) };
        Button Add(string label, MessageBoxResult value, bool isDefault, bool isCancel)
        {
            var b = new Button { Content = label, MinWidth = 92, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
            if (isDefault) b.FontWeight = FontWeights.SemiBold;
            b.Click += (_, _) => { result = value; w.Close(); };
            buttons.Children.Add(b);
            return b;
        }
        Button first = button switch
        {
            MessageBoxButton.OKCancel => Add("OK", MessageBoxResult.OK, true, false),
            MessageBoxButton.YesNo => Add("Yes", MessageBoxResult.Yes, true, false),
            MessageBoxButton.YesNoCancel => Add("Yes", MessageBoxResult.Yes, true, false),
            _ => Add("OK", MessageBoxResult.OK, true, true),
        };
        if (button == MessageBoxButton.OKCancel) Add("Cancel", MessageBoxResult.Cancel, false, true);
        if (button == MessageBoxButton.YesNo) Add("No", MessageBoxResult.No, false, true);
        if (button == MessageBoxButton.YesNoCancel) { Add("No", MessageBoxResult.No, false, false); Add("Cancel", MessageBoxResult.Cancel, false, true); }

        var copy = new Button { Content = "Copy", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(20, 4, 0, 16), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, ToolTip = "Copy the text" };
        copy.Click += (_, _) => { try { Clipboard.SetText(caption + "\n\n" + text); } catch (System.Runtime.InteropServices.ExternalException) { } };
        var footer = new DockPanel();
        DockPanel.SetDock(copy, Dock.Left);
        footer.Children.Add(copy);
        footer.Children.Add(buttons);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(body);
        w.Content = root;
        w.Loaded += (_, _) => first.Focus();
        // A message box makes a sound like the system one.
        (icon switch
        {
            MessageBoxImage.Error => System.Media.SystemSounds.Hand,
            MessageBoxImage.Warning => System.Media.SystemSounds.Exclamation,
            MessageBoxImage.Question => System.Media.SystemSounds.Question,
            _ => System.Media.SystemSounds.Asterisk,
        }).Play();
        w.ShowDialog();
        return result;
    }
}
