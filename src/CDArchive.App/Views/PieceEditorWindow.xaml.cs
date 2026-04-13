using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public enum PieceEditorMode { Piece, Subpiece, Version }

public partial class PieceEditorWindow : Window
{
    private readonly CanonPiece _piece;
    private readonly CanonPickLists _pickLists;
    private readonly PieceEditorMode _mode;
    private CanonPieceVersion? _sourceVersion; // non-null when editing a version
    private readonly string? _inheritedComposer;
    private readonly IReadOnlyList<ComposerCredit>? _inheritedComposers;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>>? _composerCatalogs;
    private readonly IReadOnlyList<RoleEntry>? _ancestorRoles;
    private readonly List<ComposerCredit> _composers;
    private readonly List<CanonPiece> _subpieces;
    private readonly List<CanonPieceVersion> _versions;
    private readonly List<RoleEntry> _roles;
    private readonly List<TempoInfo> _tempos;
    private readonly List<VariantInfo> _variants;
    private readonly List<InstrumentEntry> _pieceInstruments = [];
    private readonly List<CatalogInfo> _catalogEntries = [];

    /// <summary>
    /// The piece being edited (or newly created).
    /// </summary>
    public CanonPiece Piece => _piece;

    // ── Piece / Subpiece constructor ─────────────────────────────────────────

    public PieceEditorWindow(
        CanonPickLists pickLists,
        string composerName,
        CanonPiece? piece = null,
        IReadOnlyList<string>? composerNames = null,
        PieceEditorMode mode = PieceEditorMode.Piece,
        string? inheritedComposer = null,
        IReadOnlyList<ComposerCredit>? inheritedComposers = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? composerCatalogs = null,
        IReadOnlyList<RoleEntry>? ancestorRoles = null)
    {
        InitializeComponent();

        _pickLists          = pickLists;
        _mode               = mode;
        _inheritedComposer  = inheritedComposer;
        _inheritedComposers = inheritedComposers;
        _composerCatalogs   = composerCatalogs;
        _ancestorRoles      = ancestorRoles;
        _piece      = piece ?? new CanonPiece { Composer = composerName };
        _composers  = _piece.Composers?.ToList() ?? [];
        _subpieces  = _piece.Subpieces?.ToList() ?? [];
        _versions   = _piece.Versions?.ToList() ?? [];
        _roles      = _piece.Roles.HasValue ? RoleEntry.ParseRoles(_piece.Roles.Value) : [];
        _tempos     = _piece.Tempos?.Select(CloneTempo).ToList() ?? [];
        _variants   = _piece.Variants?.Select(CloneVariant).ToList() ?? [];

        Title = BuildTitle(mode, piece == null);

        ComposerCombo.ItemsSource = composerNames ?? [];
        ComposerCombo.SelectionChanged += (_, _) => UpdateCatalogPrefixDropdown();

        PopulateDropdowns();
        LoadFromPiece();
        RefreshTempoList();
        RefreshSubpieceList();
        RefreshVersionList();
        RefreshRoleList();
        RefreshVariantList();
    }

    // ── Version constructor ───────────────────────────────────────────────────

    public PieceEditorWindow(
        CanonPickLists pickLists,
        CanonPieceVersion? version,
        bool showSubpieceNumbers = true,
        IReadOnlyList<string>? composerNames = null,
        string? inheritedComposer = null,
        IReadOnlyList<ComposerCredit>? inheritedComposers = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? composerCatalogs = null,
        IReadOnlyList<RoleEntry>? ancestorRoles = null)
    {
        InitializeComponent();

        _pickLists          = pickLists;
        _mode               = PieceEditorMode.Version;
        _inheritedComposer  = inheritedComposer;
        _inheritedComposers = inheritedComposers;
        _composerCatalogs   = composerCatalogs;
        _ancestorRoles      = ancestorRoles;
        _sourceVersion = version ?? new CanonPieceVersion();
        _piece         = VersionToPiece(_sourceVersion, showSubpieceNumbers);
        _composers     = _piece.Composers?.ToList() ?? [];
        _subpieces     = _piece.Subpieces?.ToList() ?? [];
        _versions      = [];  // versions cannot have nested versions
        _roles         = _piece.Roles.HasValue ? RoleEntry.ParseRoles(_piece.Roles.Value) : [];
        _tempos        = _piece.Tempos?.Select(CloneTempo).ToList() ?? [];
        _variants      = _piece.Variants?.Select(CloneVariant).ToList() ?? [];

        Title = BuildTitle(PieceEditorMode.Version, version == null);

        // Show Version Description; hide the Versions section (not applicable)
        VersionDescriptionSection.Visibility = Visibility.Visible;
        VersionDescriptionBox.Text = _sourceVersion.Description ?? "";
        VersionsSectionHeader.Visibility = Visibility.Collapsed;
        VersionsSectionPanel.Visibility  = Visibility.Collapsed;

        ComposerCombo.ItemsSource = composerNames ?? [];
        ComposerCombo.SelectionChanged += (_, _) => UpdateCatalogPrefixDropdown();

        PopulateDropdowns();
        LoadFromPiece();
        RefreshTempoList();
        RefreshSubpieceList();
        RefreshVersionList();
        RefreshRoleList();
        RefreshVariantList();
    }

    // ── Constructor helpers ───────────────────────────────────────────────────

    private static string BuildTitle(PieceEditorMode mode, bool isNew) => mode switch
    {
        PieceEditorMode.Subpiece => isNew ? "New Subpiece" : "Edit Subpiece",
        PieceEditorMode.Version  => isNew ? "New Version"  : "Edit Version",
        _                        => isNew ? "New Piece"    : "Edit Piece",
    };

    /// <summary>
    /// Converts a <see cref="CanonPieceVersion"/> into a transient <see cref="CanonPiece"/>
    /// so the unified editor can work with it unchanged.
    /// </summary>
    private static CanonPiece VersionToPiece(CanonPieceVersion v, bool showSubpieceNumbers)
    {
        // Use showSubpieceNumbers as the default only when the version has no explicit override.
        bool? numberedOverride = v.NumberedSubpieces;
        return new CanonPiece
        {
            Composer               = v.Composer,
            Composers              = v.Composers?.ToList(),
            Form                   = v.Form,
            Title                  = v.Title,
            TitleEnglish           = v.TitleEnglish,
            Subtitle               = v.Subtitle,
            Nickname               = v.Nickname,
            Number                 = v.Number,
            MusicNumber            = v.MusicNumber,
            KeyTonality            = v.KeyTonality,
            KeyMode                = v.KeyMode,
            CatalogInfo            = v.CatalogInfo?.ToList(),
            InstrumentationCategory= v.InstrumentationCategory,
            Instrumentation        = v.Instrumentation,
            PublicationYear        = v.PublicationYear,
            CompositionYears       = v.CompositionYears,
            // Preserve explicit override; if null, seed from parent's default so the
            // checkbox shows the right value.
            NumberedSubpieces      = numberedOverride ?? (showSubpieceNumbers ? null : false),
            SubpiecesStart         = v.SubpiecesStart,
            FirstLine              = v.FirstLine,
            Notes                  = v.Notes,
            Variants               = v.Variants?.ToList(),
            Roles                  = v.Roles,
            Tempos                 = v.Tempos?.ToList(),
            Subpieces              = v.Subpieces?.ToList(),
        };
    }

    /// <summary>
    /// Copies the edited <see cref="_piece"/> back into the source
    /// <see cref="CanonPieceVersion"/> after the user clicks OK.
    /// </summary>
    private void CopyPieceToVersion()
    {
        var v = _sourceVersion!;
        v.Description           = NullIfEmpty(VersionDescriptionBox.Text);
        v.Composer              = _piece.Composer;
        v.Composers             = _piece.Composers;
        v.Form                  = _piece.Form;
        v.Title                 = _piece.Title;
        v.TitleEnglish          = _piece.TitleEnglish;
        v.Subtitle              = _piece.Subtitle;
        v.Nickname              = _piece.Nickname;
        v.Number                = _piece.Number;
        v.MusicNumber           = _piece.MusicNumber;
        v.KeyTonality           = _piece.KeyTonality;
        v.KeyMode               = _piece.KeyMode;
        v.CatalogInfo           = _piece.CatalogInfo;
        v.InstrumentationCategory = _piece.InstrumentationCategory;
        v.Instrumentation       = _piece.Instrumentation;
        v.PublicationYear       = _piece.PublicationYear;
        v.CompositionYears      = _piece.CompositionYears;
        v.NumberedSubpieces     = _piece.NumberedSubpieces;
        v.SubpiecesStart        = _piece.SubpiecesStart;
        v.FirstLine             = _piece.FirstLine;
        v.Notes                 = _piece.Notes;
        v.Variants              = _piece.Variants;
        v.Roles                 = _piece.Roles;
        v.Tempos                = _piece.Tempos;
        v.Subpieces             = _piece.Subpieces;
    }

    private void PopulateDropdowns()
    {
        FormCombo.ItemsSource        = _pickLists.Forms;
        KeyTonalityCombo.ItemsSource = _pickLists.KeyTonalities;
        CategoryCombo.ItemsSource    = _pickLists.Categories;
        UpdateCatalogPrefixDropdown();
    }

    /// <summary>
    /// Rebuilds the catalogue prefix dropdown to show only prefixes permitted for
    /// the currently selected composer. Falls back to the full pick-list when the
    /// composer has no restrictions defined.
    /// </summary>
    private void UpdateCatalogPrefixDropdown()
    {
        var composerName = ComposerCombo.Text?.Trim() ?? "";
        IReadOnlyList<string> prefixes = _pickLists.CatalogPrefixes;

        if (_composerCatalogs != null
            && !string.IsNullOrEmpty(composerName)
            && _composerCatalogs.TryGetValue(composerName, out var permitted)
            && permitted.Count > 0)
        {
            var permittedSet = new HashSet<string>(permitted, StringComparer.OrdinalIgnoreCase);
            prefixes = _pickLists.CatalogPrefixes
                .Where(p => permittedSet.Contains(p))
                .ToList();
        }

        // Preserve the current text across the reset
        var current = CatalogPrefixCombo.Text;
        CatalogPrefixCombo.ItemsSource = prefixes;
        CatalogPrefixCombo.Text = current;
    }

    private void LoadFromPiece()
    {
        ComposerCombo.Text = _piece.Composer ?? _inheritedComposer ?? "";

        // Seed Other contributors from parent if this piece/subpiece/version has none of its own.
        if (_composers.Count == 0 && _inheritedComposers != null)
            _composers.AddRange(_inheritedComposers);
        RefreshComposerCreditList();

        FormCombo.Text = _piece.Form ?? "";
        TitleBox.Text = _piece.Title ?? "";
        TitleEnglishBox.Text = _piece.TitleEnglish ?? "";
        SubtitleBox.Text = _piece.Subtitle ?? "";
        NicknameBox.Text = _piece.Nickname ?? "";
        NumberBox.Text = _piece.Number?.ToString() ?? "";
        MusicNumberBox.Text = _piece.MusicNumber ?? "";
        KeyTonalityCombo.Text = _piece.KeyTonality ?? "";
        CategoryCombo.Text = _piece.InstrumentationCategory ?? "";
        NumberedSubpiecesCheck.IsChecked = _piece.NumberedSubpieces ?? _piece.EffectiveSubpiecesNumbered;
        SubpiecesStartBox.Text = (_piece.SubpiecesStart ?? 1).ToString();
        PubYearBox.Text = _piece.PublicationYear?.ToString() ?? "";
        FirstLineBox.Text = _piece.FirstLine ?? "";
        NotesBox.Text = _piece.Notes ?? "";

        // Composition years (stored as a JSON string value)
        CompYearsBox.Text = _piece.CompositionYears?.ValueKind == System.Text.Json.JsonValueKind.String
            ? _piece.CompositionYears.Value.GetString() ?? ""
            : _piece.CompositionYears?.ToString() ?? "";

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
        _piece.Composer = NullIfEmpty(ComposerCombo.Text);
        _piece.Composers = _composers.Count > 0 ? _composers.ToList() : null;
        _piece.Form = NullIfEmpty(FormCombo.Text);
        _piece.Title = NullIfEmpty(TitleBox.Text);
        _piece.TitleEnglish = NullIfEmpty(TitleEnglishBox.Text);
        _piece.Subtitle = NullIfEmpty(SubtitleBox.Text);
        _piece.Nickname = NullIfEmpty(NicknameBox.Text);
        _piece.InstrumentationCategory = NullIfEmpty(CategoryCombo.Text);
        _piece.KeyTonality = NullIfEmpty(KeyTonalityCombo.Text);

        _piece.Number = int.TryParse(NumberBox.Text.Trim(), out var n) ? n : null;
        _piece.MusicNumber = NullIfEmpty(MusicNumberBox.Text);
        _piece.PublicationYear = int.TryParse(PubYearBox.Text.Trim(), out var y) ? y : null;
        _piece.FirstLine = NullIfEmpty(FirstLineBox.Text);
        _piece.Notes = NullIfEmpty(NotesBox.Text);

        var selectedMode = (KeyModeCombo.SelectedItem as ComboBoxItem)?.Content as string;
        _piece.KeyMode = string.IsNullOrEmpty(selectedMode) ? null : selectedMode;

        // Composition years
        var compYears = NullIfEmpty(CompYearsBox.Text);
        _piece.CompositionYears = compYears != null
            ? System.Text.Json.JsonDocument.Parse($"\"{compYears}\"").RootElement.Clone()
            : null;


        // Catalog info — all entries from the list
        _piece.CatalogInfo = _catalogEntries.Count > 0 ? _catalogEntries.ToList() : null;

        // Instrumentation
        _piece.Instrumentation = InstrumentEntry.SerializeInstrumentation(_pieceInstruments);

        // Numbered subpieces — save null when the value matches the category-based default
        // so the JSON stays clean for the common case.
        var numbered = NumberedSubpiecesCheck.IsChecked == true;
        var cat = NullIfEmpty(CategoryCombo.Text);
        var defaultNumbered = !string.IsNullOrEmpty(cat) &&
            !string.Equals(cat, "Opera", StringComparison.OrdinalIgnoreCase);
        _piece.NumberedSubpieces = numbered != defaultNumbered ? numbered : null;

        // Subpieces start — save null when 1 (the default)
        var start = EffectiveSubpiecesStart;
        _piece.SubpiecesStart = start == 1 ? null : start;

        // Subpieces
        _piece.Subpieces = _subpieces.Count > 0 ? _subpieces.ToList() : null;

        // Versions
        _piece.Versions = _versions.Count > 0 ? _versions.ToList() : null;

        // Tempos
        _piece.Tempos = _tempos.Count > 0 ? _tempos.ToList() : null;

        // Roles
        _piece.Roles = RoleEntry.SerializeRoles(_roles);

        // Variants
        _piece.Variants = _variants.Count > 0 ? _variants.ToList() : null;
    }

    // --- Composer credit management ---

    private void RefreshComposerCreditList()
    {
        var selected = (ComposerCreditList.SelectedItem as ListBoxItem)?.Tag;
        ComposerCreditList.Items.Clear();
        foreach (var credit in _composers)
        {
            var item = new ListBoxItem { Content = credit.DisplayLabel, Tag = credit };
            ComposerCreditList.Items.Add(item);
            if (credit == selected) ComposerCreditList.SelectedItem = item;
        }
    }

    private ComposerCredit? SelectedComposerCredit =>
        (ComposerCreditList.SelectedItem as ListBoxItem)?.Tag as ComposerCredit;

    private void OnAddComposerCreditClick(object sender, RoutedEventArgs e)
    {
        var composerNames = (ComposerCombo.ItemsSource as IReadOnlyList<string>) ?? [];
        var editor = new ComposerCreditEditorWindow(composerNames, _pickLists.CreativeRoles)
        {
            Owner = this
        };
        if (editor.ShowDialog() == true)
        {
            _composers.Add(editor.Credit);
            RefreshComposerCreditList();
        }
    }

    private void OnEditComposerCreditClick(object sender, RoutedEventArgs e) =>
        EditSelectedComposerCredit();

    private void OnComposerCreditDoubleClick(object sender, MouseButtonEventArgs e) =>
        EditSelectedComposerCredit();

    private void EditSelectedComposerCredit()
    {
        if (SelectedComposerCredit is not { } credit) return;
        var composerNames = (ComposerCombo.ItemsSource as IReadOnlyList<string>) ?? [];
        var editor = new ComposerCreditEditorWindow(composerNames, _pickLists.CreativeRoles, credit)
        {
            Owner = this
        };
        if (editor.ShowDialog() == true)
        {
            var idx = _composers.IndexOf(credit);
            _composers[idx] = editor.Credit;
            RefreshComposerCreditList();
        }
    }

    private void OnRemoveComposerCreditClick(object sender, RoutedEventArgs e)
    {
        if (SelectedComposerCredit is not { } credit) return;
        _composers.Remove(credit);
        RefreshComposerCreditList();
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
            if (sp == selectedTag)
                SubpieceList.SelectedItem = item;
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
        var newSubpiece = new CanonPiece { Number = _subpieces.Count + 1 };
        var editor = new PieceEditorWindow(
            _pickLists, "", newSubpiece,
            ComposerCombo.ItemsSource as IReadOnlyList<string>, PieceEditorMode.Subpiece,
            inheritedComposer: ComposerCombo.Text,
            inheritedComposers: _composers.Count > 0 ? _composers : null,
            composerCatalogs: _composerCatalogs,
            ancestorRoles: AncestorRolesForChildren()) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _subpieces.Add(editor.Piece);
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
        var editor = new PieceEditorWindow(
            _pickLists, sp.Composer ?? "", sp,
            ComposerCombo.ItemsSource as IReadOnlyList<string>, PieceEditorMode.Subpiece,
            inheritedComposer: ComposerCombo.Text,
            inheritedComposers: _composers.Count > 0 ? _composers : null,
            composerCatalogs: _composerCatalogs,
            ancestorRoles: AncestorRolesForChildren()) { Owner = this };
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
        // When ancestor roles are available (editing a subpiece), present the cast
        // picker so the user can select from roles defined in the parent piece.
        if (_ancestorRoles is { Count: > 0 })
        {
            var picker = new RolePickerWindow(_ancestorRoles, _roles) { Owner = this };
            if (picker.ShowDialog() == true && picker.SelectedRoles.Count > 0)
            {
                // Add as name-only references — the full definition lives on the parent piece.
                foreach (var r in picker.SelectedRoles)
                    _roles.Add(new RoleEntry { Name = r.Name });
                RefreshRoleList();
            }
            return;
        }

        // No ancestor roles — free-form entry (top-level piece cast definition).
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

    // --- Variant management ---

    private void RefreshVariantList()
    {
        var selectedTag = (VariantList.SelectedItem as ListBoxItem)?.Tag;
        VariantList.Items.Clear();
        foreach (var v in _variants)
        {
            var item = new ListBoxItem { Content = v.Description, Tag = v };
            VariantList.Items.Add(item);
            if (v == selectedTag) VariantList.SelectedItem = item;
        }
    }

    private VariantInfo? SelectedVariant =>
        (VariantList.SelectedItem as ListBoxItem)?.Tag as VariantInfo;

    private void OnAddVariantClick(object sender, RoutedEventArgs e)
    {
        var editor = new VariantEditorWindow { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _variants.Add(editor.Variant);
            RefreshVariantList();
        }
    }

    private void OnEditVariantClick(object sender, RoutedEventArgs e) => EditSelectedVariant();

    private void OnVariantDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedVariant();

    private void EditSelectedVariant()
    {
        if (SelectedVariant is not { } variant) return;
        var editor = new VariantEditorWindow(variant) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            var idx = _variants.IndexOf(variant);
            _variants[idx] = editor.Variant;
            RefreshVariantList();
        }
    }

    private void OnRemoveVariantClick(object sender, RoutedEventArgs e)
    {
        if (SelectedVariant is not { } variant) return;
        _variants.Remove(variant);
        RefreshVariantList();
    }

    private void OnMoveVariantUpClick(object sender, RoutedEventArgs e)
    {
        if (SelectedVariant is not { } variant) return;
        var idx = _variants.IndexOf(variant);
        if (idx <= 0) return;
        (_variants[idx], _variants[idx - 1]) = (_variants[idx - 1], _variants[idx]);
        RefreshVariantList();
    }

    private void OnMoveVariantDownClick(object sender, RoutedEventArgs e)
    {
        if (SelectedVariant is not { } variant) return;
        var idx = _variants.IndexOf(variant);
        if (idx < 0 || idx >= _variants.Count - 1) return;
        (_variants[idx], _variants[idx + 1]) = (_variants[idx + 1], _variants[idx]);
        RefreshVariantList();
    }

    private static VariantInfo CloneVariant(VariantInfo v) => new()
    {
        Description     = v.Description,
        LongDescription = v.LongDescription,
    };

    // --- Tempo management ---

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

    private static TempoInfo CloneTempo(TempoInfo t) => new()
    {
        Number = t.Number,
        Description = t.Description,
        SubTempos = t.SubTempos?.Select(CloneTempo).ToList()
    };

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
        // Show ensembles first, then individual instruments
        if (_pickLists.Ensembles is { Count: > 0 })
        {
            foreach (var ens in _pickLists.Ensembles.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                AvailableInstrumentsList.Items.Add($"\u266B {ens.Name}");
            AvailableInstrumentsList.Items.Add("───────────");
        }
        foreach (var inst in _pickLists.Instruments.Order())
            AvailableInstrumentsList.Items.Add(CanonFormat.TitleCase(inst));
    }

    /// <summary>
    /// Finds the EnsembleDefinition for an available-list item prefixed with the ensemble marker.
    /// Returns null if the item is a plain instrument.
    /// </summary>
    private EnsembleDefinition? GetEnsembleFromAvailableItem(string? item)
    {
        if (item == null || !item.StartsWith("\u266B ")) return null;
        var name = item[2..]; // strip "♫ " prefix
        return _pickLists.Ensembles?.FirstOrDefault(e =>
            string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// "Add" button: opens the instrument editor (or ensemble editor if an ensemble is selected).
    /// Pre-populates the editor with the selected available instrument if one is highlighted.
    /// </summary>
    private void OnAddInstrumentClick(object sender, RoutedEventArgs e)
    {
        var preselect = AvailableInstrumentsList.SelectedItem as string;
        var ensemble = GetEnsembleFromAvailableItem(preselect);

        if (ensemble != null)
        {
            var entry = new InstrumentEntry { Instrument = ensemble.Name, IsEnsemble = true };
            if (!ensemble.IsFixed)
            {
                var ensEditor = new EnsembleEntryEditorWindow(_pickLists, entry) { Owner = this };
                if (ensEditor.ShowDialog() != true) return;
                entry = ensEditor.Entry;
            }
            _pieceInstruments.Add(entry);
            RefreshInstrumentList();
            InstrumentsList.SelectedIndex = InstrumentsList.Items.Count - 1;
            return;
        }

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
    /// For variable ensembles, opens an editor to specify members.
    /// </summary>
    private void OnAddFromAvailableClick(object sender, RoutedEventArgs e)
    {
        if (AvailableInstrumentsList.SelectedItem is not string item) return;
        if (item.StartsWith("───")) return; // separator

        var ensemble = GetEnsembleFromAvailableItem(item);
        if (ensemble != null)
        {
            var entry = new InstrumentEntry { Instrument = ensemble.Name, IsEnsemble = true };
            if (!ensemble.IsFixed)
            {
                // Variable ensemble — open editor for members
                var editor = new EnsembleEntryEditorWindow(_pickLists, entry) { Owner = this };
                if (editor.ShowDialog() != true) return;
                entry = editor.Entry;
            }
            _pieceInstruments.Add(entry);
        }
        else
        {
            _pieceInstruments.Add(new InstrumentEntry { Instrument = item });
        }

        RefreshInstrumentList();
        InstrumentsList.SelectedIndex = InstrumentsList.Items.Count - 1;
        var idx = AvailableInstrumentsList.Items.IndexOf(item);
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

        var current = _pieceInstruments[idx];
        if (current.IsEnsemble)
        {
            var def = _pickLists.Ensembles?.FirstOrDefault(e =>
                string.Equals(e.Name, current.Instrument, StringComparison.OrdinalIgnoreCase));
            // Fixed ensembles have nothing to edit
            if (def?.IsFixed == true) return;

            var ensEditor = new EnsembleEntryEditorWindow(_pickLists, current) { Owner = this };
            if (ensEditor.ShowDialog() == true)
            {
                _pieceInstruments[idx] = ensEditor.Entry;
                RefreshInstrumentList();
                InstrumentsList.SelectedIndex = idx;
            }
            return;
        }

        var editor = new InstrumentEntryEditorWindow(_pickLists, current) { Owner = this };
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

    /// <summary>
    /// Returns the role list that should be passed as <c>ancestorRoles</c> when
    /// opening a subpiece editor from within this window.
    /// <list type="bullet">
    ///   <item>If we already have ancestor roles (we are a subpiece), pass them through unchanged.</item>
    ///   <item>If we are a top-level piece that has roles defined, our roles become the ancestors for our subpieces.</item>
    ///   <item>Otherwise return null (no restriction).</item>
    /// </list>
    /// </summary>
    private IReadOnlyList<RoleEntry>? AncestorRolesForChildren() =>
        _ancestorRoles ?? (_roles.Count > 0 ? (IReadOnlyList<RoleEntry>)_roles : null);

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
        var newVersion = new CanonPieceVersion();
        var editor = new PieceEditorWindow(
            _pickLists, newVersion,
            showSubpieceNumbers: NumberedSubpiecesCheck.IsChecked == true,
            composerNames: ComposerCombo.ItemsSource as IReadOnlyList<string>,
            inheritedComposer: ComposerCombo.Text,
            inheritedComposers: _composers.Count > 0 ? _composers : null,
            composerCatalogs: _composerCatalogs) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _versions.Add(newVersion);
            RefreshVersionList();
        }
    }

    private void OnEditVersionClick(object sender, RoutedEventArgs e) => EditSelectedVersion();

    private void OnVersionDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedVersion();

    private void EditSelectedVersion()
    {
        if (SelectedVersion is not { } v) return;
        var editor = new PieceEditorWindow(
            _pickLists, v,
            showSubpieceNumbers: NumberedSubpiecesCheck.IsChecked == true,
            composerNames: ComposerCombo.ItemsSource as IReadOnlyList<string>,
            inheritedComposer: ComposerCombo.Text,
            inheritedComposers: _composers.Count > 0 ? _composers : null,
            composerCatalogs: _composerCatalogs) { Owner = this };
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
        if (_mode == PieceEditorMode.Version)
            CopyPieceToVersion();
        DialogResult = true;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
