using System.Globalization;
using System.Windows.Data;

namespace TilesetEditor.App.Shell;

/// <summary>Small symbol per module for the navigation (by module title).</summary>
public sealed class ModuleIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "Project" => "🏠",
        "Tilesets" => "🧱",
        "Maps" => "🗺",
        "Secret Bases" => "🏕",
        "Dialogs & Events" => "💬",
        "Flags & Variables" => "🚩",
        "Trainers" => "⚔",
        "Game data" => "📖",
        "Sprites" => "🧍",
        "Graphics" => "🖼",
        "Features" => "⚙",
        "Mods" => "🧩",
        _ => "✦",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
