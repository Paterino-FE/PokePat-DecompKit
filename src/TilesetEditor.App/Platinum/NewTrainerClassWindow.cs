using System.IO;
using System.Windows;
using System.Windows.Controls;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Platinum;

namespace TilesetEditor.App.Platinum;

/// <summary>Dialog for a new Platinum trainer class (built in code).</summary>
public sealed class NewTrainerClassWindow : Window
{
    private readonly TextBox _constant = new() { Text = "TRAINER_CLASS_", FontFamily = new System.Windows.Media.FontFamily("Consolas") };
    private readonly TextBox _name = new();
    private readonly TextBox _article = new();
    private readonly ComboBox _copyFrom = new() { IsEditable = true };
    private readonly ComboBox _gender = new() { ItemsSource = new[] { "Male", "Female" }, SelectedIndex = 0, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _prize = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _picture = new();
    private readonly Image _preview = Ui.Picture();
    private readonly string _root;
    private bool _articleTouched;

    public NewTrainerClass? Result { get; private set; }

    public NewTrainerClassWindow(string root, IReadOnlyList<string> classes, string? copyFrom)
    {
        _root = root;
        Title = "New trainer class";
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        _copyFrom.ItemsSource = classes;
        _copyFrom.Text = copyFrom ?? "TRAINER_CLASS_ACE_TRAINER_FEMALE";
        _copyFrom.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(TakeValues);
        _copyFrom.LostFocus += (_, _) => TakeValues();
        _name.TextChanged += (_, _) => { if (!_articleTouched) _article.Text = Article(_name.Text); };
        _article.TextChanged += (_, _) => _articleTouched = _article.IsKeyboardFocused || _articleTouched;

        var p = new StackPanel { Margin = new Thickness(14), Width = 520 };
        p.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = "A new class with its own name and battle picture – no existing class changes. PokePat adds the constant at the end of " +
                   "generated/trainer_classes.txt, copies the picture files of a class with a similar layout, adds both names to the text banks " +
                   "and the gender and prize money to include/data.",
        });
        void Field(string label, FrameworkElement e, string? hint = null)
        {
            p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
            p.Children.Add(e);
            if (hint != null) p.Children.Add(Ui.Hint(hint, 520));
        }
        Field("Constant", _constant, "The folder is derived from it: TRAINER_CLASS_REYA → res/trainers/classes/reya.");
        Field("Name shown in battle", _name, "e.g. \"Empress\" – \"Empress Reya would like to battle!\"");
        Field("Name with article", _article, "e.g. \"an Empress\" (used in some texts).");
        Field("Copy picture files from", _copyFrom, "A class whose picture has a similar size and pose: its cell and animation data are reused.");
        Field("Gender", _gender, "Decides the battle intro and music for female trainers.");
        Field("Prize money multiplier (0–255)", _prize, "Prize money = level of the last Pokémon × 4 × this value.");
        var pick = new DockPanel();
        var browse = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) => Browse();
        DockPanel.SetDock(browse, Dock.Right);
        pick.Children.Add(browse);
        pick.Children.Add(_picture);
        Field("Battle picture (optional, PNG 80×80)", pick, "Up to 15 colours; transparent or the top-left colour = background. Empty = keep the copied picture for now.");
        p.Children.Add(new Border { Child = _preview, Width = 84, Height = 84, Background = Imaging.Checkerboard(8), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "Continue…", IsDefault = true, MinWidth = 90 };
        ok.Click += (_, _) => Finish();
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 });
        p.Children.Add(buttons);
        Content = p;
        TakeValues();
        Loaded += (_, _) => { _constant.Focus(); _constant.CaretIndex = _constant.Text.Length; };
    }

    private static string Article(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "";
        return ("aeiouAEIOU".Contains(name[0]) ? "an " : "a ") + name;
    }

    private void TakeValues()
    {
        var (female, prize) = PlatinumTrainerClasses.Values(_root, _copyFrom.Text.Trim());
        _gender.SelectedIndex = female ? 1 : 0;
        _prize.Text = prize.ToString();
    }

    private RgbaImage? _image;

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG pictures|*.png", Title = "Battle picture (80×80)" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _image = Png.Load(dlg.FileName).Rgba;
            _picture.Text = dlg.FileName;
            _preview.Source = Imaging.ToBitmap(_image);
        }
        catch (Exception ex) when (ex is IOException or PngFormatException)
        {
            AppDialog.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Finish()
    {
        var constant = string.Join("_", _constant.Text.Trim().ToUpperInvariant().Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries));
        if (!constant.StartsWith("TRAINER_CLASS_", StringComparison.Ordinal)) constant = "TRAINER_CLASS_" + constant;
        if (!int.TryParse(_prize.Text.Trim(), out var prize)) { AppDialog.Show(this, "The prize money multiplier must be a number.", Title); return; }
        if (_picture.Text.Trim().Length == 0) _image = null;
        Result = new NewTrainerClass(constant, _name.Text.Trim(), _article.Text.Trim(), _copyFrom.Text.Trim(), _gender.SelectedIndex == 1, prize, _image);
        DialogResult = true;
    }
}
