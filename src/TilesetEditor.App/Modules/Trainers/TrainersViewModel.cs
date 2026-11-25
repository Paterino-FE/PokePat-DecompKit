using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Project;
using TilesetEditor.Core.Trainers;
using TilesetEditor.Export;

namespace TilesetEditor.App.Modules.Trainers;

public sealed class TrainerListItem(TrainerData data)
{
    public TrainerData Data { get; } = data;
    public string Id => Data.Id;
    public string Title => $"{Data.Name}  ·  {TrainerData.Pretty(Data.TrainerClass, "TRAINER_CLASS_")}";
    public string Party => Data.PartySummary;
    public override string ToString() => Data.Id;
}

/// <summary>One party member card.</summary>
public sealed class MonVm : ObservableObject
{
    private readonly TrainersViewModel _owner;
    private readonly TrainerMonData _data;
    private BitmapSource? _preview;

    public MonVm(TrainersViewModel owner, TrainerMonData data)
    {
        _owner = owner;
        _data = data;
        RemoveCommand = new RelayCommand(() => owner.RemoveMon(this));
        UpCommand = new RelayCommand(() => owner.MoveMon(this, -1));
        DownCommand = new RelayCommand(() => owner.MoveMon(this, +1));
        UpdatePreview();
    }

    public TrainerMonData Data => _data;
    public ICommand RemoveCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand DownCommand { get; }
    public TrainersViewModel Owner => _owner;
    public BitmapSource? Preview { get => _preview; private set => Set(ref _preview, value); }

    private void Changed(string? name = null)
    {
        OnPropertyChanged(name);
        _owner.MarkDirty();
    }

    public string Species { get => _data.Species; set { if (_data.Species == (value ?? "").Trim()) return; _data.Species = (value ?? "").Trim(); Changed(); UpdatePreview(); } }
    public string Level { get => _data.Level.ToString(); set { if (int.TryParse(value, out var l) && l != _data.Level) { _data.Level = l; Changed(); } } }
    public string HeldItem { get => _data.HeldItem; set { value = (value ?? "").Trim(); if (value == _data.HeldItem) return; _data.HeldItem = value; Changed(); } }
    public string Ability { get => _data.Ability; set { value = (value ?? "").Trim(); if (value == _data.Ability) return; _data.Ability = value; Changed(); } }
    public string Nature { get => _data.Nature; set { value = (value ?? "").Trim(); if (value == _data.Nature) return; _data.Nature = value; Changed(); } }
    public string Nickname { get => _data.Nickname; set { value = (value ?? "").Trim(); if (value == _data.Nickname) return; _data.Nickname = value; Changed(); } }
    public bool IsShiny { get => _data.IsShiny; set { if (value == _data.IsShiny) return; _data.IsShiny = value; Changed(); } }
    public string Ivs { get => _data.Ivs; set { value = (value ?? "").Trim(); if (value == _data.Ivs) return; _data.Ivs = value; Changed(); } }

    private string GetMove(int i) => i < _data.Moves.Count ? _data.Moves[i] : "";
    private void SetMove(int i, string? value)
    {
        value = (value ?? "").Trim();
        if (value == "MOVE_NONE") value = "";
        if (GetMove(i) == value) return;
        var moves = Enumerable.Range(0, 4).Select(GetMove).ToList();
        moves[i] = value;
        _data.Moves = moves.Where(m => m.Length > 0).ToList();
        Changed($"Move{i + 1}");
    }
    public string Move1 { get => GetMove(0); set => SetMove(0, value); }
    public string Move2 { get => GetMove(1); set => SetMove(1, value); }
    public string Move3 { get => GetMove(2); set => SetMove(2, value); }
    public string Move4 { get => GetMove(3); set => SetMove(3, value); }

    private void UpdatePreview()
    {
        var root = _owner.Root;
        if (root == null) { Preview = null; return; }
        var path = TrainerGraphics.SpeciesFront(root, _data.Species);
        var img = path != null ? TrainerGraphics.LoadSprite(path) : null;
        Preview = img != null ? Imaging.ToBitmap(img) : null;
    }
}

/// <summary>
/// Trainer teams from src/data/trainers.h (C syntax) or src/data/trainers.party (competitive syntax, chosen via
/// COMPETITIVE_PARTY_SYNTAX): view, edit (name, class, picture, items, AI, party) and create new trainers.
/// Saving rewrites only the changed trainer's block; unchanged fields keep their exact text.
/// </summary>
public sealed class TrainersViewModel : ObservableObject, IToolkitModule
{
    private readonly ProjectContext _ctx;
    private TrainerSource? _source;
    private List<TrainerData> _all = [];
    private Dictionary<string, string> _pics = [];
    private TrainerData? _original;
    private bool _dirty, _loading;

    public TrainersViewModel(ProjectContext ctx)
    {
        _ctx = ctx;
        _ctx.ProjectChanged += (_, _) => { if (!_dirty) _ = LoadAsync(); };
        SaveCommand = new RelayCommand(Save, () => _dirty);
        DiscardCommand = new RelayCommand(() => { _dirty = false; ShowSelected(); }, () => _dirty);
        AddMonCommand = new RelayCommand(AddMon, () => Editing != null && Party.Count < TrainerFile.PartySize);
        NewTrainerCommand = new RelayCommand(NewTrainer, () => _source != null && _all.Count > 0 && !_dirty);
        View = new TrainersView { DataContext = this };
        _ = LoadAsync();
    }

    public string Title => "Trainers";
    public string Description => "Trainer teams: view, edit, create";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => _dirty;
    public string? UnsavedChangesDescription => _dirty ? $"Trainers: changes to {Editing?.Id} not saved yet." : null;
    public string? Root => _ctx.Root;

    public ICommand SaveCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand AddMonCommand { get; }
    public ICommand NewTrainerCommand { get; }

    public ObservableCollection<TrainerListItem> Trainers { get; } = [];
    public ObservableCollection<MonVm> Party { get; } = [];
    public TrainerSymbols Symbols { get; private set; } = new();
    public IReadOnlyList<string> SpeciesList => Symbols.Species;
    public IReadOnlyList<string> ItemList { get; private set; } = [];
    public IReadOnlyList<string> MoveList { get; private set; } = [];
    public IReadOnlyList<string> AbilityList { get; private set; } = [];

    private string _filter = "", _summary = "Reading trainers …", _status = "";
    private BitmapSource? _picPreview;
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) Apply(); } }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public BitmapSource? PicPreview { get => _picPreview; private set => Set(ref _picPreview, value); }

    private TrainerListItem? _selected;
    public TrainerListItem? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            if (_dirty && value != null && MessageBox.Show(Application.Current.MainWindow,
                    $"Discard the unsaved changes to {Editing?.Id}?", "Trainers", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                OnPropertyChanged();
                return;
            }
            _dirty = false;
            _selected = value;
            OnPropertyChanged();
            ShowSelected();
        }
    }

    public TrainerData? Editing { get; private set; }

    // Header fields of the trainer being edited
    public string Name { get => Editing?.Name ?? ""; set { if (Editing == null || Editing.Name == value) return; Editing.Name = (value ?? "").Trim(); MarkDirty(); } }
    public string TrainerClass { get => Editing?.TrainerClass ?? ""; set { if (Editing == null || Editing.TrainerClass == value) return; Editing.TrainerClass = (value ?? "").Trim(); MarkDirty(); } }
    public string Pic { get => Editing?.Pic ?? ""; set { if (Editing == null || Editing.Pic == value) return; Editing.Pic = (value ?? "").Trim(); MarkDirty(); UpdatePic(); } }
    public string Music { get => Editing?.Music ?? ""; set { if (Editing == null || Editing.Music == value) return; Editing.Music = (value ?? "").Trim(); MarkDirty(); } }
    public bool DoubleBattle { get => Editing?.DoubleBattle ?? false; set { if (Editing == null || Editing.DoubleBattle == value) return; Editing.DoubleBattle = value; MarkDirty(); } }
    public string AiFlags { get => Editing?.AiFlags ?? ""; set { if (Editing == null || Editing.AiFlags == value) return; Editing.AiFlags = (value ?? "").Trim(); MarkDirty(); } }
    public string Items
    {
        get => Editing == null ? "" : string.Join(", ", Editing.Items);
        set
        {
            if (Editing == null) return;
            var list = (value ?? "").Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (list.SequenceEqual(Editing.Items)) return;
            Editing.Items = list;
            MarkDirty();
        }
    }
    public bool HasTrainer => Editing != null;

    public void MarkDirty()
    {
        if (_loading) return;
        _dirty = true;
        Status = "Unsaved changes.";
        OnPropertyChanged(nameof(HasUnsavedChanges));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task LoadAsync()
    {
        var root = _ctx.Root;
        if (root == null) return;
        Summary = "Reading trainers …";
        var keep = Selected?.Id;
        TrainerSource source;
        try
        {
            (source, Symbols, _pics) = await Task.Run(() => (TrainerSource.Load(root), TrainerSymbols.Load(root), TrainerGraphics.TrainerPics(root)));
        }
        catch (IOException ex)
        {
            Summary = "Could not read trainers: " + ex.Message;
            return;
        }
        _source = source;
        _all = source.Trainers.ToList();
        ItemList = ["", .. Symbols.Items];
        MoveList = ["", .. Symbols.Moves];
        AbilityList = ["", .. Symbols.Abilities];
        OnPropertyChanged(nameof(Symbols));
        OnPropertyChanged(nameof(SpeciesList));
        OnPropertyChanged(nameof(ItemList));
        OnPropertyChanged(nameof(MoveList));
        OnPropertyChanged(nameof(AbilityList));
        Summary = _all.Count == 0
            ? $"No trainers found in {source.RelativePath}."
            : $"{_all.Count} trainers · {source.MonCount} Pokémon · source: {source.RelativePath} ({source.FormatName})"
              + (source.Problems.Count > 0 ? $" · {source.Problems.Count} not editable" : "");
        _dirty = false;
        Apply();
        _selected = null;
        Selected = Trainers.FirstOrDefault(t => t.Id == keep) ?? Trainers.FirstOrDefault();
    }

    private void Apply()
    {
        var f = Filter.Trim();
        Trainers.Clear();
        foreach (var d in _all.Where(d => d.Id != "TRAINER_NONE").Where(d => f.Length == 0 || d.Id.Contains(f, StringComparison.OrdinalIgnoreCase) || d.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                                          || d.TrainerClass.Contains(f, StringComparison.OrdinalIgnoreCase) || d.Party.Any(m => m.Species.Contains(f, StringComparison.OrdinalIgnoreCase))))
            Trainers.Add(new TrainerListItem(d));
    }

    private void ShowSelected()
    {
        _loading = true;
        Party.Clear();
        _original = Selected?.Data;
        Editing = _original?.Clone();
        if (Editing != null)
            foreach (var m in Editing.Party) Party.Add(new MonVm(this, m));
        foreach (var n in new[] { nameof(Editing), nameof(Name), nameof(TrainerClass), nameof(Pic), nameof(Music), nameof(DoubleBattle), nameof(AiFlags), nameof(Items), nameof(HasTrainer) })
            OnPropertyChanged(n);
        UpdatePic();
        _loading = false;
        _dirty = false;
        Status = Editing == null ? "" : $"{Editing.Id} · {Editing.Party.Count} Pokémon";
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdatePic()
    {
        PicPreview = null;
        if (Root == null || Editing == null || !_pics.TryGetValue(Editing.Pic, out var rel)) return;
        var img = TrainerGraphics.LoadSprite(ProjectPaths.Combine(Root, rel));
        if (img != null) PicPreview = Imaging.ToBitmap(img);
    }

    private void SyncParty()
    {
        if (Editing == null) return;
        Editing.Party = Party.Select(p => p.Data).ToList();
        MarkDirty();
    }

    public void RemoveMon(MonVm mon)
    {
        Party.Remove(mon);
        SyncParty();
    }

    public void MoveMon(MonVm mon, int delta)
    {
        int i = Party.IndexOf(mon), j = i + delta;
        if (i < 0 || j < 0 || j >= Party.Count) return;
        Party.Move(i, j);
        SyncParty();
    }

    private void AddMon()
    {
        if (Editing == null || Party.Count >= TrainerFile.PartySize) return;
        int level = Party.Count > 0 ? Party.Max(p => p.Data.Level) : 5;
        var species = Party.LastOrDefault()?.Data.Species ?? Symbols.Species.FirstOrDefault(s => s != "SPECIES_NONE") ?? "SPECIES_NONE";
        if (_source == null) return;
        Party.Add(new MonVm(this, _source.NewMon(species, level)));
        SyncParty();
    }

    private List<string> Validate(TrainerData d)
    {
        var errors = new List<string>();
        if (d.Name.Length == 0) errors.Add("The trainer needs a name.");
        if (d.Name.Length > TrainerFile.NameLength) errors.Add($"Name \"{d.Name}\" is longer than {TrainerFile.NameLength} characters.");
        if (d.Name.Contains('"')) errors.Add("The name must not contain quotes.");
        if (d.Party.Count == 0) errors.Add("The party is empty – add at least one Pokémon.");
        if (d.Party.Count > TrainerFile.PartySize) errors.Add($"At most {TrainerFile.PartySize} Pokémon.");
        if (d.DoubleBattle && d.Party.Count < 2) errors.Add("A double battle needs at least two Pokémon.");
        if (Symbols.Classes.Count > 0 && !Symbols.Classes.Contains(d.TrainerClass)) errors.Add($"Unknown trainer class {d.TrainerClass}.");
        if (Symbols.Pics.Count > 0 && !Symbols.Pics.Contains(d.Pic)) errors.Add($"Unknown trainer picture {d.Pic}.");
        foreach (var i in d.Items.Where(i => !Symbols.Items.Contains(i))) errors.Add($"Unknown item {i}.");
        if (d.Items.Count > 4) errors.Add("A trainer can carry at most 4 items.");
        for (int n = 0; n < d.Party.Count; n++)
        {
            var m = d.Party[n];
            string who = $"Pokémon {n + 1}";
            if (!Symbols.Species.Contains(m.Species)) errors.Add($"{who}: unknown species {m.Species}.");
            if (m.Level is < 1 or > 100) errors.Add($"{who}: level {m.Level} is not between 1 and 100.");
            if (m.HeldItem.Length > 0 && !Symbols.Items.Contains(m.HeldItem)) errors.Add($"{who}: unknown item {m.HeldItem}.");
            if (m.Ability.Length > 0 && !Symbols.Abilities.Contains(m.Ability)) errors.Add($"{who}: unknown ability {m.Ability}.");
            if (m.Nature.Length > 0 && Symbols.Natures.Count > 0 && !Symbols.Natures.Contains(m.Nature)) errors.Add($"{who}: unknown nature {m.Nature}.");
            foreach (var mv in m.Moves.Where(mv => !Symbols.Moves.Contains(mv))) errors.Add($"{who}: unknown move {mv}.");
            if (m.Nickname.Length > 10 || m.Nickname.Contains('"')) errors.Add($"{who}: nickname must be at most 10 characters without quotes.");
            if (m.Ivs.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(m.Ivs, @"^\s*([12]?\d|3[01])(\s*,\s*([12]?\d|3[01])){5}\s*$") && !m.Ivs.StartsWith("TRAINER_PARTY_IVS(", StringComparison.Ordinal))
                errors.Add($"{who}: IVs must be six numbers 0–31 (HP, Atk, Def, Speed, SpAtk, SpDef).");
        }
        return errors;
    }

    private void Save()
    {
        var root = Root;
        var d = Editing;
        var source = _source;
        if (root == null || d == null || _original == null || source == null) return;
        d.Party = Party.Select(p => p.Data).ToList();
        var errors = Validate(d);
        if (errors.Count > 0) { ProjectWriter.Warn("Save trainer", string.Join("\n", errors)); return; }
        if (source.Problems.TryGetValue(d.Id, out var problem)) { ProjectWriter.Warn("Save trainer", problem + " Edit this trainer in a text editor."); return; }
        PlannedFile file;
        try
        {
            file = ProjectWriter.ModifyText(root, source.RelativePath, t =>
            {
                if (t != source.Text) throw new InvalidOperationException($"{source.RelativePath} was changed outside the toolkit. Reload and try again.");
                return source.Write(t, _original, d);
            }, $"{d.Id}: {d.PartySummary}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            ProjectWriter.Warn("Save trainer", ex.Message);
            return;
        }
        if (!ProjectWriter.Apply(root, $"Trainer {d.Id}", source.RelativePath, [file],
                "Only this trainer's block changes; unchanged fields and Pokémon keep their exact text.",
                "Build the ROM afterwards to test the battle.")) return;
        _dirty = false;
        Status = "Saved.";
        _ctx.Reload();
    }

    private void NewTrainer()
    {
        var root = Root;
        var source = _source;
        if (root == null || source == null) return;
        var template = Selected?.Data ?? _all.FirstOrDefault(t => t.Party.Count > 0);
        var dlg = new InputDialog("New trainer",
            "Creates a new trainer with one Pokémon (based on the selected trainer's class and picture). You can edit the team afterwards. " +
            "The trainer can then be used in the event builder (\"Trainer battle\").",
            ("Constant (e.g. TRAINER_GUARD_ROUTE_101)", "TRAINER_"), ("Name shown in battle (max. 10 characters)", ""));
        if (dlg.ShowDialog() != true) return;
        var id = dlg.Values[0].Trim().ToUpperInvariant().Replace(' ', '_');
        if (!id.StartsWith("TRAINER_", StringComparison.Ordinal)) id = "TRAINER_" + id;
        var name = dlg.Values[1].Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^TRAINER_[A-Z0-9_]+$") || id == "TRAINER_") { ProjectWriter.Warn("New trainer", $"\"{id}\" is not a valid constant name."); return; }
        if (_all.Any(t => t.Id == id)) { ProjectWriter.Warn("New trainer", $"{id} already exists."); return; }
        var data = new TrainerData
        {
            Id = id,
            Name = name,
            TrainerClass = template?.TrainerClass ?? Symbols.Classes.FirstOrDefault() ?? "TRAINER_CLASS_PKMN_TRAINER_1",
            Pic = template?.Pic ?? Symbols.Pics.FirstOrDefault() ?? "TRAINER_PIC_HIKER",
            Music = template?.Music ?? "TRAINER_ENCOUNTER_MUSIC_MALE",
            AiFlags = template?.AiFlags.Length > 0 ? template.AiFlags : "AI_FLAG_BASIC_TRAINER",
        };
        data.Party.Add(source.NewMon(template?.Party.FirstOrDefault()?.Species ?? "SPECIES_ZIGZAGOON", template?.Party.FirstOrDefault()?.Level ?? 5));
        var errors = Validate(data);
        if (errors.Count > 0) { ProjectWriter.Warn("New trainer", string.Join("\n", errors)); return; }
        try
        {
            var oppFull = ProjectPaths.Combine(root, TrainerFile.OpponentsFile);
            var slot = TrainerFile.FindFreeId(File.ReadAllText(oppFull), _all.Select(t => t.Id).ToList());
            if (slot.Problem != null) { ProjectWriter.Warn("New trainer", slot.Problem); return; }
            var files = new List<PlannedFile>
            {
                ProjectWriter.ModifyText(root, TrainerFile.OpponentsFile, t => TrainerFile.AddConstant(t, id, slot),
                    $"#define {id} {slot.Id}" + (slot.RaisesCount ? $", TRAINERS_COUNT → {slot.Id + 1}" : " (unused number)")),
                ProjectWriter.ModifyText(root, source.RelativePath, t => source.Append(t, data), $"New entry {id}"),
            };
            if (!ProjectWriter.Apply(root, $"New trainer {id}", source.RelativePath, files,
                    $"Trainer number {slot.Id}. Each trainer has a \"defeated\" flag; the number decides which one.")) return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            ProjectWriter.Warn("New trainer", ex.Message);
            return;
        }
        Filter = id;
        _ctx.Reload();
    }
}
