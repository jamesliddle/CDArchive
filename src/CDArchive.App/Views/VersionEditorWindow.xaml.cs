using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class VersionEditorWindow : Window
{
    private readonly CanonPickLists _pickLists;
    private readonly CanonPieceVersion _version;
    private readonly List<CanonPiece> _subpieces;
    private readonly List<TempoInfo> _tempos;
    private readonly List<CatalogInfo> _catalogEntries = [];
    private readonly List<InstrumentEntry> _pieceInstruments = [];
    private readonly List<string> _textAuthors = [];
    private readonly List<string> _roles = [];
    private bool _showSubpieceNumbers;

    public CanonPieceVersion Version => _version;

    public VersionEditorWindow(CanonPickLists pickLists, CanonPieceVersion? version = null,
        bool showSubpieceNumbers = true)
    {
        InitializeComponent();

        _pickLists = pickLists;
        _version = version ?? new CanonPieceVersion();
        _subpieces = _version.Subpieces?.ToList() ?? [];
        _tempos = _version.Tempos != null
            ? _version.Tempos.Select(CloneTempo).ToList()
            : [];
        _showSubpieceNumbers = showSubpieceNumbers;

        Title = version == null ? "New Version" : "Edit Version";

        FormCombo.ItemsSource = _pickLists.Forms;
        KeyTonalityCombo.ItemsSource = _pickLists.KeyTonalities;
        CatalogPrefixCombo.ItemsSource = _pickLists.CatalogPrefixes;
        CategoryCombo.ItemsSource = _pickLists.Categories;

        LoadFromVersion();
        RefreshTempoList();
        RefreshSubpieceList();
    }

    private void LoadFromVersion()
    {
        DescriptionBox.Text   = _version.Description ?? "";
        FormCombo.Text        = _version.Form ?? "";
        TitleBox.Text         = _version.Title ?? "";
        TitleEnglishBox.Text  = _version.TitleEnglish ?? "";
        SubtitleBox.Text      = _version.Subtitle ?? "";
        NicknameBox.Text      = _version.Nickname ?? "";
        NumberBox.Text        = _version.Number?.ToString() ?? "";
        MusicNumberBox.Text   = _version.MusicNumber ?? "";
        KeyTonalityCombo.Text = _version.KeyTonality ?? "";
        CategoryCombo.Text    = _version.InstrumentationCategory ?? "";
        PubYearBox.Text       = _version.PublicationYear?.ToString() ?? "";
        FirstLineBox.Text     = _version.FirstLine ?? "";

        // Composition years
        CompYearsBox.Text = _version.CompositionYears?.ValueKind == System.Text.Json.JsonValueKind.String
            ? _version.CompositionYears.Value.GetString() ?? ""
            : _version.CompositionYears?.ToString() ?? "";

        NumberedSubpiecesCheck.IsChecked = _version.NumberedSubpieces ?? _showSubpieceNumbers;
        SubpiecesStartBox.Text = (_version.SubpiecesStart ?? 1).ToString();

        // Key mode combo
        var mode = (_version.KeyMode ?? "").ToLowerInvariant();
        foreach (ComboBoxItem item in KeyModeCombo.Items)
        {
            if ((item.Content as string ?? "") == mode)
            {
                KeyModeCombo.SelectedItem = item;
                break;
            }
        }

        // Catalogue
        if (_version.CatalogInfo != null)
            _catalogEntries.AddRange(_version.CatalogInfo);
        RefreshCatalogList();

        // Instrumentation
        if (_version.Instrumentation.HasValue)
            _pieceInstruments.AddRange(InstrumentEntry.ParseInstrumentation(_version.Instrumentation.Value));
        RefreshInstrumentList();

        // Text Author
        if (_version.TextAuthor?.ValueKind == System.Text.Json.JsonValueKind.Array)
            _textAuthors.AddRange(_version.TextAuthor.Value.EnumerateArray()
                .Select(e => e.GetString() ?? "").Where(s => s.Length > 0));
        else if (_version.TextAuthor?.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var s = _version.TextAuthor.Value.GetString();
            if (!string.IsNullOrWhiteSpace(s)) _textAuthors.Add(s.Trim());
        }
        RefreshTextAuthorList();

        // Roles
        if (_version.Roles?.ValueKind == System.Text.Json.JsonValueKind.Array)
            _roles.AddRange(_version.Roles.Value.EnumerateArray()
                .Select(e => e.GetString() ?? "").Where(s => s.Length > 0));
        RefreshRoleList();
    }

    private void SaveToVersion()
    {
        _version.Description  = NullIfEmpty(DescriptionBox.Text);
        _version.Form         = NullIfEmpty(FormCombo.Text);
        _version.Title        = NullIfEmpty(TitleBox.Text);
        _version.TitleEnglish = NullIfEmpty(TitleEnglishBox.Text);
        _version.Subtitle     = NullIfEmpty(SubtitleBox.Text);
        _version.Nickname     = NullIfEmpty(NicknameBox.Text);
        _version.Number       = int.TryParse(NumberBox.Text.Trim(), out var n) ? n : null;
        _version.MusicNumber  = NullIfEmpty(MusicNumberBox.Text);
        _version.KeyTonality  = NullIfEmpty(KeyTonalityCombo.Text);
        _version.InstrumentationCategory = NullIfEmpty(CategoryCombo.Text);
        _version.PublicationYear = int.TryParse(PubYearBox.Text.Trim(), out var y) ? y : null;
        _version.FirstLine    = NullIfEmpty(FirstLineBox.Text);

        var selectedMode = (KeyModeCombo.SelectedItem as ComboBoxItem)?.Content as string;
        _version.KeyMode = string.IsNullOrEmpty(selectedMode) ? null : selectedMode;

        // Composition years
        var compYears = NullIfEmpty(CompYearsBox.Text);
        _version.CompositionYears = compYears != null
            ? System.Text.Json.JsonDocument.Parse($"\"{compYears}\"").RootElement.Clone()
            : null;

        // Numbered subpieces
        var numbered = NumberedSubpiecesCheck.IsChecked == true;
        _version.NumberedSubpieces = numbered != _showSubpieceNumbers ? numbered : null;

        var start = EffectiveSubpiecesStart;
        _version.SubpiecesStart = start == 1 ? null : start;

        // Catalogue
        _version.CatalogInfo = _catalogEntries.Count > 0 ? _catalogEntries.ToList() : null;

        // Instrumentation
        _version.Instrumentation = InstrumentEntry.SerializeInstrumentation(_pieceInstruments);

        // Text Author — store as JSON string array
        _version.TextAuthor = _textAuthors.Count > 0
            ? System.Text.Json.JsonSerializer.SerializeToElement(_textAuthors.ToArray())
            : null;

        // Roles — store as JSON string array
        _version.Roles = _roles.Count > 0
            ? System.Text.Json.JsonSerializer.SerializeToElement(_roles.ToArray())
            : null;

        _version.Tempos   = _tempos.Count > 0 ? _tempos.ToList() : null;
        _version.Subpieces = _subpieces.Count > 0 ? _subpieces.ToList() : null;
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

    // --- Tempo list ---

    private void RefreshTempoList()
    {
        TempoList.Items.Clear();
        foreach (var t in _tempos.OrderBy(t => t.Number))
        {
            var desc = !string.IsNullOrEmpty(t.Description) ? t.Description : "(no description)";
            TempoList.Items.Add(new ListBoxItem { Content = $"{t.Number}. {desc}", Tag = t });
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
        if (editor.ShowDialog() == true) RefreshTempoList();
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
        (ordered[idx].Number, ordered[idx - 1].Number) = (ordered[idx - 1].Number, ordered[idx].Number);
        RefreshTempoList();
        SelectTempoByRef(tempo);
    }

    private void OnMoveTempoDownClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTempo is not { } tempo) return;
        var ordered = _tempos.OrderBy(t => t.Number).ToList();
        var idx = ordered.IndexOf(tempo);
        if (idx < 0 || idx >= ordered.Count - 1) return;
        (ordered[idx].Number, ordered[idx + 1].Number) = (ordered[idx + 1].Number, ordered[idx].Number);
        RefreshTempoList();
        SelectTempoByRef(tempo);
    }

    private void SelectTempoByRef(TempoInfo tempo)
    {
        foreach (ListBoxItem item in TempoList.Items)
        {
            if (item.Tag == tempo) { TempoList.SelectedItem = item; break; }
        }
    }

    // --- Subpiece management ---

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
        if (editor.ShowDialog() == true) RefreshSubpieceList();
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

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        SaveToVersion();
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
