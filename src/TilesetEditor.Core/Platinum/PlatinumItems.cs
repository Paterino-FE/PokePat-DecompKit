using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>What a new item gets; everything else (use function, effect, icon) is copied from <see cref="Template"/>.</summary>
public sealed record NewItem(string Constant, string Template, string Name, string Plural, string Description, int Price, string? Pocket);

/// <summary>
/// Adds items to pokeplatinum: the constant goes into generated/items.txt before MAX_ITEMS (so no existing item changes its
/// number) and res/items/data/&lt;name&gt;.json is a copy of a similar item with the new name, price, pocket and description.
/// The build makes the item tables, names and descriptions from it. The icon is the template's.
/// </summary>
public static partial class PlatinumItems
{
    public const string ConstantsFile = "generated/items.txt";
    public const string DataDir = "res/items/data";

    [GeneratedRegex(@"^ITEM_[A-Z0-9_]+$")]
    private static partial Regex ConstantPattern();

    public static string FileOf(string constant) => $"{DataDir}/{constant[5..].ToLowerInvariant()}.json";

    /// <summary>"Super Candy" → "Super Candies", "Potion" → "Potions".</summary>
    public static string Plural(string name) =>
        name.EndsWith('y') && name.Length > 1 && !"aeiou".Contains(name[^2]) ? name[..^1] + "ies"
        : name.EndsWith('s') || name.EndsWith('x') || name.EndsWith("ch") || name.EndsWith("sh") ? name + "es"
        : name + "s";

    public static (List<PlatinumFileChange> Files, List<string> Notes) PlanAdd(string root, NewItem item)
    {
        if (!ConstantPattern().IsMatch(item.Constant)) throw new InvalidDataException("The constant must look like ITEM_NAME (capital letters, digits, _).");
        if (item.Constant.StartsWith("ITEM_UNUSED_", StringComparison.Ordinal)) throw new InvalidDataException("ITEM_UNUSED_… names are reserved for empty slots.");
        if (item.Name.Trim().Length == 0) throw new InvalidDataException("The item needs a name.");
        if (item.Price is < 0 or > 65535) throw new InvalidDataException("The price must be 0–65535.");
        var constantsPath = ProjectPaths.Combine(root, ConstantsFile);
        var lines = File.ReadAllLines(constantsPath).ToList();
        var names = lines.Select(l => l.Split('=')[0].Trim()).ToList();
        if (names.Contains(item.Constant)) throw new InvalidDataException($"{item.Constant} already exists.");
        int max = names.IndexOf("MAX_ITEMS");
        if (max < 0) throw new InvalidDataException($"{ConstantsFile} has no MAX_ITEMS line.");
        var templatePath = ProjectPaths.Combine(root, FileOf(item.Template));
        if (!File.Exists(templatePath)) throw new InvalidDataException($"{item.Template} has no data file to copy.");
        if (File.Exists(ProjectPaths.Combine(root, FileOf(item.Constant)))) throw new InvalidDataException($"{FileOf(item.Constant)} already exists.");

        var template = PlatinumJsonFile.Load(root, FileOf(item.Template), 2);
        var obj = (JsonObject)JsonText.Parse(template.Root.ToJsonString())!;
        obj["name"] = item.Name.Trim();
        obj["plural"] = item.Plural.Trim().Length > 0 ? item.Plural.Trim() : Plural(item.Name.Trim());
        if (obj.ContainsKey("article")) obj["article"] = "aeiouAEIOU".Contains(item.Name.Trim()[0]) ? "an" : "a";
        obj["description"] = new JsonArray(JsonText.SplitLines(JsonText.FromEscapes(item.Description)).Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
        obj["price"] = item.Price;
        if (item.Pocket != null && obj.ContainsKey("fieldPocket")) obj["fieldPocket"] = item.Pocket;
        if (obj.ContainsKey("gbaID")) obj["gbaID"] = "GBA_ITEM_NONE"; // no Pal Park / GBA counterpart
        var json = JsonText.Dump(obj, 2) + template.OriginalText[template.OriginalText.TrimEnd().Length..];

        lines.Insert(max, item.Constant);
        var utf8 = new UTF8Encoding(false);
        var constants = string.Join("\n", lines) + "\n";
        var notes = new List<string>
        {
            $"{item.Constant} gets number {max} (before MAX_ITEMS); MAX_ITEMS becomes {max + 1}.",
            $"Effect, use function, battle use and icon are copied from {item.Template} – change the JSON file for more.",
            "Give it to the player in a script (e.g. GiveItem / a Poké Mart list) – PokePat does not place it anywhere.",
        };
        return (
        [
            new(ConstantsFile, utf8.GetBytes(constants), $"{item.Constant} before MAX_ITEMS"),
            new(FileOf(item.Constant), utf8.GetBytes(json), $"{item.Name}: copy of {item.Template}"),
        ], notes);
    }
}
