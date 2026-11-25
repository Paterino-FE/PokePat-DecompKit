using System.Windows;

namespace TilesetEditor.App.Modules.Events;

/// <summary>Collects the values for a new person (object event) on the selected map.</summary>
public partial class NewPersonWindow : Window
{
    private readonly int _width, _height;

    public NewPersonWindow(string map, int width, int height, IReadOnlyList<string> graphics, IReadOnlyList<string> movementTypes, string? eventLabel,
        TilesetEditor.Core.Events.EventTrigger kind = TilesetEditor.Core.Events.EventTrigger.Person, IReadOnlyList<string>? vars = null)
    {
        InitializeComponent();
        _width = width;
        _height = height;
        Kind = kind;
        string what = kind switch { TilesetEditor.Core.Events.EventTrigger.Sign => "New sign", TilesetEditor.Core.Events.EventTrigger.Trigger => "New trigger tile", _ => "New person" };
        Header.Text = $"{what} on {map}";
        bool person = kind == TilesetEditor.Core.Events.EventTrigger.Person;
        foreach (UIElement e in new UIElement[] { GraphicsLabel, Graphics, MovementLabel, Movement })
            e.Visibility = person ? Visibility.Visible : Visibility.Collapsed;
        bool trigger = kind == TilesetEditor.Core.Events.EventTrigger.Trigger;
        VarLabel.Visibility = VarPanel.Visibility = trigger ? Visibility.Visible : Visibility.Collapsed;
        VarName.ItemsSource = vars ?? [];
        VarName.Text = "VAR_TEMP_1";
        if (kind == TilesetEditor.Core.Events.EventTrigger.Sign) Elevation.Text = "0";
        if (trigger) Info.Text = "A trigger tile fires when the player steps on it and the variable has the given value (VAR_TEMP_1 = 0 is true on every map at first). To stop it from firing again and again, change the variable in the event.";
        Graphics.ItemsSource = graphics;
        Graphics.Text = graphics.Contains("OBJ_EVENT_GFX_BOY_1") ? "OBJ_EVENT_GFX_BOY_1" : graphics.FirstOrDefault() ?? "";
        Movement.ItemsSource = movementTypes;
        Movement.Text = "MOVEMENT_TYPE_FACE_DOWN";
        X.Text = Math.Max(0, width / 2).ToString();
        Y.Text = Math.Max(0, height / 2).ToString();
        SizeInfo.Text = width > 0 ? $"(Map: {width}×{height})" : "";
        AssignEvent.Content = eventLabel != null ? $"Assign event \"{eventLabel}\"" : "no event selected";
        AssignEvent.IsChecked = eventLabel != null;
        AssignEvent.IsEnabled = eventLabel != null;
    }

    public string GraphicsId => Graphics.Text.Trim();
    public string MovementType => Movement.Text.Trim();
    public int PosX { get; private set; }
    public int PosY { get; private set; }
    public int ElevationValue { get; private set; }
    public bool AssignSelectedEvent => AssignEvent.IsChecked == true;
    public TilesetEditor.Core.Events.EventTrigger Kind { get; }
    public string Var => VarName.Text.Trim();
    public string VarValueText => VarValue.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(X.Text, out var x) || !int.TryParse(Y.Text, out var y) || x < 0 || y < 0 || (_width > 0 && (x >= _width || y >= _height)))
        {
            Error.Text = _width > 0 ? $"Position must be inside the map (0–{_width - 1} / 0–{_height - 1})." : "Please enter valid numbers for X and Y.";
            return;
        }
        if (!int.TryParse(Elevation.Text, out var el) || el is < 0 or > 15) { Error.Text = "Elevation must be 0–15."; return; }
        if (Kind == TilesetEditor.Core.Events.EventTrigger.Person && !GraphicsId.StartsWith("OBJ_EVENT_GFX_", StringComparison.Ordinal))
        {
            Error.Text = "Please pick a sprite (OBJ_EVENT_GFX_…).";
            return;
        }
        if (Kind == TilesetEditor.Core.Events.EventTrigger.Trigger && (!Var.StartsWith("VAR_", StringComparison.Ordinal) || !int.TryParse(VarValueText, out _)))
        {
            Error.Text = "Please enter a variable (VAR_…) and a numeric value.";
            return;
        }
        PosX = x;
        PosY = y;
        ElevationValue = el;
        DialogResult = true;
    }
}
