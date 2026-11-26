using System.Text;
using TilesetEditor.Export;

namespace TilesetEditor.Tests;

public class BackupHistoryTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static string Read(string root, string rel) => File.ReadAllText(Path.Combine(root, rel));

    [Fact]
    public void ChangesCanBeUndoneOneByOneOrBackToBefore()
    {
        var root = TestPaths.NewTempDir("bk-project");
        var backups = TestPaths.NewTempDir("bk-backups");
        File.WriteAllText(Path.Combine(root, "b.txt"), "b0");
        var applier = new ExportApplier(backupRoot: backups);

        // 1: creates a.txt, changes b.txt
        Assert.True(applier.Apply(ExportPlan.ForFiles(root, "First", "", [
            new PlannedFile("a.txt", PlannedAction.Create, B("a1"), "new"),
            new PlannedFile("b.txt", PlannedAction.Modify, B("b1"), "changed")])).Success);
        Thread.Sleep(1100);   // backups are ordered by their time
        // 2: changes a.txt
        Assert.True(applier.Apply(ExportPlan.ForFiles(root, "Second", "", [new PlannedFile("a.txt", PlannedAction.Modify, B("a2"), "changed")])).Success);

        var all = BackupHistory.List(backups);
        Assert.Equal(["Second", "First"], all.Select(e => e.Title).ToArray());
        Assert.True(all.All(e => e.HasManifest && BackupHistory.SameProject(e, root)));
        Assert.Equal(PlannedAction.Create, all[1].Files.Single(f => f.Path == "a.txt").Action);

        // Undo only the second change: a.txt back to a1.
        var (undo2, c2) = BackupHistory.PlanUndo(all[0], root);
        Assert.Empty(c2);
        Assert.Equal("a1", Encoding.UTF8.GetString(undo2.Files.Single().Content));

        // Back to before the first: a.txt removed, b.txt back to b0 – after a hand edit of b.txt, which is reported.
        File.WriteAllText(Path.Combine(root, "b.txt"), "b-by-hand");
        var (back, conflicts, undone) = BackupHistory.PlanBackTo(all[1], all, root);
        Assert.Equal(2, undone.Count);
        Assert.Equal(["b.txt"], conflicts.Select(c => c.Path).ToArray());
        Assert.True(applier.Apply(back).Success);
        Assert.False(File.Exists(Path.Combine(root, "a.txt")));
        Assert.Equal("b0", Read(root, "b.txt"));

        // The undo is a change itself and can be undone again.
        var again = BackupHistory.List(backups).First();
        Assert.StartsWith("Back to before", again.Title);
        Assert.True(applier.Apply(BackupHistory.PlanUndo(again, root).Plan).Success);
        Assert.Equal("a2", Read(root, "a.txt"));
        Assert.Equal("b-by-hand", Read(root, "b.txt"));
    }
}
