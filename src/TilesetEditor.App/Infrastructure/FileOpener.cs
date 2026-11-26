using System.Diagnostics;
using System.IO;

namespace TilesetEditor.App.Infrastructure;

/// <summary>Opens a project file at a line: in VS Code when it is installed (code -g file:line), else in the default app.</summary>
public static class FileOpener
{
    /// <summary>Returns a status line, or null.</summary>
    public static string? OpenAt(string file, int? line)
    {
        if (FindVsCode() is { } code)
        {
            try
            {
                var arg = line is { } l ? $"{file}:{l}" : file;
                Process.Start(new ProcessStartInfo(code) { ArgumentList = { "-g", arg }, UseShellExecute = false, CreateNoWindow = true });
                return $"Opened {Path.GetFileName(file)}{(line != null ? $" at line {line}" : "")} in VS Code.";
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception)
        {
            // No app for .s/.inc/.json: Notepad always works.
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { file }, UseShellExecute = false });
        }
        return line != null ? $"Opened {Path.GetFileName(file)} – the problem is in line {line}." : null;
    }

    private static string? FindVsCode()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var c in new[] { Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"), Path.Combine(programs, "Microsoft VS Code", "Code.exe") })
            if (File.Exists(c)) return c;
        return null;
    }
}
