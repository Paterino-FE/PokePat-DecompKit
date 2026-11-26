using System.Windows;
using System.Windows.Threading;

namespace TilesetEditor.App.Infrastructure;

/// <summary>
/// Reloading a module when the project was (re)opened: right away while the module is on screen; otherwise it notes that it
/// is out of date and loads when it is shown. Modules with slow data (game data, trainers, Pokémon) also <em>prefetch</em>:
/// shortly after the app has become idle they load in the background, one after another, so opening them is instant.
/// </summary>
public sealed class DeferredLoad
{
    private readonly Func<Task?> _load;
    private readonly bool _prefetch;
    private FrameworkElement? _view;
    private bool _stale;
    private Task? _running;

    public DeferredLoad(Action load) : this(() => { load(); return null; }, false) { }

    /// <param name="load">Loads the module; returns the task of an asynchronous load (or null).</param>
    /// <param name="prefetch">Load in the background after startup even while the module is hidden.</param>
    public DeferredLoad(Func<Task?> load, bool prefetch)
    {
        _load = load;
        _prefetch = prefetch;
    }

    private string Name => _load.Target?.GetType().DeclaringType?.Name ?? _load.Target?.GetType().Name ?? "module";

    public void Attach(FrameworkElement view)
    {
        _view = view;
        view.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && _stale) Perf.Time($"load on show: {Name}", Run);
        };
    }

    public void Request()
    {
        if (_view == null || _view.IsVisible) { Run(); return; }
        _stale = true;
        if (_prefetch) Prefetcher.Enqueue(this);
    }

    private void Run()
    {
        _stale = false;
        _running = _load();
    }

    /// <summary>Background loading of hidden modules: after the app is idle, one module at a time.</summary>
    private static class Prefetcher
    {
        private static readonly Queue<DeferredLoad> Queue = new();
        private static bool _active;

        public static void Enqueue(DeferredLoad d)
        {
            if (!Queue.Contains(d)) Queue.Enqueue(d);
            if (_active) return;
            _active = true;
            _ = RunAsync();
        }

        private static async Task RunAsync()
        {
            try
            {
                // Let the window and the visible module come up first.
                await Task.Delay(1500);
                while (Queue.Count > 0)
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var d = Queue.Dequeue();
                    if (!d._stale) continue; // shown (and loaded) in the meantime
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    d.Run();
                    if (d._running is { } t) { try { await t; } catch (Exception) { /* the module shows its own error */ } }
                    Perf.Log($"prefetch: {d.Name}", sw.ElapsedMilliseconds);
                }
            }
            finally { _active = false; }
        }
    }
}
