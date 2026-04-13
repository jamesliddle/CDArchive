using System.Collections.ObjectModel;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

/// <summary>
/// ViewModel for the Pick Lists editor screen.
/// Manages all eight pick lists (Forms, Categories, Catalogues, Keys,
/// Instruments, Creative Roles, Ensembles, Voice Types) through a single dropdown-driven UI.
/// </summary>
public partial class PickListsViewModel : ObservableObject
{
    private readonly ICanonDataService _svc;
    private readonly CanonViewModel _canonVm;

    // Working copies — always kept sorted
    private readonly List<string> _forms          = [];
    private readonly List<string> _categories     = [];
    private readonly List<string> _catalogPrefixes = [];
    private readonly List<string> _keyTonalities  = [];
    private readonly List<string> _instruments    = [];
    private readonly List<string> _creativeRoles  = [];
    private readonly List<EnsembleDefinition> _ensembles = [];
    private readonly List<string> _voiceTypes     = [];

    // Rename tracking for lists that map to piece fields
    private readonly Dictionary<string, string> _formRenames     = new();
    private readonly Dictionary<string, string> _categoryRenames = new();
    private readonly Dictionary<string, string> _catalogRenames  = new();
    private readonly Dictionary<string, string> _keyRenames      = new();

    public static IReadOnlyList<string> PickListNames { get; } =
        ["Forms", "Categories", "Catalogues", "Keys", "Instruments", "Creative Roles", "Ensembles", "Voice Types"];

    [ObservableProperty] private int _selectedListIndex;
    [ObservableProperty] private ObservableCollection<string> _currentItems = [];
    [ObservableProperty] private int _selectedItemIndex = -1;
    [ObservableProperty] private string _editText = "";
    [ObservableProperty] private bool _isEnsembleList;
    [ObservableProperty] private string _statusMessage = "";

    public PickListsViewModel(ICanonDataService svc, CanonViewModel canonVm)
    {
        _svc = svc;
        _canonVm = canonVm;
    }

    /// <summary>Reloads all lists from JSON. Called on each navigation to this screen.</summary>
    public async Task LoadAsync()
    {
        var pl = await _svc.LoadPickListsAsync();

        Load(_forms,          pl.Forms);
        Load(_categories,     pl.Categories);
        Load(_catalogPrefixes, pl.CatalogPrefixes);
        Load(_keyTonalities,  pl.KeyTonalities);
        Load(_instruments,    pl.Instruments);
        Load(_creativeRoles,  pl.CreativeRoles);
        Load(_voiceTypes,     pl.VoiceTypes);

        _ensembles.Clear();
        if (pl.Ensembles != null)
            _ensembles.AddRange(pl.Ensembles.Select(e => new EnsembleDefinition
            {
                Name    = e.Name,
                Members = e.Members != null ? new List<string>(e.Members) : null,
            }));

        _formRenames.Clear();
        _categoryRenames.Clear();
        _catalogRenames.Clear();
        _keyRenames.Clear();

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
            // Propagate renames to all in-memory pieces
            ApplyRenames();

            var pl = new CanonPickLists
            {
                Forms           = _forms.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                Categories      = _categories.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                CatalogPrefixes = _catalogPrefixes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                KeyTonalities   = _keyTonalities.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                Instruments     = _instruments.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                CreativeRoles   = _creativeRoles.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                Ensembles       = SortedEnsembles().ToList(),
                VoiceTypes      = _voiceTypes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            };

            await _svc.SavePickListsAsync(pl);
            _canonVm.PickLists = pl;           // Refresh the shared in-memory copy

            _formRenames.Clear();
            _categoryRenames.Clear();
            _catalogRenames.Clear();
            _keyRenames.Clear();

            StatusMessage = "Pick lists saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    // ── Helpers exposed to view code-behind ──────────────────────────────────

    /// <summary>
    /// Returns a snapshot CanonPickLists from the current working copies,
    /// suitable for passing to dialogs that need the instrument list.
    /// </summary>
    public CanonPickLists PickListsForDialog => new()
    {
        Instruments  = new List<string>(_instruments),
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
        IsEnsembleList = SelectedListIndex == 6;

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

    private List<string> CurrentStringList() => SelectedListIndex switch
    {
        0 => _forms,
        1 => _categories,
        2 => _catalogPrefixes,
        3 => _keyTonalities,
        4 => _instruments,
        5 => _creativeRoles,
        7 => _voiceTypes,
        _ => [],
    };

    private Dictionary<string, string> CurrentRenameDict() => SelectedListIndex switch
    {
        0 => _formRenames,
        1 => _categoryRenames,
        2 => _catalogRenames,
        3 => _keyRenames,
        _ => new Dictionary<string, string>(),
    };

    private IEnumerable<EnsembleDefinition> SortedEnsembles() =>
        _ensembles.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

    private void SortEnsembles() =>
        _ensembles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

    // ── Rename propagation ────────────────────────────────────────────────────

    private void ApplyRenames()
    {
        if (_formRenames.Count == 0 && _categoryRenames.Count == 0 &&
            _catalogRenames.Count == 0 && _keyRenames.Count == 0)
            return;

        var count = 0;
        foreach (var piece in _canonVm.Pieces)
            count += ApplyRenamesToPiece(piece);

        if (count > 0)
            _ = _svc.SavePiecesAsync(_canonVm.Pieces.ToList());
    }

    private int ApplyRenamesToPiece(CanonPiece piece)
    {
        var count = 0;

        if (piece.Form != null && _formRenames.TryGetValue(piece.Form, out var nf))
        { piece.Form = nf; count++; }

        if (piece.InstrumentationCategory != null &&
            _categoryRenames.TryGetValue(piece.InstrumentationCategory, out var nc))
        { piece.InstrumentationCategory = nc; count++; }

        if (piece.KeyTonality != null && _keyRenames.TryGetValue(piece.KeyTonality, out var nk))
        { piece.KeyTonality = nk; count++; }

        if (piece.CatalogInfo != null)
        {
            foreach (var ci in piece.CatalogInfo)
            {
                if (ci.Catalog != null && _catalogRenames.TryGetValue(ci.Catalog, out var ncat))
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
