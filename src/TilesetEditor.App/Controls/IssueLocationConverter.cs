using System.Globalization;
using System.Windows.Data;
using TilesetEditor.Core.Artwork;

namespace TilesetEditor.App.Controls;

public sealed class IssueLocationConverter : IValueConverter
{
    public static readonly IssueLocationConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IssueLocation l ? l.Describe(null) : "–";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
