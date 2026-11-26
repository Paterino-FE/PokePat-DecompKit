using TilesetEditor.Core;

namespace TilesetEditor.Tests;

public class UndoHistoryTests
{
    [Fact]
    public void UndoRedoAndMergingQuickChanges()
    {
        var h = new UndoHistory<string>((a, b) => a == b);
        h.Reset("a");
        h.Record("b");
        h.Record("b");                       // no change: no step
        h.Record("c");
        Assert.Equal("b", h.Undo());
        Assert.Equal("a", h.Undo());
        Assert.False(h.CanUndo);
        Assert.Equal("b", h.Redo());
        h.Record("x");                       // a new change drops the redo steps
        Assert.False(h.CanRedo);
        Assert.Equal("b", h.Undo());

        h.Reset("t");
        h.Record("t1", mergeMs: 5000);
        h.Record("t12", mergeMs: 5000);      // typing: one step
        Assert.Equal("t", h.Undo());
        Assert.False(h.CanUndo);
    }
}
