using System.Diagnostics;
using System.IO;

namespace TilesetEditor.App.Infrastructure;

/// <summary>
/// Timings of slow steps (opening a project, each module's reload, writing files) in %LOCALAPPDATA%\TilesetEditor\perf.log,
/// so a slow start or a hang can be traced to its cause. Only steps above a few milliseconds are written.
/// </summary>
public static class Perf
{
    public static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TilesetEditor", "perf.log");
    private static readonly Stopwatch Since = Stopwatch.StartNew();
    private static readonly object Gate = new();

    public static T Time<T>(string what, Func<T> f)
    {
        var sw = Stopwatch.StartNew();
        try { return f(); }
        finally { Log(what, sw.ElapsedMilliseconds); }
    }

    public static void Time(string what, Action f)
    {
        var sw = Stopwatch.StartNew();
        try { f(); }
        finally { Log(what, sw.ElapsedMilliseconds); }
    }

    /// <summary>Raises each handler of an event on its own and logs how long it took.</summary>
    public static void Raise(EventHandler? handler, object sender, string eventName)
    {
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList())
        {
            var target = d.Target?.GetType();
            // Handlers are usually lambdas: name the class that declared them.
            var owner = target?.DeclaringType?.Name ?? target?.Name ?? d.Method.DeclaringType?.Name ?? "?";
            Time($"{eventName} → {owner}", () => ((EventHandler)d)(sender, EventArgs.Empty));
        }
    }

    public static void Log(string what, long ms)
    {
        if (ms < 15) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} +{Since.ElapsedMilliseconds,7} ms  {ms,6} ms  {what}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
    }
}
