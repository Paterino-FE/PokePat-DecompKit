using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace TilesetEditor.App.Infrastructure;

public enum AppTheme { System, Light, Dark }

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
        foreach (var (key, (light, dark)) in Palette) res[key] = Brush(IsDark ? dark : light);

        // Colors of the standard controls (text boxes, combo box lists, menus, selection) follow the theme in dark mode;
        // in light mode Windows' own colors stay.
        var system = new (object Key, string Color)[]
        {
            (SystemColors.WindowBrushKey, "#272223"), (SystemColors.ControlBrushKey, "#2E2829"), (SystemColors.ControlTextBrushKey, "#EEE6E4"),
            (SystemColors.WindowTextBrushKey, "#EEE6E4"), (SystemColors.GrayTextBrushKey, "#8C7F80"), (SystemColors.HighlightBrushKey, "#5C2E33"),
            (SystemColors.HighlightTextBrushKey, "#FFFFFF"), (SystemColors.InactiveSelectionHighlightBrushKey, "#46292D"),
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
