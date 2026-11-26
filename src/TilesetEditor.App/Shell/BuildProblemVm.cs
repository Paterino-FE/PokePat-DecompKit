using TilesetEditor.Core.Project;

namespace TilesetEditor.App.Shell;

/// <summary>A row of the build problem list.</summary>
public sealed class BuildProblemVm(BuildProblem problem, bool isFirst, string? showIn)
{
    public BuildProblem Problem { get; } = problem;
    public string Icon => Problem.IsError ? "⛔" : "⚠";
    public string Where => Problem.File == null ? "(no file named)" : Problem.Where;
    public string Message => Problem.Message;
    /// <summary>The first error is usually the cause; later ones often only follow from it.</summary>
    public bool IsFirst { get; } = isFirst;
    public string? ShowIn { get; } = showIn;
    public bool CanShow => ShowIn != null;
    public bool HasFile => Problem.File != null;
    public string ShowTip => ShowIn != null ? "Show in PokéPat – " + ShowIn : "Open the file at this line";
}
