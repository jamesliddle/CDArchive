using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class MovementEditorWindow : Window
{
    private readonly CanonPiece _movement;
    private readonly CanonPickLists _pickLists;
    private readonly List<TempoInfo> _tempos;
    private readonly List<CanonPiece> _subpieces;
    private readonly List<CanonPieceVersion> _versions;
    private readonly List<string> _roles = [];
    private readonly List<CatalogInfo> _catalogEntries = [];
    private readonly List<InstrumentEntry> _pieceInstruments = [];
    private readonly List<string> _textAuthors = [];

    public CanonPiece Movement => _movement;

    public MovementEditorWindow(CanonPickLists pickLists, CanonPiece? movement = null)
    {
        InitializeComponent();

        _pickLists = pickLists;
        _movement = movement ?? new CanonPiece();
        _tempos = _movement.Tempos != null
            ? _movement.Tempos.Select(CloneTempo).ToList()
            : [];
        _subpieces = _movement.Subpieces?.ToList() ?? [];
        _versions = _movement.Versions?.ToList() ?? [];

        Title = movement == null ? "New Subpiece" : "Edit Subpiece";

        FormCombo.ItemsSource = _pickLists.Forms;
        KeyTonalityCombo.ItemsSource = _pickLists.KeyTonalities;
        CatalogPrefixCombo.ItemsSource = _pickLists.CatalogPrefixes;
        CategoryCombo.ItemsSource = _pickLists.Categories;

        LoadFromMovement();
        RefreshTempoList();
        RefreshRoleList();
        RefreshSubpieceList();
        RefreshVersionList();
    }

    private void LoadFromMovement()
    {
        NumberBox.Text        = _movement.Number?.ToString() ?? "";
        MusicNumberBox.Text   = _movement.MusicNumber ?? "";
        FormCombo.Text        = _movement.Form ?? "";
        TitleBox.Text         = _movement.Title ?? "";
        TitleEnglishBox.Text  = _movement.TitleEnglish ?? "";
        SubtitleBox.Text      = _movement.Subtitle ?? "";
        NicknameBox.Text      = _movement.Nickname ?? "";
        KeyTonalityCombo.Text = _movement.KeyTonality ?? "";
        CategoryCombo.Text    = _movement.InstrumentationCategory ?? "";
        PubYearBox.Text       = _movement.PublicationYear?.ToString() ?? "";
        FirstLineBox.Text     = _movement.FirstLine ?? "";

        // Composition years
        CompYearsBox.Text = _movement.CompositionYears?.ValueKind == System.Text.Json.JsonValueKind.String
            ? _movement.CompositionYears.Value.GetString() ?? ""
            : _movement.CompositionYears?.ToString() ?? "";

        NumberedSubpiecesCheck.IsChecked =
            _movement.NumberedSubpieces ?? _movement.EffectiveSubpiecesNumbered;
        SubpiecesStartBox.Text = (_movement.SubpiecesStart ?? 1).ToString();

        // Key mode combo
        var mode = (_movement.KeyMode ?? "").ToLowerInvariant();
        foreach (ComboBoxItem item in KeyModeCombo.Items)
        {
            if ((item.Content as string ?? "") == mode)
            {
                KeyModeCombo.SelectedItem = item;
                break;
            }
        }

        // Catalogue
        if (_movement.CatalogInfo != null)
            _catalogEntries.AddRange(_movement.CatalogInfo);
        RefreshCatalogList();

        // Instrumentation
        if (_movement.Instrumentation.HasValue)
            _pieceInstruments.AddRange(InstrumentEntry.ParseInstrumentation(_movement.Instrumentation.Value));
        RefreshInstrumentList();

        // Text Author
        if (_movement.TextAuthor?.ValueKind == System.Text.Json.JsonValueKind.Array)
            _textAuthors.AddRange(_movement.TextAuthor.Value.EnumerateArray()
                .Select(e => e.GetString() ?? "").Where(s => s.Length > 0));
        else if (_movement.TextAuthor?.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var s = _movement.TextAuthor.Value.GetString();
            if (!string.IsNullOrWhiteSpace(s)) _textAuthors.Add(s.Trim());
        }
        RefreshTextAuthorList();

        // Roles — JSON string array
        if (_movement.Roles?.ValueKind == System.Text.Json.JsonValueKind.Array)
            _roles.AddRange(_movement.Roles.Value.EnumerateArray()
                .Select(e => e.GetString() ?? "").Where(s => s.Length > 0));
        RefreshRoleList();
    }

    private void SaveToMovement()
    {
        _movement.Number        = int.TryParse(NumberBox.Text.Trim(), out var n) ? n : null;
        _movement.MusicNumber   = NullIfEmpty(MusicNumberBox.Text);
        _movement.Form          = NullIfEmpty(FormCombo.Text);
        _movement.Title         = NullIfEmpty(TitleBox.Text);
        _movement.TitleEnglish  = NullIfEmpty(TitleEnglishBox.Text);
        _movement.Subtitle      = NullIfEmpty(SubtitleBox.Text);
        _movement.Nickname      = NullIfEmpty(NicknameBox.Text);
        _movement.KeyTonality   = NullIfEmpty(KeyTonalityCombo.Text);
        _movement.InstrumentationCategory = NullIfEmpty(CategoryCombo.Text);
        _movement.PublicationYear = int.TryParse(PubYearBox.Text.Trim(), out var y) ? y : null;
        _movement.FirstLine     = NullIfEmpty(FirstLineBox.Text);

        var selectedMode = (KeyModeCombo.SelectedItem as ComboBoxItem)?.Content as string;
        _movement.KeyMode = string.IsNullOrEmpty(selectedMode) ? null : selectedMode;

        // Composition years
        var compYears = NullIfEmpty(CompYearsBox.Text);
        _movement.CompositionYears = compYears != null
            ? System.Text.Json.JsonDocument.Parse($"\"{compYears}\"").RootElement.Clone()
            : null;

        // Catalogue
        _movement.CatalogInfo = _catalogEntries.Count > 0 ? _catalogEntries.ToList() : null;

        // Instrumentation
        _movement.Instrumentation = InstrumentEntry.SerializeInstrumentation(_pieceInstruments);

        // Text Author — store as JSON string array
        _movement.TextAuthor = _textAuthors.Count > 0
            ? System.Text.Json.JsonSerializer.SerializeToElement(_textAuthors.ToArray())
            : null;

        // Roles — store as JSON string array
        _movement.Roles = _roles.Count > 0
            ? System.Text.Json.JsonSerializer.SerializeToElement(_roles.ToArray())
            : null;

        _movement.Tempos = _tempos.Count > 0 ? _tempos.ToList() : null;
        _movement.Subpieces = _subpieces.Count > 0 ? _subpieces.ToList() : null;
        _movement.Versions = _versions.Count > 0 ? _versions.ToList() : null;

        var numbered = NumberedSubpiecesCheck.IsChecked == true;
        var defaultNumbered = !string.IsNullOrEmpty(_movement.InstrumentationCategory) &&
            !string.Equals(_movement.InstrumentationCategory, "Opera", StringComparison.OrdinalIgnoreCase);
        _movement.NumberedSubpieces = numbered != defaultNumbered ? numbered : null;

        var start = EffectiveSubpiecesStart;
        _movement.SubpiecesStart = start == 1 ? null : start;
    }

    // --- Role list ---

    private void RefreshRoleList()
    {
        var selected = RoleList.SelectedItem as string;
        RoleList.Items.Clear();
        foreach (var name in _roles)
            RoleList.Items.Add(name);
        if (selected != null && RoleList.Items.Contains(selected))
            RoleList.SelectedItem = selected;
    }

    private void OnAddRoleClick(object sender, RoutedEventArgs e)
    {
        var name = RoleEntryBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        _roles.Add(name);
        RefreshRoleList();
        RoleList.SelectedItem = name;
        RoleEntryBox.Text = "";
        RoleEntryBox.Focus();
    }

    private void OnRemoveRoleClick(object sender, RoutedEventArgs e)
    {
        if (RoleList.SelectedItem is not string name) return;
        _roles.Remove(name);
        RefreshRoleList();
    }

    // --- Catalogue list ---

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

    private void OnAddFromAvailableClick(object sender, RoutedEventArgs e)
    {
        if (AvailableInstrumentsList.SelectedItem is not string instrument) return;
        _pieceInstruments.Add(new InstrumentEntry { Instrument = instrument });
        RefreshInstrumentList();
        InstrumentsList.SelectedIndex = InstrumentsList.Items.Count - 1;
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

    // --- Text Author list ---

    private void RefreshTextAuthorList()
    {
        var selected = TextAuthorList.SelectedItem as string;
        TextAuthorList.Items.Clear();
        foreach (var name in _textAuthors)
            TextAuthorList.Items.Add(name);
        if (selected != null && TextAuthorList.Items.Contains(selected))
            TextAuthorList.SelectedItem = selected;
    }

    private void OnAddTextAuthorClick(object sender, RoutedEventArgs e)
    {
        var name = TextAuthorEntryBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        _textAuthors.Add(name);
        RefreshTextAuthorList();
        TextAuthorList.SelectedItem = name;
        TextAuthorEntryBox.Text = "";
        TextAuthorEntryBox.Focus();
    }

    private void OnRemoveTextAuthorClick(object sender, RoutedEventArgs e)
    {
        if (TextAuthorList.SelectedItem is not string name) return;
        _textAuthors.Remove(name);
        RefreshTextAuthorList();
    }

    // --- Tempo list ---

    private void RefreshTempoList()
    {
        TempoList.Items.Clear();
        foreach (var t in _tempos.OrderBy(t => t.Number))
        {
            var desc = !string.IsNullOrEmpty(t.Description) ? t.Description : "(no description)";
            TempoList.Items.Add(new ListBoxItem
            {
                Content = $"{t.Number}. {desc}",
                Tag = t
            });
        }
    }

    private TempoInfo? SelectedTempo =>
        (TempoList.SelectedItem as ListBoxItem)?.Tag as TempoInfo;

    private void OnAddTempoClick(object sender, RoutedEventArgs e)
    {
        var nextNumber = _tempos.Count > 0 ? _tempos.Max(t => t.Number) + 1 : 1;
        var editor = new TempoEditorWindow(nextNumber) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _tempos.Add(editor.Tempo);
            RefreshTempoList();
        }
    }

    private void OnEditTempoClick(object sender, RoutedEventArgs e) => EditSelectedTempo();

    private void OnTempoDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedTempo();

    private void EditSelectedTempo()
    {
        if (SelectedTempo is not { } tempo) return;
        var editor = new TempoEditorWindow(tempo) { Owner = this };
        if (editor.ShowDialog() == true)
            RefreshTempoList();
    }

    private void OnRemoveTempoClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTempo is not { } tempo) return;
        _tempos.Remove(tempo);
        RefreshTempoList();
    }

    private void OnMoveTempoUpClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTempo is not { } tempo) return;
        var ordered = _tempos.OrderBy(t => t.Number).ToList();
        var idx = ordered.IndexOf(tempo);
        if (idx <= 0) return;
        (ordered[idx].Number, ordered[idx - 1].Number) =
            (ordered[idx - 1].Number, ordered[idx].Number);
        RefreshTempoList();
        SelectTempoByRef(tempo);
    }

    private void OnMoveTempoDownClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTempo is not { } tempo) return;
        var ordered = _tempos.OrderBy(t => t.Number).ToList();
        var idx = ordered.IndexOf(tempo);
        if (idx < 0 || idx >= ordered.Count - 1) return;
        (ordered[idx].Number, ordered[idx + 1].Number) =
            (ordered[idx + 1].Number, ordered[idx].Number);
        RefreshTempoList();
        SelectTempoByRef(tempo);
    }

    private void SelectTempoByRef(TempoInfo tempo)
    {
        foreach (ListBoxItem item in TempoList.Items)
        {
            if (item.Tag == tempo)
            {
                TempoList.SelectedItem = item;
                break;
            }
        }
    }

    // --- Sub-movement management ---

    private void RenumberSubpieces()
    {
        var start = EffectiveSubpiecesStart;
        for (var i = 0; i < _subpieces.Count; i++)
            _subpieces[i].Number = start + i;
    }

    private void RefreshSubpieceList()
    {
        RenumberSubpieces();
        var showNums = NumberedSubpiecesCheck.IsChecked == true;
        var selectedTag = (SubpieceList.SelectedItem as ListBoxItem)?.Tag;
        SubpieceList.Items.Clear();
        foreach (var sp in _subpieces)
        {
            var item = new ListBoxItem { Content = sp.BuildSubpieceTitle(showNums), Tag = sp };
            SubpieceList.Items.Add(item);
            if (sp == selectedTag) SubpieceList.SelectedItem = item;
        }
    }

    private void OnNumberedSubpiecesChanged(object sender, RoutedEventArgs e) =>
        RefreshSubpieceList();

    private void OnSubpiecesStartChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        RefreshSubpieceList();

    private int EffectiveSubpiecesStart =>
        int.TryParse(SubpiecesStartBox?.Text.Trim(), out var s) ? s : 1;

    private CanonPiece? SelectedSubpiece =>
        (SubpieceList.SelectedItem as ListBoxItem)?.Tag as CanonPiece;

    private void OnAddSubpieceClick(object sender, RoutedEventArgs e)
    {
        var newMovement = new CanonPiece { Number = _subpieces.Count + 1 };
        var editor = new MovementEditorWindow(_pickLists, newMovement) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _subpieces.Add(editor.Movement);
            RefreshSubpieceList();
        }
    }

    private void OnEditSubpieceClick(object sender, RoutedEventArgs e) => EditSelectedSubpiece();

    private void OnSubpieceDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedSubpiece();

    private void EditSelectedSubpiece()
    {
        if (SelectedSubpiece is not { } sp) return;
        var editor = new MovementEditorWindow(_pickLists, sp) { Owner = this };
        if (editor.ShowDialog() == true)
            RefreshSubpieceList();
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
        (_subpieces[idx], _subpieces[idx - 1]) = (_subpieces[idx - 1], _subpieces[idx]);
        RefreshSubpieceList();
    }

    private void OnMoveSubpieceDownClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSubpiece is not { } sp) return;
        var idx = _subpieces.IndexOf(sp);
        if (idx < 0 || idx >= _subpieces.Count - 1) return;
        (_subpieces[idx], _subpieces[idx + 1]) = (_subpieces[idx + 1], _subpieces[idx]);
        RefreshSubpieceList();
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
        var editor = new VersionEditorWindow(_pickLists, showSubpieceNumbers: NumberedSubpiecesCheck.IsChecked == true) { Owner = this };
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
        var editor = new VersionEditorWindow(_pickLists, v, showSubpieceNumbers: NumberedSubpiecesCheck.IsChecked == true) { Owner = this };
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

    // --- OK ---

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        SaveToMovement();
        DialogResult = true;
    }

    private static TempoInfo CloneTempo(TempoInfo t) => new()
    {
        Number = t.Number,
        Description = t.Description,
        SubTempos = t.SubTempos?.Select(CloneTempo).ToList()
    };

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
