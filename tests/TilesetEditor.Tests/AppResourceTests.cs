using System.Text.RegularExpressions;

namespace TilesetEditor.Tests;

/// <summary>
/// The WPF app cannot run in the tests, but a resource name used in code that no XAML file defines crashes the app at
/// runtime ("'X' resource not found"). This checks every FindResource("…") / Res("…") of the App against the XAML keys.
/// </summary>
public class AppResourceTests
{
    [Fact]
    public void EveryResourceUsedInCodeIsDefined()
    {
        string? app = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TilesetEditor.sln"))) { app = Path.Combine(d.FullName, "src", "TilesetEditor.App"); break; }
        Assert.NotNull(app);
        var keys = Directory.EnumerateFiles(app!, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value)).ToHashSet();
        var missing = new List<string>();
        foreach (var f in Directory.EnumerateFiles(app!, "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"(?:FindResource|TryFindResource|Res|Resources\[)\(?""([A-Za-z]+)""\)?"))
                if (!keys.Contains(m.Groups[1].Value)) missing.Add($"{Path.GetFileName(f)}: {m.Groups[1].Value}");
        }
        Assert.Empty(missing);
    }
}
