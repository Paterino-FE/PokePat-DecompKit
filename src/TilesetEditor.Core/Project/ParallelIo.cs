using System.Runtime.ExceptionServices;

namespace TilesetEditor.Core.Project;

/// <summary>
/// Parallel file access for project folders on slow shares (\\wsl.localhost): each file read is a round trip, so many
/// files are read at once. Errors come out as the original exception (IOException, FormatException …), not wrapped in
/// an AggregateException, so callers keep handling them as before.
/// </summary>
public static class ParallelIo
{
    public const int Degree = 16;

    public static void For(int count, Action<int> body)
    {
        try { Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Degree }, body); }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }
    }
}
