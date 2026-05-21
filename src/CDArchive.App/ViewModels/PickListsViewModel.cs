using System.Collections.ObjectModel;
using CDArchive.Core.Helpers;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

/// <summary>
/// ViewModel for the Pick Lists editor screen.
/// Manages all ten pick lists (Forms, Categories, Catalogues, Keys,
/// Instruments, Creative Roles, Ensembles, Voice Types, Performer Roles,
/// Labels) through a single dropdown-driven UI.
///
/// <para>
/// Rework H30 regression: pre-fix the VM identified lists by their display
/// index — most painfully <c>SelectedListIndex == 6</c> (= Ensembles) appeared
/// as a literal in two places, and <c>CurrentStringList()</c> /
/// <c>CurrentRenameDict()</c> were positional <c>switch</c> expressions over
/// the same display order. Reordering or inserting a new pick list silently
/// routed every Add / Update / Remove to the wrong list. Now the dispatch
/// runs on <see cref="PickListKind"/> via <see cref="PickListKinds"/>; the
/// display order is just a presentation array.
/// </para>
/// </summary>
public partial class PickListsViewModel : ObservableObject
{
    private readonly ICanonDataService _svc;
    private readonly CanonViewModel _canonVm;

    // Working copies — always kept sorted. Keyed by kind, so reshuffling the
    // display order in PickListKinds.OrderedKinds doesn't risk routing
    // commands to the wrong list.
    private readonly Dictionary<PickListKind, List<string>> _stringLists;
    private readonly List<EnsembleDefinition> _ensembles = [];

    // Rename tracking for lists that map to piece fields. Same keying
    // discipline: indexed by kind, not by display position.
    private readonly Dictionary<PickListKind, Dictionary<string, string>> _renames;

    /// <summary>
    /// Display order for the selector ComboBox. Bound via <c>x:Static</c>
    /// from <c>PickListsView.xaml</c>.
    /// </summary>
    public static IReadOnlyList<string> PickListNames { get; } = PickListKinds.OrderedDisplayNames;

    [ObservableProperty] private int _selectedListIndex;
    [ObservableProperty] private ObservableCollection<string> _currentItems = [];
    [ObservableProperty] private int _selectedItemIndex = -1;
    [ObservableProperty] private string _editText = "";
    [ObservableProperty] private bool _isEnsembleList;
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>
    /// The kind currently displayed. Computed from <see cref="SelectedListIndex"/>;
    /// the rest of the VM dispatches on this, never on the raw index.
    /// </summary>
    public PickListKind CurrentKind => PickListKinds.KindAt(SelectedListIndex);

    public PickListsViewModel(ICanonDataService svc, CanonViewModel canonVm)
    {
        _svc = svc;
        _canonVm = canonVm;

        _stringLists = new Dictionary<PickListKind, List<string>>
        {
            [PickListKind.Forms]          = [],
            [PickListKind.Categories]     = [],
            [PickListKind.Catalogues]     = [],
            [PickListKind.Keys]           = [],
            [PickListKind.Instruments]    = [],
            [PickListKind.CreativeRoles]  = [],
            [PickListKind.VoiceTypes]     = [],
            [PickListKind.PerformerRoles] = [],
            [PickListKind.Labels]         = [],
        };

        _renames = new Dictionary<PickListKind, Dictionary<string, string>>
        {
            [PickListKind.Forms]      = new(),
            [PickListKind.Categories] = new(),
            [PickListKind.Catalogues] = new(),
            [PickListKind.Keys]       = new(),
        };
    }

    /// <summary>Reloads all lists from JSON. Called on each navigation to this screen.</summary>
    public async Task LoadAsync()
    {
        var pl = await _svc.LoadPickListsAsync();

        Load(_stringLists[PickListKind.Forms],          pl.Forms);
        Load(_stringLists[PickListKind.Categories],     pl.Categories);
        Load(_stringLists[PickListKind.Catalogues],     pl.CatalogPrefixes);
        Load(_stringLists[PickListKind.Keys],           pl.KeyTonalities);
        Load(_stringLists[PickListKind.Instruments],    pl.Instruments);
        Load(_stringLists[PickListKind.CreativeRoles],  pl.CreativeRoles);
        Load(_stringLists[PickListKind.VoiceTypes],     pl.VoiceTypes);
        Load(_stringLists[PickListKind.PerformerRoles], pl.PerformerRoles);
        Load(_stringLists[PickListKind.Labels],         pl.Labels);

        _ensembles.Clear();
        if (pl.Ensembles != null)
            _ensembles.AddRange(pl.Ensembles.Select(e => new EnsembleDefinition
            {
                Name    = e.Name,
                Members = e.Members != null ? new List<string>(e.Members) : null,
            }));

        foreach (var dict in _renames.Values) dict.Clear();

        RefreshCurrentList();
        StatusMessage = "";
    }

    private static void Load(List<string> target, IEnumerable<string> source)
    {
        target.Clear();
        target.AddRange(source);
        target.Sort(StringComparer.OrdinalIgnoreCase);
    }

    partial void OnSelectedListIndexChanged(int value) => RefreshCurrentList();

    partial void OnSelectedItemIndexChanged(int value)
    {
        if (value >= 0 && value < CurrentItems.Count)
            EditText = RawNameAt(value);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void AddItem()
    {
        var value = EditText.Trim();
        if (string.IsNullOrEmpty(value)) return;

        if (IsEnsembleList)
        {
            if (_ensembles.Any(e => string.Equals(e.Name, value, StringComparison.OrdinalIgnoreCase)))
                return;
            _ensembles.Add(new EnsembleDefinition { Name = value });
            SortEnsembles();
        }
        else
        {
            var list = CurrentStringList();
            if (list.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
            list.Add(value);
            list.Sort(StringComparer.OrdinalIgnoreCase);
        }

        RefreshCurrentList();
        EditText = "";
        StatusMessage = $"Added \"{value}\". Save to persist.";
    }

    [RelayCommand]
    private void UpdateItem()
    {
        var idx = SelectedItemIndex;
        if (idx < 0 || idx >= CurrentItems.Count) return;

        var newValue = EditText.Trim();
        if (string.IsNullOrEmpty(newValue)) return;

        var oldName = RawNameAt(idx);
        if (oldName == newValue) return;

        if (IsEnsembleList)
        {
            var ens = SortedEnsembles().ElementAtOrDefault(idx);
            if (ens == null) return;
            ens.Name = newValue;
            SortEnsembles();
        }
        else
        {
            var list = CurrentStringList();
            var renames = CurrentRenameDict();

            // Chain renames: if oldName was itself already a rename target, update the original mapping
            var originalKey = renames.FirstOrDefault(r => r.Value == oldName).Key;
            if (originalKey != null)
                renames[originalKey] = newValue;
            else
                renames[oldName] = newValue;

            var i = list.IndexOf(oldName);
            if (i >= 0) list[i] = newValue;
            list.Sort(StringComparer.OrdinalIgnoreCase);
        }

        RefreshCurrentList();
        StatusMessage = "Renamed. Save to apply to all pieces.";
    }

    [RelayCommand]
    private void RemoveItem()
    {
        var idx = SelectedItemIndex;
        if (idx < 0 || idx >= CurrentItems.Count) return;

        var name = RawNameAt(idx);

        if (IsEnsembleList)
        {
            var ens = SortedEnsembles().ElementAtOrDefault(idx);
            if (ens != null) _ensembles.Remove(ens);
        }
        else
        {
            CurrentStringList().Remove(name);
        }

        RefreshCurrentList();
        StatusMessage = $"Removed \"{name}\". Save to persist.";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            // Propagate renames to all in-memory pieces. Returns the number of
            // piece-level fields actually mutated; when > 0 the pieces save
            // must run alongside the pick-lists save in the same transaction
            // so a failure either lands both or neither — see Rework C14.
            var renamedCount = ApplyRenames();

            var pl = new CanonPickLists
            {
                Forms           = Sorted(PickListKind.Forms),
                Categories      = Sorted(PickListKind.Categories),
                CatalogPrefixes = Sorted(PickListKind.Catalogues),
                KeyTonalities   = Sorted(PickListKind.Keys),
                Instruments     = Sorted(PickListKind.Instruments),
                CreativeRoles   = Sorted(PickListKind.CreativeRoles),
                Ensembles       = SortedEnsembles().ToList(),
                VoiceTypes      = Sorted(PickListKind.VoiceTypes),
                PerformerRoles  = Sorted(PickListKind.PerformerRoles),
                Labels          = Sorted(PickListKind.Labels),
            };

            await _svc.SaveBatchAsync(
                pickLists: pl,
                pieces:    renamedCount > 0 ? _canonVm.Pieces.ToList() : null);

            _canonVm.PickLists = pl;           // Refresh the shared in-memory copy

            foreach (var dict in _renames.Values) dict.Clear();

            StatusMessage = renamedCount > 0
                ? $"Pick lists saved. Renamed {renamedCount} piece field(s)."
                : "Pick lists saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    private List<string> Sorted(PickListKind kind) =>
        _stringLists[kind].OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

    // ── Helpers exposed to view code-behind ──────────────────────────────────

    /// <summary>
    /// Returns a snapshot CanonPickLists from the current working copies,
    /// suitable for passing to dialogs that need the instrument list.
    /// </summary>
    public CanonPickLists PickListsForDialog => new()
    {
        Instruments  = new List<string>(_stringLists[PickListKind.Instruments]),
        Ensembles    = _ensembles.Select(e => new EnsembleDefinition
        {
            Name    = e.Name,
            Members = e.Members != null ? new List<string>(e.Members) : null,
        }).ToList(),
    };

    /// <summary>Returns the EnsembleDefinition currently selected, or null.</summary>
    public EnsembleDefinition? SelectedEnsemble()
    {
        if (!IsEnsembleList || SelectedItemIndex < 0) return null;
        return SortedEnsembles().ElementAtOrDefault(SelectedItemIndex);
    }

    /// <summary>Applies updated member list to the selected ensemble and refreshes the display.</summary>
    public void ApplyEnsembleMembers(List<string>? members)
    {
        var ens = SelectedEnsemble();
        if (ens == null) return;
        ens.Members = members is { Count: > 0 } ? members : null;
        RefreshCurrentList();
        StatusMessage = "Members updated. Save to persist.";
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private void RefreshCurrentList()
    {
        var kind = CurrentKind;
        IsEnsembleList = kind == PickListKind.Ensembles;

        IEnumerable<string> items = IsEnsembleList
            ? SortedEnsembles().Select(e => e.IsFixed ? $"{e.Name}  ({e.Members!.Count})" : e.Name)
            : (IEnumerable<string>)CurrentStringList();

        CurrentItems = new ObservableCollection<string>(items);
        SelectedItemIndex = -1;
        EditText = "";
    }

    /// <summary>Returns the raw ensemble or string name at the given display index (strips member count).</summary>
    private string RawNameAt(int idx)
    {
        if (IsEnsembleList)
            return SortedEnsembles().ElementAtOrDefault(idx)?.Name ?? "";
        var list = CurrentStringList();
        return idx < list.Count ? list[idx] : "";
    }

    /// <summary>
    /// The string-list backing the currently selected kind. For
    /// <see cref="PickListKind.Ensembles"/> (which is not a string list)
    /// returns an empty list — callers gate on <see cref="IsEnsembleList"/>
    /// before invoking, so this branch is defensive only.
    /// </summary>
    private List<string> CurrentStringList() =>
        _stringLists.TryGetValue(CurrentKind, out var list) ? list : [];

    /// <summary>
    /// The rename-tracking dictionary for the currently selected kind, or
    /// an empty dict for kinds whose values don't map to a piece-level field.
    /// </summary>
    private Dictionary<string, string> CurrentRenameDict() =>
        _renames.TryGetValue(CurrentKind, out var dict) ? dict : new Dictionary<string, string>();

    private IEnumerable<EnsembleDefinition> SortedEnsembles() =>
        _ensembles.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

    private void SortEnsembles() =>
        _ensembles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

    // ── Rename propagation ────────────────────────────────────────────────────

    /// <summary>
    /// Walks every piece (and subpieces) and applies the pending Form /
    /// Category / Catalogue / Key renames. Returns the number of piece-level
    /// field replacements made — the caller decides whether to bundle a
    /// pieces save into the same transactional batch as the pick-lists save.
    /// Previously this method fire-and-forgot a pieces save itself (retired
    /// Rework C14): failures vanished silently, the status message lied, and
    /// the in-flight save could race a Canon reload.
    /// </summary>
    private int ApplyRenames()
    {
        if (_renames.Values.All(d => d.Count == 0))
            return 0;

        var count = 0;
        foreach (var piece in _canonVm.Pieces)
            count += ApplyRenamesToPiece(piece);

        return count;
    }

    private int ApplyRenamesToPiece(CanonPiece piece)
    {
        var count = 0;
        var formRenames     = _renames[PickListKind.Forms];
        var categoryRenames = _renames[PickListKind.Categories];
        var catalogRenames  = _renames[PickListKind.Catalogues];
        var keyRenames      = _renames[PickListKind.Keys];

        if (piece.Form != null && formRenames.TryGetValue(piece.Form, out var nf))
        { piece.Form = nf; count++; }

        if (piece.InstrumentationCategory != null &&
            categoryRenames.TryGetValue(piece.InstrumentationCategory, out var nc))
        { piece.InstrumentationCategory = nc; count++; }

        if (piece.KeyTonality != null && keyRenames.TryGetValue(piece.KeyTonality, out var nk))
        { piece.KeyTonality = nk; count++; }

        if (piece.CatalogInfo != null)
        {
            foreach (var ci in piece.CatalogInfo)
            {
                if (ci.Catalog != null && catalogRenames.TryGetValue(ci.Catalog, out var ncat))
                { ci.Catalog = ncat; count++; }
            }
        }

        // Recurse into subpieces
        if (piece.Subpieces != null)
            foreach (var sub in piece.Subpieces)
                count += ApplyRenamesToPiece(sub);

        return count;
    }
}
