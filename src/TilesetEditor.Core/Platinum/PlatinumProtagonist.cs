using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>A picture of the player character (Lucas / Dawn) and where it is used.</summary>
public sealed record ProtagonistPicture(bool Female, string Label, string PngRel, string Hint, int FrameSize = 0);

/// <summary>
/// All pictures of the player characters in one place: battle front and back (throw animation), the encounter mugshot,
/// the scan form of the back sprite, and every overworld form (walking, bike, surf, fishing, Pokétch, saving …).
/// </summary>
public static class PlatinumProtagonist
{
    public static List<ProtagonistPicture> List(string root)
    {
        var list = new List<ProtagonistPicture>();
        foreach (var female in new[] { false, true })
        {
            var cls = $"{PlatinumProject.TrainerClassesDir}/{(female ? "player_female" : "player_male")}";
            void Add(string label, string rel, string hint, int frame = 0)
            {
                if (File.Exists(ProjectPaths.Combine(root, rel))) list.Add(new ProtagonistPicture(female, label, rel, hint, frame));
            }
            Add("Battle – front", $"{cls}/front.png", "80×80: the trainer before a battle (Union Room, link battles, trainer card).");
            Add("Battle – back", $"{cls}/back.png", "The back sprite with its throw animation (frames of 80 px below each other).", 80);
            Add("Battle – back (scan)", $"{cls}/back_scan.png", "Second form of the back sprite used while the battle starts.");
            Add("Encounter mugshot", $"{cls}/mugshot.png", "The picture in the battle intro of special battles (e.g. rival, gym leaders).");
            var ow = $"{PlatinumOverworld.Folder}/player";
            var dir = ProjectPaths.Combine(root, ow);
            if (Directory.Exists(dir))
            {
                var prefix = female ? "player_f" : "player_m";
                foreach (var f in Directory.GetFiles(dir, prefix + "*.png").Select(Path.GetFileName).OrderBy(f => f!.Length).ThenBy(f => f, StringComparer.Ordinal))
                {
                    var form = Path.GetFileNameWithoutExtension(f!)[prefix.Length..].Trim('_');
                    Add("Overworld – " + (form.Length == 0 ? "walking" : PlatinumNames.Pretty(form.ToUpperInvariant(), "")), $"{ow}/{f}",
                        "Frames of 32×32 below each other (walking, running, turning …).", 32);
                }
            }
        }
        return list;
    }
}
