using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Graphics;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;
using TilesetEditor.Core.SecretBases;
using TilesetEditor.Core.Sprites;
using TilesetEditor.Export;

namespace TilesetEditor.App.Modules.SecretBases;

/// <summary>Kind of a new sprite decoration, taken from an existing one (category + shape).</summary>
public sealed record DecorationTemplate(string Category, string Shape, string Example)
{
    public override string ToString() => $"{Category.Replace("DECORCAT_", "").ToLowerInvariant() switch { "doll" => "Doll", "cushion" => "Cushion", var c => c }} " +
                                         $"{Shape.Replace("DECORSHAPE_", "")}  (like {Example})";
}

/// <summary>Collects a <see cref="NewDecorationRequest"/>: names, kind, price, description, overworld sprite (existing or new PNG), optional icon.</summary>
public sealed class NewDecorationWindow : Window
{
    private readonly ProjectInfo _project;
    private readonly IReadOnlyList<OverworldSprite> _sprites;
    private readonly IReadOnlyDictionary<string, Rgba32[]> _palettes;
    private readonly TextBox _name = new() { Text = "NEW DOLL" };
    private readonly TextBox _constant = new() { Text = "NEW_DOLL", FontFamily = new FontFamily("Consolas") };
    private readonly ComboBox _kind = new();
    private readonly TextBox _price = new() { Text = "3000", Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _description = new() { AcceptsReturn = true, Height = 64, TextWrapping = TextWrapping.NoWrap, Text = "A doll.\r\nPlace it on a mat\r\nor a desk." };
    private readonly RadioButton _useExisting = new() { Content = "Existing overworld sprite", IsChecked = true, GroupName = "sprite" };
    private readonly ComboBox _sprite = new() { IsTextSearchEnabled = true };
    private readonly RadioButton _useNew = new() { Content = "Import a new sprite (PNG)", GroupName = "sprite", Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _browseSprite = new() { Content = "Choose PNG…" };
    private readonly TextBlock _spriteFile = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly ComboBox _frame = new() { Width = 80 };
    private readonly CheckBox _ownPalette = new() { Content = "Own new palette (from the image's colors)", IsChecked = true };
    private readonly ComboBox _paletteTag = new() { IsEnabled = false };
    private readonly CheckBox _nearest = new() { Content = "Use closest palette colors" };
    private readonly CheckBox _useIcon = new() { Content = "Own menu icon (24×24 PNG, max. 15 colors)" };
    private readonly Button _browseIcon = new() { Content = "Choose icon…" };
    private readonly TextBlock _iconFile = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly Image _preview = new() { Stretch = Stretch.None, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private string? _spritePng, _iconPng;
    private bool _constantTouched;

    public NewDecorationRequest? Request { get; private set; }

    public NewDecorationWindow(ProjectInfo project, DecorationData data, IReadOnlyList<OverworldSprite> sprites, IReadOnlyDictionary<string, Rgba32[]> palettes)
    {
        _project = project;
        _sprites = sprites;
        _palettes = palettes;
        Title = "New sprite decoration (doll / cushion)";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 900;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        var templates = data.Items.Where(d => d.IsSprite && DecorationPlanner.SpriteCategories.Contains(d.Category))
            .GroupBy(d => (d.Category, d.Shape)).Select(g => new DecorationTemplate(g.Key.Category, g.Key.Shape, g.First().Name)).ToList();
        if (templates.Count == 0) templates.Add(new DecorationTemplate("DECORCAT_DOLL", "DECORSHAPE_1x1", "–"));
        _kind.ItemsSource = templates;
        _kind.SelectedIndex = 0;
        var spriteList = sprites.OrderByDescending(s => s.GfxId.Contains("DOLL") || s.GfxId.Contains("CUSHION")).ThenBy(s => s.GfxId).ToList();
        _sprite.ItemsSource = spriteList;
        _sprite.SelectedItem = spriteList.FirstOrDefault();
        _frame.ItemsSource = new[] { "16x16", "32x32", "16x32", "64x64" };
        _frame.SelectedIndex = 0;
        _paletteTag.ItemsSource = palettes.Keys.OrderBy(k => k).ToList();
        _paletteTag.SelectedIndex = 0;

        var panel = new StackPanel { Margin = new Thickness(14) };
        void Label(string text) => panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
        void Hint(string text) => panel.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.Resources["Hint"], TextWrapping = TextWrapping.Wrap });
        Hint("Sprite decorations are shown as an overworld object when placed in a secret base (or the player's room). " +
             "Only dolls and cushions work this way; desks, mats etc. are metatiles of the secret base tileset.");
        Label("Name (in-game, max. 15 characters)");
        panel.Children.Add(_name);
        Label("Constant");
        panel.Children.Add(_constant);
        Hint("Becomes DECOR_<CONSTANT>.");
        Label("Kind");
        panel.Children.Add(_kind);
        Label("Price");
        panel.Children.Add(_price);
        Label("Description (max. 3 lines)");
        panel.Children.Add(_description);
        Label("Graphics");
        panel.Children.Add(_useExisting);
        panel.Children.Add(_sprite);
        panel.Children.Add(_useNew);
        var newRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 2, 0, 0) };
        newRow.Children.Add(_browseSprite);
        newRow.Children.Add(new TextBlock { Text = "Frame size:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) });
        newRow.Children.Add(_frame);
        panel.Children.Add(newRow);
        var newOptions = new StackPanel { Margin = new Thickness(18, 2, 0, 0) };
        newOptions.Children.Add(_spriteFile);
        newOptions.Children.Add(_ownPalette);
        newOptions.Children.Add(_paletteTag);
        newOptions.Children.Add(_nearest);
        newOptions.Children.Add(new TextBlock
        {
            Text = "A single frame (e.g. 16×16 for a small doll, 32×32 for a big one). It is registered like in the Sprites module.",
            Style = (Style)Application.Current.Resources["Hint"], TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(newOptions);
        Label("Menu icon");
        panel.Children.Add(_useIcon);
        var iconRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 2, 0, 0) };
        iconRow.Children.Add(_browseIcon);
        iconRow.Children.Add(_iconFile);
        panel.Children.Add(iconRow);
        Hint("Without an own icon the decoration menu shows the overworld sprite, like for the small dolls.");
        Label("Preview");
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.NearestNeighbor);
        panel.Children.Add(_preview);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "Show plan…", IsDefault = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.SemiBold };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 });
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _name.TextChanged += (_, _) =>
        {
            if (_constantTouched) return;
            _constant.Text = Regex.Replace(_name.Text.Trim().ToUpperInvariant().Replace("É", "E"), "[^A-Z0-9]+", "_").Trim('_');
            _constantTouched = false;
        };
        _constant.TextChanged += (_, _) => _constantTouched = _constant.IsKeyboardFocusWithin;
        _sprite.SelectionChanged += (_, _) => UpdatePreview();
        _useExisting.Checked += (_, _) => UpdatePreview();
        _useNew.Checked += (_, _) => UpdatePreview();
        _ownPalette.Checked += (_, _) => _paletteTag.IsEnabled = false;
        _ownPalette.Unchecked += (_, _) => _paletteTag.IsEnabled = true;
        _browseSprite.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "PNG|*.png", Title = "Doll / cushion sprite (one frame)" };
            if (dlg.ShowDialog(this) != true) return;
            _spritePng = dlg.FileName;
            _useNew.IsChecked = true;
            try
            {
                var png = Png.Load(dlg.FileName);
                _spriteFile.Text = $"{Path.GetFileName(dlg.FileName)} – {png.Width}×{png.Height}";
                var size = $"{png.Width}x{png.Height}";
                if (((string[])_frame.ItemsSource).Contains(size)) _frame.SelectedItem = size;
            }
            catch (PngFormatException ex) { _spriteFile.Text = ex.Message; }
            UpdatePreview();
        };
        _browseIcon.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "PNG|*.png", Title = "Decoration icon (24×24)" };
            if (dlg.ShowDialog(this) != true) return;
            _iconPng = dlg.FileName;
            _useIcon.IsChecked = true;
            _iconFile.Text = " " + Path.GetFileName(dlg.FileName);
            UpdatePreview();
        };
        ok.Click += (_, _) => Confirm();
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        try
        {
            RgbaImage? img = null;
            if (_useNew.IsChecked == true && _spritePng != null) img = GameGraphics.Render(Png.Load(_spritePng));
            else if (_sprite.SelectedItem is OverworldSprite s) img = SecretBasePreview.Sprite(_project, s.GfxId, _sprites, _palettes);
            if (_useIcon.IsChecked == true && _iconPng != null)
            {
                var icon = GameGraphics.Render(Png.Load(_iconPng));
                if (img == null) img = icon;
                else
                {
                    var both = new RgbaImage(img.Width + 8 + icon.Width, Math.Max(img.Height, icon.Height));
                    both.Blit(img, 0, 0, 0, 0, img.Width, img.Height);
                    both.Blit(icon, 0, 0, img.Width + 8, 0, icon.Width, icon.Height);
                    img = both;
                }
            }
            _preview.Source = img != null ? ImageReplaceFlow.Scaled(img, 3) : null;
        }
        catch (Exception ex) when (ex is IOException or PngFormatException)
        {
            _preview.Source = null;
        }
    }

    private void Confirm()
    {
        if (_kind.SelectedItem is not DecorationTemplate kind) return;
        if (!int.TryParse(_price.Text.Trim(), out var price)) { ProjectWriter.Warn(Title, "The price must be a number."); return; }
        SpriteImportRequest? newSprite = null;
        string? gfx = null;
        var constant = _constant.Text.Trim().ToUpperInvariant();
        if (constant.StartsWith("DECOR_", StringComparison.Ordinal)) constant = constant[6..];
        if (_useNew.IsChecked == true)
        {
            if (_spritePng == null) { ProjectWriter.Warn(Title, "Choose the PNG of the new sprite first."); return; }
            var parts = ((string)_frame.SelectedItem).Split('x');
            var spriteName = DecorationPlanner.ToPascal(constant);
            newSprite = new SpriteImportRequest(spriteName, _spritePng, int.Parse(parts[0]), int.Parse(parts[1]),
                _paletteTag.SelectedItem as string ?? "OBJ_EVENT_PAL_TAG_NPC_1", _nearest.IsChecked == true,
                _ownPalette.IsChecked == true ? spriteName : null);
        }
        else
        {
            if (_sprite.SelectedItem is not OverworldSprite s) { ProjectWriter.Warn(Title, "Choose an overworld sprite."); return; }
            gfx = s.GfxId;
        }
        if (_useIcon.IsChecked == true && _iconPng == null) { ProjectWriter.Warn(Title, "Choose the icon PNG or untick \"Own menu icon\"."); return; }
        Request = new NewDecorationRequest(constant, _name.Text.Trim(), kind.Category, kind.Shape, price, _description.Text.Replace("\r\n", "\n").TrimEnd('\n'),
            gfx, _useIcon.IsChecked == true ? _iconPng : null, newSprite);
        DialogResult = true;
    }
}
