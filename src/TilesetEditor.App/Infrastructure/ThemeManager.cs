using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace TilesetEditor.App.Infrastructure;

public enum AppTheme { System, Light, Dark }

/// <summary>The look of the game being edited: Pokémon Center red (no project), Emerald green, Platinum purple and silver.</summary>
public enum GameStyle { Center, Emerald, Platinum }

/// <summary>
/// Light (Pokémon Center: white walls, red roof) and dark theme. Every color of the UI is a brush resource referenced with
/// DynamicResource, so switching replaces the brushes and everything updates at once – including the system colors the
/// standard WPF controls use and the windows' title bars.
/// </summary>
public static class ThemeManager
{
    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["AccentBrush"] = ("#E3343A", "#E8545A"),
        ["AccentDarkBrush"] = ("#B8212B", "#FF8C90"),
        ["PanelBrush"] = ("#FFF8F2", "#1C1819"),
        ["SurfaceBrush"] = ("#FFFFFF", "#272223"),
        ["BorderBrush"] = ("#EBD9D4", "#433A3B"),
        ["PinkBrush"] = ("#FDE8E8", "#3A2729"),
        ["PinkStrongBrush"] = ("#F9C9CB", "#5C2E33"),
        ["HoverBrush"] = ("#FFF2F2", "#33262A"),
        ["InkBrush"] = ("#3B2F2F", "#EEE6E4"),
        ["MutedBrush"] = ("#7D6B69", "#A8999A"),
        ["AltRowBrush"] = ("#FFFAF7", "#2B2526"),
        ["ReadOnlyBrush"] = ("#FCF6F3", "#221E1F"),
        ["ChangedRowBrush"] = ("#FEF3C7", "#4A3E1B"),
        ["ErrorRowBrush"] = ("#FEE2E2", "#4A2326"),
        ["ErrorCellBrush"] = ("#FCA5A5", "#7A2D30"),
        ["WarnCellBrush"] = ("#FED7AA", "#6E4A1E"),
        ["ErrorBrush"] = ("#B91C1C", "#FF8080"),
        ["SuccessBrush"] = ("#2E7D4F", "#6FD39A"),
        ["WarnBannerBrush"] = ("#FFF4D6", "#3B3220"),
        ["WarnLineBrush"] = ("#F2B544", "#8A6D2B"),
        ["WarnTextBrush"] = ("#B45309", "#F2B544"),
    };

    /// <summary>Per game: the colours that carry the game's character (accent, roof, tinted surfaces, selection).</summary>
    private static readonly Dictionary<GameStyle, Dictionary<string, (string Light, string Dark)>> Styles = new()
    {
        [GameStyle.Center] = new(),
        [GameStyle.Emerald] = new()
        {
            ["AccentBrush"] = ("#1E9B5A", "#35C47A"),
            ["AccentDarkBrush"] = ("#13703F", "#86E3AF"),
            ["PanelBrush"] = ("#F3FAF5", "#151B17"),
            ["SurfaceBrush"] = ("#FFFFFF", "#1E2621"),
            ["BorderBrush"] = ("#CFE5D7", "#33443A"),
            ["PinkBrush"] = ("#E2F4E9", "#203229"),
            ["PinkStrongBrush"] = ("#BEE5CC", "#2B5240"),
            ["HoverBrush"] = ("#EDF8F1", "#22302A"),
            ["InkBrush"] = ("#22332A", "#E4EEE8"),
            ["MutedBrush"] = ("#5F7568", "#98AC9F"),
            ["AltRowBrush"] = ("#F7FCF9", "#212924"),
            ["ReadOnlyBrush"] = ("#F1F8F3", "#1A201C"),
            ["#Roof"] = ("#2FB46B|#1E9B5A|#13703F", "#24985A|#17784A|#0E5233"),
            ["#Selection"] = ("", "#2B5240|#223D31"),
        },
        [GameStyle.Platinum] = new()
        {
            ["AccentBrush"] = ("#6E4FB0", "#9C80DB"),
            ["AccentDarkBrush"] = ("#4F3487", "#CDBBF5"),
            ["PanelBrush"] = ("#F5F4F8", "#18171C"),
            ["SurfaceBrush"] = ("#FFFFFF", "#222029"),
            ["BorderBrush"] = ("#DAD5E4", "#3B3647"),
            ["PinkBrush"] = ("#ECE7F6", "#2B2638"),
            ["PinkStrongBrush"] = ("#D6CBEE", "#44375F"),
            ["HoverBrush"] = ("#F2EEF9", "#2A2533"),
            ["InkBrush"] = ("#2C2738", "#ECE9F2"),
            ["MutedBrush"] = ("#6C6680", "#A49DB5"),
            ["AltRowBrush"] = ("#FAF9FC", "#25222C"),
            ["ReadOnlyBrush"] = ("#F4F2F8", "#1D1B22"),
            ["#Roof"] = ("#8A6BC8|#6E4FB0|#4A3A6E", "#6F55A8|#563F8C|#2F2549"),
            ["#Selection"] = ("", "#44375F|#352B4A"),
        },
    };

    public static GameStyle Style { get; private set; } = GameStyle.Center;

    /// <summary>Switches to the colours of another game (keeps light/dark).</summary>
    public static void ApplyStyle(GameStyle style)
    {
        if (style == Style) return;
        Style = style;
        Apply(Current);
    }

    private static (string Light, string Dark) Pick(string key) =>
        Styles[Style].TryGetValue(key, out var v) ? v : Palette.TryGetValue(key, out var b) ? b : ("", "");

    private static LinearGradientBrush Roof(string stops)
    {
        var c = stops.Split('|').Select(x => (Color)ColorConverter.ConvertFromString(x)).ToArray();
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        b.GradientStops.Add(new GradientStop(c[0], 0));
        b.GradientStops.Add(new GradientStop(c[1], 0.55));
        b.GradientStops.Add(new GradientStop(c[2], 1));
        b.Freeze();
        return b;
    }

    public static AppTheme Current { get; private set; } = AppTheme.Light;
    public static bool IsDark { get; private set; }
    public static event EventHandler? Changed;

    public static AppTheme Parse(string? s) => Enum.TryParse<AppTheme>(s, true, out var t) ? t : AppTheme.System;

    /// <summary>Call once at startup: applies the theme and gives every window the right title bar.</summary>
    public static void Initialize(AppTheme theme)
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => { if (s is Window w) DarkTitleBar(w, IsDark); }));
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Current == AppTheme.System)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
        };
        Apply(theme);
    }

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        IsDark = theme == AppTheme.Dark || (theme == AppTheme.System && SystemPrefersDark());
        var res = Application.Current.Resources;
        foreach (var key in Palette.Keys) { var (light, dark) = Pick(key); res[key] = Brush(IsDark ? dark : light); }
        var roof = Styles[Style].TryGetValue("#Roof", out var r) ? (IsDark ? r.Dark : r.Light) : IsDark ? "#D8474D|#B8343B|#7E1D24" : "#EF4A4F|#E3343A|#B8212B";
        res["RoofBrush"] = Roof(roof);
        var selection = Styles[Style].TryGetValue("#Selection", out var sel) && sel.Dark.Length > 0 ? sel.Dark.Split('|') : ["#5C2E33", "#46292D"];

        // Colors of the standard controls (text boxes, combo box lists, menus, selection) follow the theme in dark mode;
        // in light mode Windows' own colors stay.
        var system = new (object Key, string Color)[]
        {
            (SystemColors.WindowBrushKey, "#272223"), (SystemColors.ControlBrushKey, "#2E2829"), (SystemColors.ControlTextBrushKey, "#EEE6E4"),
            (SystemColors.WindowTextBrushKey, "#EEE6E4"), (SystemColors.GrayTextBrushKey, "#8C7F80"), (SystemColors.HighlightBrushKey, selection[0]),
            (SystemColors.HighlightTextBrushKey, "#FFFFFF"), (SystemColors.InactiveSelectionHighlightBrushKey, selection[1]),
            (SystemColors.InactiveSelectionHighlightTextBrushKey, "#EEE6E4"), (SystemColors.ControlLightBrushKey, "#3A3233"),
            (SystemColors.ControlLightLightBrushKey, "#433A3B"), (SystemColors.ControlDarkBrushKey, "#5A4F50"), (SystemColors.ControlDarkDarkBrushKey, "#120F10"),
            (SystemColors.MenuBrushKey, "#272223"), (SystemColors.MenuTextBrushKey, "#EEE6E4"), (SystemColors.InfoBrushKey, "#272223"), (SystemColors.InfoTextBrushKey, "#EEE6E4"),
        };
        foreach (var (key, color) in system)
        {
            if (IsDark) res[key] = Brush(color);
            else res.Remove(key);
        }
        foreach (Window w in Application.Current.Windows) DarkTitleBar(w, IsDark);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>Windows 10/11: "AppsUseLightTheme" = 0 means dark apps.</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Dark title bar (DWMWA_USE_IMMERSIVE_DARK_MODE) – ignored on Windows versions without it.</summary>
    private static void DarkTitleBar(Window w, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }
}
