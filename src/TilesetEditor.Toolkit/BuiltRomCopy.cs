namespace TilesetEditor.Toolkit;

/// <summary>
/// After a successful build the ROM is copied next to PokéPat (folder "ROM"), named after the project folder, e.g.
/// ROM\pokeplatinum.nds – replaced by every new build. A copy on the Windows drive opens faster in an emulator than the
/// file inside WSL, and it is easy to find.
/// </summary>
public static class BuiltRomCopy
{
    public const string FolderName = "ROM";

    /// <summary>Where the copy of a project's ROM goes.</summary>
    public static string TargetPath(string appDir, string projectRoot, string romPath)
    {
        var name = Path.GetFileName(projectRoot.TrimEnd('/', '\\'));
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(romPath);
        return Path.Combine(appDir, FolderName, name + Path.GetExtension(romPath));
    }

    /// <summary>Copies the ROM (replacing the last one); returns the copy's path, or null and the reason.</summary>
    public static (string? Path, string? Error) Copy(string romPath, string projectRoot, string appDir)
    {
        var target = TargetPath(appDir, projectRoot, romPath);
        var temp = target + ".new";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(romPath, temp, overwrite: true);
            File.Move(temp, target, overwrite: true);
            return (target, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return (null, ex is IOException && File.Exists(target)
                ? $"The ROM in {Path.GetDirectoryName(target)} could not be replaced – is it still open in the emulator?"
                : $"The ROM could not be copied to {Path.GetDirectoryName(target)}: {ex.Message}");
        }
    }
}
