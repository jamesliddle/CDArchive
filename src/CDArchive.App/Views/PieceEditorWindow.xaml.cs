using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class PieceEditorWindow : Window
{
    private readonly CanonPiece _piece;
    private readonly CanonPickLists _pickLists;
    private readonly string _composerName;
    private readonly List<CanonPiece> _subpieces;
    private readonly List<CanonPieceVersion> _versions;
    private readonly List<RoleEntry> _roles;
    private readonly List<InstrumentEntry> _pieceInstruments = [];
    private readonly List<CatalogInfo> _catalogEntries = [];
    private readonly List<string> _librettists = [];

    /// <summary>
    /// The piece being edited (or newly created).
    /// </summary>
    public CanonPiece Piece => _piece;

    /// <summary>
    /// Accumulated renames from pick list editing within this session.
    /// </summary>
    public Dictionary<string, string> FormRenames { get; } = new();
    public Dictionary<string, string> CategoryRenames { get; } = new();
    public Dictionary<string, string> CatalogRenames { get; } = new();
    public Dictionary<string, string> KeyRenames { get; } = new();
    public Dictionary<string, string> InstrumentRenames { get; } = new();

    public PieceEditorWindow(
        CanonPickLists pickLists,
        string composerName,
        CanonPiece? piece = null)
    {
        InitializeComponent();

        _pickLists = pickLists;
        _composerName = composerName;
        _piece = piece ?? new CanonPiece { Composer = composerName };
        _subpieces = _piece.Subpieces?.ToList() ?? [];
        _versions = _piece.Versions?.ToList() ?? [];
        _roles = _piece.Roles.HasValue ? RoleEntry.ParseRoles(_piece.Roles.Value) : [];

        Title = piece == null ? "New Piece" : "Edit Piece";

        PopulateDropdowns();
        LoadFromPiece();
        RefreshSubpieceList();
        RefreshVersionList();
        RefreshRoleList();
    }

    private void PopulateDropdowns()
    {
        FormCombo.ItemsSource = _pickLists.Forms;
        KeyTonalityCombo.ItemsSource = _pickLists.KeyTonalities;
        CatalogPrefixCombo.ItemsSource = _pickLists.CatalogPrefixes;
        CategoryCombo.ItemsSource = _pickLists.Categories;
    }

    private void LoadFromPiece()
    {
        FormCombo.Text = _piece.Form ?? "";
        TitleBox.Text = _piece.Title ?? "";
        TitleEnglishBox.Text = _piece.TitleEnglish ?? "";
        SubtitleBox.Text = _piece.Subtitle ?? "";
        NicknameBox.Text = _piece.Nickname ?? "";
        NumberBox.Text = _piece.Number?.ToString() ?? "";
        KeyTonalityCombo.Text = _piece.KeyTonality ?? "";
        DescriptiveTitleBox.Text = _piece.DescriptiveTitle ?? "";
        NameBox.Text = _piece.Name ?? "";
        CategoryCombo.Text = _piece.InstrumentationCategory ?? "";
        PubYearBox.Text = _piece.PublicationYear?.ToString() ?? "";

        // Composition years (stored as a JSON string value)
        CompYearsBox.Text = _piece.CompositionYears?.ValueKind == System.Text.Json.JsonValueKind.String
            ? _piece.CompositionYears.Value.GetString() ?? ""
            : _piece.CompositionYears?.ToString() ?? "";

        // Librettist (stored as JSON array of strings)
        if (_piece.Librettist?.ValueKind == System.Text.Json.JsonValueKind.Array)
            _librettists.AddRange(_piece.Librettist.Value.EnumerateArray()
                .Select(e => e.GetString() ?? "").Where(s => s.Length > 0));
        else if (_piece.Librettist?.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var s = _piece.Librettist.Value.GetString();
            if (!string.IsNullOrWhiteSpace(s)) _librettists.Add(s.Trim());
        }
        RefreshLibrettistList();

        // Text author (stored as a JSON string value)
        TextAuthorBox.Text = _piece.TextAuthor?.ValueKind == System.Text.Json.JsonValueKind.String
            ? _piece.TextAuthor.Value.GetString() ?? ""
            : _piece.TextAuthor?.ToString() ?? "";

        // Key mode combo
        var mode = (_piece.KeyMode ?? "").ToLowerInvariant();
        foreach (ComboBoxItem item in KeyModeCombo.Items)
        {
            if ((item.Content as string ?? "") == mode)
            {
                KeyModeCombo.SelectedItem = item;
                break;
            }
        }

        // Catalog info — all entries
        if (_piece.CatalogInfo != null)
            _catalogEntries.AddRange(_piece.CatalogInfo);
        RefreshCatalogList();

        // Instrumentation — parse current instruments into the piece list
        if (_piece.Instrumentation.HasValue)
            _pieceInstruments.AddRange(InstrumentEntry.ParseInstrumentation(_piece.Instrumentation.Value));
        RefreshInstrumentList();
    }

    private void SaveToPiece()
    {
        _piece.Composer = _composerName;
        _piece.Form = NullIfEmpty(FormCombo.Text);
        _piece.Title = NullIfEmpty(TitleBox.Text);
        _piece.TitleEnglish = NullIfEmpty(TitleEnglishBox.Text);
        _piece.Subtitle = NullIfEmpty(SubtitleBox.Text);
        _piece.Nickname = NullIfEmpty(NicknameBox.Text);
        _piece.DescriptiveTitle = NullIfEmpty(DescriptiveTitleBox.Text);
        _piece.Name = NullIfEmpty(NameBox.Text);
        _piece.InstrumentationCategory = NullIfEmpty(CategoryCombo.Text);
        _piece.KeyTonality = NullIfEmpty(KeyTonalityCombo.Text);

        _piece.Number = int.TryParse(NumberBox.Text.Trim(), out var n) ? n : null;
        _piece.PublicationYear = int.TryParse(PubYearBox.Text.Trim(), out var y) ? y : null;

        var selectedMode = (KeyModeCombo.SelectedItem as ComboBoxItem)?.Content as string;
        _piece.KeyMode = string.IsNullOrEmpty(selectedMode) ? null : selectedMode;

        // Composition years
        var compYears = NullIfEmpty(CompYearsBox.Text);
        _piece.CompositionYears = compYears != null
            ? System.Text.Json.JsonDocument.Parse($"\"{compYears}\"").RootElement.Clone()
            : null;

        // Librettist — store as JSON string array
        _piece.Librettist = _librettists.Count > 0
            ? System.Text.Json.JsonSerializer.SerializeToElement(_librettists.ToArray())
            : null;

        // Text author — store as JSON string
        var textAuthor = NullIfEmpty(TextAuthorBox.Text);
        _piece.TextAuthor = textAuthor != null
            ? System.Text.Json.JsonSerializer.SerializeToElement(textAuthor)
            : null;

        // Catalog info — all entries from the list
        _piece.CatalogInfo = _catalogEntries.Count > 0 ? _catalogEntries.ToList() : null;

        // Instrumentation
        _piece.Instrumentation = InstrumentEntry.SerializeInstrumentation(_pieceInstruments);

        // Subpieces
        _piece.Subpieces = _subpieces.Count > 0 ? _subpieces.ToList() : null;

        // Versions
        _piece.Versions = _versions.Count > 0 ? _versions.ToList() : null;

        // Roles
        _piece.Roles = RoleEntry.SerializeRoles(_roles);
    }

    // --- Subpiece management ---

    private void RefreshSubpieceList()
    {
        var selectedTag = (SubpieceList.SelectedItem as ListBoxItem)?.Tag;
        SubpieceList.Items.Clear();
        foreach (var sp in _subpieces)
        {
            var label = FormatSubpieceLabel(sp);
            var item = new ListBoxItem { Content = label, Tag = sp };
            SubpieceList.Items.Add(item);
            if (sp == selectedTag)
                SubpieceList.SelectedItem = item;
        }
    }

    private static string FormatSubpieceLabel(CanonPiece sp) => sp.SubpieceDisplayTitle;

    private CanonPiece? SelectedSubpiece =>
        (SubpieceList.SelectedItem as ListBoxItem)?.Tag as CanonPiece;

    private void OnAddSubpieceClick(object sender, RoutedEventArgs e)
    {
        var nextNumber = _subpieces.Count > 0
            ? _subpieces.Where(s => s.Number.HasValue).Select(s => s.Number!.Value).DefaultIfEmpty(0).Max() + 1
            : 1;
        var newMovement = new CanonPiece { Number = nextNumber };

        var editor = new MovementEditorWindow(_pickLists, newMovement) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _subpieces.Add(editor.Movement);
            RefreshSubpieceList();
        }
    }

    private void OnEditSubpieceClick(object sender, RoutedEventArgs e)
    {
        EditSelectedSubpiece();
    }

    private void OnSubpieceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        EditSelectedSubpiece();
    }

    private void EditSelectedSubpiece()
    {
        if (SelectedSubpiece is not { } sp) return;

        var editor = new MovementEditorWindow(_pickLists, sp) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            RefreshSubpieceList();
        }
    }

    private void OnRemoveSubpieceClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSubpiece is not { } sp) return;
        _subpieces.Remove(sp);
        RefreshSubpieceList();
    }

    private void OnMoveSubpieceUpClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSubpiece is not { } sp) return;
        var idx = _subpieces.IndexOf(sp);
        if (idx <= 0) return;

        // Swap positions in the list and swap Number values
        (_subpieces[idx], _subpieces[idx - 1]) = (_subpieces[idx - 1], _subpieces[idx]);
        (sp.Number, _subpieces[idx].Number) = (_subpieces[idx].Number, sp.Number);
        RefreshSubpieceList();
    }

    private void OnMoveSubpieceDownClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSubpiece is not { } sp) return;
        var idx = _subpieces.IndexOf(sp);
        if (idx < 0 || idx >= _subpieces.Count - 1) return;

        (_subpieces[idx], _subpieces[idx + 1]) = (_subpieces[idx + 1], _subpieces[idx]);
        (sp.Number, _subpieces[idx].Number) = (_subpieces[idx].Number, sp.Number);
        RefreshSubpieceList();
    }

    // --- Role management ---

    private void RefreshRoleList()
    {
        var selectedTag = (RoleList.SelectedItem as ListBoxItem)?.Tag;
        RoleList.Items.Clear();
        foreach (var r in _roles)
        {
            var item = new ListBoxItem { Content = r.DisplayLabel, Tag = r };
            RoleList.Items.Add(item);
            if (r == selectedTag) RoleList.SelectedItem = item;
        }
    }

    private RoleEntry? SelectedRole =>
        (RoleList.SelectedItem as ListBoxItem)?.Tag as RoleEntry;

    private void OnAddRoleClick(object sender, RoutedEventArgs e)
    {
        var editor = new RoleEditorWindow(_pickLists) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _roles.Add(editor.Role);
            RefreshRoleList();
        }
    }

    private void OnEditRoleClick(object sender, RoutedEventArgs e) => EditSelectedRole();

    private void OnRoleDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedRole();

    private void EditSelectedRole()
    {
        if (SelectedRole is not { } r) return;
        var editor = new RoleEditorWindow(_pickLists, r) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            var idx = _roles.IndexOf(r);
            _roles[idx] = editor.Role;
            RefreshRoleList();
        }
    }

    private void OnRemoveRoleClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRole is not { } r) return;
        _roles.Remove(r);
        RefreshRoleList();
    }

    private void OnMoveRoleUpClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRole is not { } r) return;
        var idx = _roles.IndexOf(r);
        if (idx <= 0) return;
        (_roles[idx], _roles[idx - 1]) = (_roles[idx - 1], _roles[idx]);
        RefreshRoleList();
    }

    private void OnMoveRoleDownClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRole is not { } r) return;
        var idx = _roles.IndexOf(r);
        if (idx < 0 || idx >= _roles.Count - 1) return;
        (_roles[idx], _roles[idx + 1]) = (_roles[idx + 1], _roles[idx]);
        RefreshRoleList();
    }

    // --- Catalog list ---

    private void RefreshCatalogList()
    {
        CatalogList.Items.Clear();
        foreach (var cat in _catalogEntries)
        {
            var label = $"{cat.Catalog} {cat.CatalogNumber}".Trim();
            CatalogList.Items.Add(new ListBoxItem { Content = label, Tag = cat });
        }
    }

    private void OnAddCatalogClick(object sender, RoutedEventArgs e)
    {
        var prefix = CatalogPrefixCombo.Text.Trim();
        var number = CatalogNumberBox.Text.Trim();
        if (string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(number)) return;
        _catalogEntries.Add(new CatalogInfo
        {
            Catalog = prefix,
            CatalogNumber = string.IsNullOrEmpty(number) ? null : number
        });
        RefreshCatalogList();
        CatalogPrefixCombo.Text = "";
        CatalogNumberBox.Text = "";
    }

    private void OnRemoveCatalogClick(object sender, RoutedEventArgs e)
    {
        if (CatalogList.SelectedItem is not ListBoxItem item || item.Tag is not CatalogInfo cat) return;
        _catalogEntries.Remove(cat);
        RefreshCatalogList();
    }

    // --- Librettist list ---

    private void RefreshLibrettistList()
    {
        var selected = LibrettistList.SelectedItem as string;
        LibrettistList.Items.Clear();
        foreach (var name in _librettists)
            LibrettistList.Items.Add(name);
        if (selected != null && LibrettistList.Items.Contains(selected))
            LibrettistList.SelectedItem = selected;
    }

    private void OnAddLibrettistClick(object sender, RoutedEventArgs e)
    {
        var name = LibrettistEntryBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        _librettists.Add(name);
        RefreshLibrettistList();
        LibrettistList.SelectedItem = name;
        LibrettistEntryBox.Text = "";
        LibrettistEntryBox.Focus();
    }

    private void OnRemoveLibrettistClick(object sender, RoutedEventArgs e)
    {
        if (LibrettistList.SelectedItem is not string name) return;
        _librettists.Remove(name);
        RefreshLibrettistList();
    }

    // --- Instrumentation list ---

    private void RefreshInstrumentList()
    {
        var selectedIdx = InstrumentsList.SelectedIndex;
        InstrumentsList.Items.Clear();
        foreach (var entry in _pieceInstruments)
            InstrumentsList.Items.Add(entry.DisplayLabel);
        if (selectedIdx >= 0 && selectedIdx < InstrumentsList.Items.Count)
            InstrumentsList.SelectedIndex = selectedIdx;

        AvailableInstrumentsList.Items.Clear();
        foreach (var inst in _pickLists.Instruments.Order())
            AvailableInstrumentsList.Items.Add(CanonFormat.TitleCase(inst));
    }

    /// <summary>
    /// "Add" button or double-click on available list: adds simple instrument via the editor.
    /// Pre-populates the editor with the selected available instrument if one is highlighted.
    /// </summary>
    private void OnAddInstrumentClick(object sender, RoutedEventArgs e)
    {
        var preselect = AvailableInstrumentsList.SelectedItem as string;
        var seed = preselect != null ? new InstrumentEntry { Instrument = preselect } : null;
        var editor = new InstrumentEntryEditorWindow(_pickLists, seed) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _pieceInstruments.Add(editor.Entry);
            RefreshInstrumentList();
            InstrumentsList.SelectedIndex = InstrumentsList.Items.Count - 1;
        }
    }

    /// <summary>
    /// Right-arrow button or double-click on available list: adds the selected available
    /// instrument directly as a simple entry (no dialog needed for plain names).
    /// </summary>
    private void OnAddFromAvailableClick(object sender, RoutedEventArgs e)
    {
        if (AvailableInstrumentsList.SelectedItem is not string instrument) return;
        _pieceInstruments.Add(new InstrumentEntry { Instrument = instrument });
        RefreshInstrumentList();
        InstrumentsList.SelectedIndex = InstrumentsList.Items.Count - 1;
        // Keep the same available item selected for quick repeated adds
        var idx = AvailableInstrumentsList.Items.IndexOf(instrument);
        if (idx >= 0) AvailableInstrumentsList.SelectedIndex = idx;
    }

    private void OnAvailableInstrumentDoubleClick(object sender, MouseButtonEventArgs e) =>
        OnAddFromAvailableClick(sender, e);

    private void OnEditInstrumentClick(object sender, RoutedEventArgs e) =>
        EditSelectedInstrument();

    private void OnInstrumentDoubleClick(object sender, MouseButtonEventArgs e) =>
        EditSelectedInstrument();

    private void EditSelectedInstrument()
    {
        var idx = InstrumentsList.SelectedIndex;
        if (idx < 0) return;
        var editor = new InstrumentEntryEditorWindow(_pickLists, _pieceInstruments[idx]) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _pieceInstruments[idx] = editor.Entry;
            RefreshInstrumentList();
            InstrumentsList.SelectedIndex = idx;
        }
    }

    private void OnRemoveInstrumentClick(object sender, RoutedEventArgs e)
    {
        var idx = InstrumentsList.SelectedIndex;
        if (idx < 0) return;
        _pieceInstruments.RemoveAt(idx);
        RefreshInstrumentList();
        if (_pieceInstruments.Count > 0)
            InstrumentsList.SelectedIndex = Math.Min(idx, _pieceInstruments.Count - 1);
    }

    private void OnMoveInstrumentUpClick(object sender, RoutedEventArgs e)
    {
        var idx = InstrumentsList.SelectedIndex;
        if (idx <= 0) return;
        (_pieceInstruments[idx], _pieceInstruments[idx - 1]) = (_pieceInstruments[idx - 1], _pieceInstruments[idx]);
        RefreshInstrumentList();
        InstrumentsList.SelectedIndex = idx - 1;
    }

    private void OnMoveInstrumentDownClick(object sender, RoutedEventArgs e)
    {
        var idx = InstrumentsList.SelectedIndex;
        if (idx < 0 || idx >= _pieceInstruments.Count - 1) return;
        (_pieceInstruments[idx], _pieceInstruments[idx + 1]) = (_pieceInstruments[idx + 1], _pieceInstruments[idx]);
        RefreshInstrumentList();
        InstrumentsList.SelectedIndex = idx + 1;
    }

    // --- Version management ---

    private void RefreshVersionList()
    {
        var selectedTag = (VersionList.SelectedItem as ListBoxItem)?.Tag;
        VersionList.Items.Clear();
        foreach (var v in _versions)
        {
            var item = new ListBoxItem { Content = FormatVersionLabel(v), Tag = v };
            VersionList.Items.Add(item);
            if (v == selectedTag) VersionList.SelectedItem = item;
        }
    }

    private static string FormatVersionLabel(CanonPieceVersion v)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(v.Description)) parts.Add(v.Description);
        var cat = v.CatalogInfo?.FirstOrDefault();
        if (cat != null) parts.Add($"{cat.Catalog} {cat.CatalogNumber}".Trim());
        if (!string.IsNullOrEmpty(v.InstrumentationCategory)) parts.Add(v.InstrumentationCategory);
        if (v.PublicationYear.HasValue) parts.Add(v.PublicationYear.Value.ToString());
        if (v.Subpieces is { Count: > 0 }) parts.Add($"({v.Subpieces.Count} mvts)");
        return parts.Count > 0 ? string.Join(" · ", parts) : "(no description)";
    }

    private CanonPieceVersion? SelectedVersion =>
        (VersionList.SelectedItem as ListBoxItem)?.Tag as CanonPieceVersion;

    private void OnAddVersionClick(object sender, RoutedEventArgs e)
    {
        var editor = new VersionEditorWindow(_pickLists) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _versions.Add(editor.Version);
            RefreshVersionList();
        }
    }

    private void OnEditVersionClick(object sender, RoutedEventArgs e) => EditSelectedVersion();

    private void OnVersionDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedVersion();

    private void EditSelectedVersion()
    {
        if (SelectedVersion is not { } v) return;
        var editor = new VersionEditorWindow(_pickLists, v) { Owner = this };
        if (editor.ShowDialog() == true) RefreshVersionList();
    }

    private void OnRemoveVersionClick(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion is not { } v) return;
        _versions.Remove(v);
        RefreshVersionList();
    }

    private void OnMoveVersionUpClick(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion is not { } v) return;
        var idx = _versions.IndexOf(v);
        if (idx <= 0) return;
        (_versions[idx], _versions[idx - 1]) = (_versions[idx - 1], _versions[idx]);
        RefreshVersionList();
    }

    private void OnMoveVersionDownClick(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion is not { } v) return;
        var idx = _versions.IndexOf(v);
        if (idx < 0 || idx >= _versions.Count - 1) return;
        (_versions[idx], _versions[idx + 1]) = (_versions[idx + 1], _versions[idx]);
        RefreshVersionList();
    }

    // --- OK / Pick Lists ---

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        SaveToPiece();
        DialogResult = true;
    }

    private void OnEditPickListsClick(object sender, RoutedEventArgs e)
    {
        var editor = new PickListEditorWindow(_pickLists) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            editor.ApplyTo(_pickLists);
            PopulateDropdowns();

            // Accumulate renames so the caller can propagate them
            MergeRenames(FormRenames, editor.FormRenames);
            MergeRenames(CategoryRenames, editor.CategoryRenames);
            MergeRenames(CatalogRenames, editor.CatalogRenames);
            MergeRenames(KeyRenames, editor.KeyRenames);
            MergeRenames(InstrumentRenames, editor.InstrumentRenames);

            // Refresh the instruments list after pick list changes
            RefreshInstrumentList();
        }
    }

    /// <summary>
    /// Merges new renames into accumulated renames, chaining through prior mappings.
    /// </summary>
    private static void MergeRenames(Dictionary<string, string> accumulated, Dictionary<string, string> incoming)
    {
        foreach (var (oldVal, newVal) in incoming)
        {
            var originalKey = accumulated.FirstOrDefault(r => r.Value == oldVal).Key;
            if (originalKey != null)
                accumulated[originalKey] = newVal;
            else
                accumulated[oldVal] = newVal;
        }
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
