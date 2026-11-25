namespace TilesetEditor.Core.Artwork;

public enum IssueSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>Where an issue is located in the imported artwork. Coordinates are in source pixels.</summary>
public sealed record IssueLocation(int? Metatile, Layer? Layer, int? TileX, int? TileY, int? PixelX = null, int? PixelY = null)
{
    public string Describe(Artwork? art)
    {
        var parts = new List<string>();
        if (Metatile != null)
        {
            if (art != null && art.Columns > 0)
                parts.Add($"Metatile {Metatile} (column {Metatile % art.Columns}, row {Metatile / art.Columns})");
            else parts.Add($"Metatile {Metatile}");
        }
        if (TileX != null && TileY != null) parts.Add($"Tile {TileX},{TileY}");
        if (Layer != null) parts.Add($"Layer {LayerNames.German(Layer.Value)}");
        if (PixelX != null && PixelY != null) parts.Add($"Pixel {PixelX},{PixelY}");
        return string.Join(", ", parts);
    }
}

public sealed record Issue(IssueSeverity Severity, string Code, string Message, IssueLocation? Location = null, string Source = "Pre-check")
{
    public string SeverityText => Severity switch
    {
        IssueSeverity.Error => "Error",
        IssueSeverity.Warning => "Warning",
        _ => "Info",
    };
}
