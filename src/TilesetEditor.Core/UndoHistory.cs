namespace TilesetEditor.Core;

/// <summary>
/// Undo/redo for an editor that is not saved yet: after every change the editor reports its whole state (e.g. the JSON
/// of the files it edits); undo goes back to the state before, redo forward again. Comparing states means no change is
/// recorded twice and nothing has to be hooked in before a change.
/// </summary>
public sealed class UndoHistory<T>(Func<T, T, bool> equal, int limit = 200)
{
    private readonly LinkedList<T> _undo = new();
    private readonly Stack<T> _redo = new();
    private T? _current;
    private bool _has;
    private DateTime _last = DateTime.MinValue;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>A new starting point (opened or saved): no history.</summary>
    public void Reset(T state)
    {
        _last = DateTime.MinValue;
        _undo.Clear();
        _redo.Clear();
        _current = state;
        _has = true;
    }

    /// <summary>
    /// After a change: remembers the state before it. Changes within <paramref name="mergeMs"/> of the previous one (typing,
    /// dragging) become one step.
    /// </summary>
    public void Record(T state, int mergeMs = 0)
    {
        if (!_has) { Reset(state); return; }
        if (equal(_current!, state)) return;
        var now = DateTime.UtcNow;
        bool merge = mergeMs > 0 && (now - _last).TotalMilliseconds < mergeMs && _undo.Count > 0;
        _last = now;
        if (merge) { _current = state; _redo.Clear(); return; }
        _undo.AddLast(_current!);
        if (_undo.Count > limit) _undo.RemoveFirst();
        _redo.Clear();
        _current = state;
    }

    public T? Undo()
    {
        if (!CanUndo) return default;
        _redo.Push(_current!);
        _current = _undo.Last!.Value;
        _undo.RemoveLast();
        return _current;
    }

    public T? Redo()
    {
        if (!CanRedo) return default;
        _undo.AddLast(_current!);
        _current = _redo.Pop();
        return _current;
    }
}
