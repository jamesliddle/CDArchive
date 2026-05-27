using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDArchive.App.ViewModels;
using CDArchive.Core.Helpers;
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
    // H13 PieceEditor slice 4: all 8 list-shaped fields moved onto
    // PieceEditorViewModel as ObservableCollection<T>. Reference them via
    // _vm.Composers / _vm.CatalogEntries / _vm.PieceInstruments /
    // _vm.Subpieces / _vm.Versions / _vm.Roles / _vm.Markers / _vm.Variants
    // throughout the code-behind.
    //
    // Markers note (preserved from pre-fix): the items in _vm.Markers are the
    // actual MusicalMarker instances from the piece, not clones, so stable Ids
    // stay attached to whatever album-track refs already point at them. The
    // VM's CloneVariant deep-copy applies ONLY to Variants.

    // H13 PieceEditor slice 1: 10 simple text fields move to PieceEditorViewModel.
    // XAML TwoWay-binds to _vm.X (no MixedField — PieceEditor has no multi-edit).
    // Combobox + checkbox + list-shaped fields stay in code-behind; later slices
    // migrate them.
    private readonly PieceEditorViewModel _vm = new();

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
        DataContext = _vm;

        _pickLists          = pickLists;
        _mode               = mode;
        _inheritedComposer  = inheritedComposer;
        _inheritedComposers = inheritedComposers;
        _composerCatalogs   = composerCatalogs;
        _ancestorRoles      = ancestorRoles;
        _piece      = piece ?? new CanonPiece { Composer = composerName };
        // H13 PieceEditor slice 4: list population moves into the VM via
        // LoadFromPiece → LoadListsFromPiece. The ctor's 6 list-init lines
        // (Composers / Subpieces / Versions / Roles / Markers / Variants)
        // retired here.

        Title = BuildTitle(mode, piece == null);

        ComposerCombo.ItemsSource = composerNames ?? [];
        ComposerCombo.SelectionChanged += (_, _) => UpdateCatalogPrefixDropdown();

        PopulateDropdowns();
        LoadFromPiece();
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
        DataContext = _vm;

        _pickLists          = pickLists;
        _mode               = PieceEditorMode.Version;
        _inheritedComposer  = inheritedComposer;
        _inheritedComposers = inheritedComposers;
        _composerCatalogs   = composerCatalogs;
        _ancestorRoles      = ancestorRoles;
        _sourceVersion = version ?? new CanonPieceVersion();
        _piece         = VersionToPiece(_sourceVersion, showSubpieceNumbers);
        // H13 PieceEditor slice 4: list population moves into the VM via
        // LoadFromPiece → LoadListsFromPiece. Versions list auto-empties for
        // a version-mode load because PieceVersionShuttle.FromVersion excludes
        // CanonPiece.Versions (versions of a version aren't a thing) so the
        // resulting _piece.Versions is null.

        Title = BuildTitle(PieceEditorMode.Version, version == null);

        // Show Version Description; hide the Versions section (not applicable)
        VersionDescriptionSection.Visibility = Visibility.Visible;
        // H13 PieceEditor slice 1: VersionDescription lives on the VM.
        _vm.LoadVersionDescription(_sourceVersion);
        VersionsSectionHeader.Visibility = Visibility.Collapsed;
        VersionsSectionPanel.Visibility  = Visibility.Collapsed;

        ComposerCombo.ItemsSource = composerNames ?? [];
        ComposerCombo.SelectionChanged += (_, _) => UpdateCatalogPrefixDropdown();

        PopulateDropdowns();
        LoadFromPiece();
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
    /// so the unified editor can work with it unchanged. Delegates to the
    /// shared <see cref="PieceVersionShuttle.FromVersion"/> helper (H14) —
    /// pre-fix the property list lived here and silently dropped any field
    /// that wasn't kept in sync between the two model classes (which is how
    /// the <c>TextAuthor</c> drift went unnoticed for so long).
    /// </summary>
    private static CanonPiece VersionToPiece(CanonPieceVersion v, bool showSubpieceNumbers) =>
        PieceVersionShuttle.FromVersion(v, showSubpieceNumbers);

    /// <summary>
    /// Copies the edited <see cref="_piece"/> back into the source
    /// <see cref="CanonPieceVersion"/> after the user clicks OK. Delegates
    /// to <see cref="PieceVersionShuttle.IntoVersion"/>; the version-only
    /// <c>Description</c> field is still owned by this editor.
    /// </summary>
    private void CopyPieceToVersion()
    {
        var v = _sourceVersion!;
        // H13 PieceEditor slice 1: VersionDescription lives on the VM.
        _vm.SaveVersionDescription(v);
        PieceVersionShuttle.IntoVersion(_piece, v);
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
        // H13 PieceEditor slice 2: read from VM (same content as
        // ComposerCombo.Text via the TwoWay binding, but VM is the canonical
        // source).
        var composerName = _vm.Composer.Trim();
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
        // H13 PieceEditor slice 1: text fields load through the VM
        // (TwoWay-bound in XAML — see VM's LoadFromPiece for the field set).
        // Slice 2: combobox fields (Composer / Form / KeyTonality / KeyMode /
        // Category) also load through the VM via the inheritedComposer overload.
        // Slice 3: NumberedSubpieces + SubpiecesStart also.
        // Slice 4: 8 list-shaped fields (Composers / CatalogEntries /
        // PieceInstruments / Subpieces / Versions / Roles / Markers /
        // Variants) populated by VM.LoadListsFromPiece called from LoadFromPiece.
        _vm.LoadFromPiece(_piece, _inheritedComposer);
        // Seed Other contributors from parent if this piece has none of its own.
        _vm.SeedInheritedComposers(_inheritedComposers);

        // Refresh all 8 ListBoxes once after the VM's load has populated the
        // collections. Subsequent Add/Edit/Remove handlers re-fire the
        // appropriate Refresh after mutating the collection.
        RefreshComposerCreditList();
        RefreshCatalogList();
        RefreshInstrumentList();
        RefreshMarkerList();
        RefreshSubpieceList();
        RefreshVersionList();
        RefreshRoleList();
        RefreshVariantList();
    }

    private void SaveToPiece()
    {
        // H13 PieceEditor slice 1: text fields save through the VM.
        // Slice 2: combobox fields (Composer / Form / KeyTonality / KeyMode /
        // Category) also save through the VM.
        // Slice 3: NumberedSubpieces + SubpiecesStart also (handles save-null-
        // when-matches-default normalisation).
        // Slice 4: 8 list-shaped fields persist via VM.SaveListsToPiece called
        // from SaveToPiece. Markers preserve stable Ids (items in
        // _vm.Markers are the actual MusicalMarker instances from the piece,
        // not clones).
        _vm.SaveToPiece(_piece);
    }

    // --- Composer credit management ---

    private void RefreshComposerCreditList()
    {
        var selected = (ComposerCreditList.SelectedItem as ListBoxItem)?.Tag;
        ComposerCreditList.Items.Clear();
        foreach (var credit in _vm.Composers)
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
            _vm.Composers.Add(editor.Credit);
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
            var idx = _vm.Composers.IndexOf(credit);
            _vm.Composers[idx] = editor.Credit;
            RefreshComposerCreditList();
        }
    }

    private void OnRemoveComposerCreditClick(object sender, RoutedEventArgs e)
    {
        if (SelectedComposerCredit is not { } credit) return;
        _vm.Composers.Remove(credit);
        RefreshComposerCreditList();
    }

    // --- Subpiece management ---

    private void RenumberSubpieces()
    {
        // H13 PieceEditor slice 3: VM owns SubpiecesStart parsing.
        var start = _vm.EffectiveSubpiecesStart;
        for (var i = 0; i < _vm.Subpieces.Count; i++)
            _vm.Subpieces[i].Number = start + i;
    }

    private void RefreshSubpieceList()
    {
        RenumberSubpieces();
        // H13 PieceEditor slice 3: VM owns NumberedSubpieces.
        var showNums = _vm.NumberedSubpieces;
        var selectedTag = (SubpieceList.SelectedItem as ListBoxItem)?.Tag;
        SubpieceList.Items.Clear();
        foreach (var sp in _vm.Subpieces)
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

    // H13 PieceEditor slice 3: EffectiveSubpiecesStart moved to
    // PieceEditorViewModel — see _vm.EffectiveSubpiecesStart.

    private CanonPiece? SelectedSubpiece =>
        (SubpieceList.SelectedItem as ListBoxItem)?.Tag as CanonPiece;

    private void OnAddSubpieceClick(object sender, RoutedEventArgs e)
    {
        var newSubpiece = new CanonPiece { Number = _vm.Subpieces.Count + 1 };
        var editor = new PieceEditorWindow(
            _pickLists, "", newSubpiece,
            ComposerCombo.ItemsSource as IReadOnlyList<string>, PieceEditorMode.Subpiece,
            inheritedComposer: ComposerCombo.Text,
            inheritedComposers: _vm.Composers.Count > 0 ? _vm.Composers : null,
            composerCatalogs: _composerCatalogs,
            ancestorRoles: AncestorRolesForChildren()) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _vm.Subpieces.Add(editor.Piece);
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
            inheritedComposers: _vm.Composers.Count > 0 ? _vm.Composers : null,
            composerCatalogs: _composerCatalogs,
            ancestorRoles: AncestorRolesForChildren()) { Owner = this };
        if (editor.ShowDialog() == true)
            RefreshSubpieceList();
    }

    private void OnRemoveSubpieceClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSubpiece is not { } sp) return;
        _vm.Subpieces.Remove(sp);
        RefreshSubpieceList();
    }

    private void OnMoveSubpieceUpClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSubpiece is not { } sp) return;
        var idx = _vm.Subpieces.IndexOf(sp);
        if (idx <= 0) return;
        (_vm.Subpieces[idx], _vm.Subpieces[idx - 1]) = (_vm.Subpieces[idx - 1], _vm.Subpieces[idx]);
        RefreshSubpieceList();
    }

    private void OnMoveSubpieceDownClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSubpiece is not { } sp) return;
        var idx = _vm.Subpieces.IndexOf(sp);
        if (idx < 0 || idx >= _vm.Subpieces.Count - 1) return;
        (_vm.Subpieces[idx], _vm.Subpieces[idx + 1]) = (_vm.Subpieces[idx + 1], _vm.Subpieces[idx]);
        RefreshSubpieceList();
    }

    // --- Role management ---

    private void RefreshRoleList()
    {
        var selectedTag = (RoleList.SelectedItem as ListBoxItem)?.Tag;
        RoleList.Items.Clear();
        foreach (var r in _vm.Roles)
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
            var picker = new RolePickerWindow(_ancestorRoles, _vm.Roles) { Owner = this };
            if (picker.ShowDialog() == true && picker.SelectedRoles.Count > 0)
            {
                // Add as name-only references — the full definition lives on the parent piece.
                foreach (var r in picker.SelectedRoles)
                    _vm.Roles.Add(new RoleEntry { Name = r.Name });
                RefreshRoleList();
            }
            return;
        }

        // No ancestor roles — free-form entry (top-level piece cast definition).
        var editor = new RoleEditorWindow(_pickLists) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _vm.Roles.Add(editor.Role);
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
            var idx = _vm.Roles.IndexOf(r);
            _vm.Roles[idx] = editor.Role;
            RefreshRoleList();
        }
    }

    private void OnRemoveRoleClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRole is not { } r) return;
        _vm.Roles.Remove(r);
        RefreshRoleList();
    }

    private void OnMoveRoleUpClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRole is not { } r) return;
        var idx = _vm.Roles.IndexOf(r);
        if (idx <= 0) return;
        (_vm.Roles[idx], _vm.Roles[idx - 1]) = (_vm.Roles[idx - 1], _vm.Roles[idx]);
        RefreshRoleList();
    }

    private void OnMoveRoleDownClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRole is not { } r) return;
        var idx = _vm.Roles.IndexOf(r);
        if (idx < 0 || idx >= _vm.Roles.Count - 1) return;
        (_vm.Roles[idx], _vm.Roles[idx + 1]) = (_vm.Roles[idx + 1], _vm.Roles[idx]);
        RefreshRoleList();
    }

    // --- Variant management ---

    private void RefreshVariantList()
    {
        var selectedTag = (VariantList.SelectedItem as ListBoxItem)?.Tag;
        VariantList.Items.Clear();
        foreach (var v in _vm.Variants)
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
            _vm.Variants.Add(editor.Variant);
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
            var idx = _vm.Variants.IndexOf(variant);
            _vm.Variants[idx] = editor.Variant;
            RefreshVariantList();
        }
    }

    private void OnRemoveVariantClick(object sender, RoutedEventArgs e)
    {
        if (SelectedVariant is not { } variant) return;
        _vm.Variants.Remove(variant);
        RefreshVariantList();
    }

    private void OnMoveVariantUpClick(object sender, RoutedEventArgs e)
    {
        if (SelectedVariant is not { } variant) return;
        var idx = _vm.Variants.IndexOf(variant);
        if (idx <= 0) return;
        (_vm.Variants[idx], _vm.Variants[idx - 1]) = (_vm.Variants[idx - 1], _vm.Variants[idx]);
        RefreshVariantList();
    }

    private void OnMoveVariantDownClick(object sender, RoutedEventArgs e)
    {
        if (SelectedVariant is not { } variant) return;
        var idx = _vm.Variants.IndexOf(variant);
        if (idx < 0 || idx >= _vm.Variants.Count - 1) return;
        (_vm.Variants[idx], _vm.Variants[idx + 1]) = (_vm.Variants[idx + 1], _vm.Variants[idx]);
        RefreshVariantList();
    }

    // H13 PieceEditor slice 4: CloneVariant retired — moved to
    // PieceEditorViewModel as a private static helper used internally by
    // LoadListsFromPiece.

    // --- Marker management ---
    // Markers carry stable Ids that album-track refs depend on, so they're
    // edited in place rather than cloned. Add/Remove mutate the _vm.Markers list
    // (and _piece.Markers via ApplyToPiece on save); Edit mutates the marker
    // instance itself, so the Id never changes through a round-trip.

    private void RefreshMarkerList()
    {
        var keep = SelectedMarker;
        MarkerList.Items.Clear();
        foreach (var m in _vm.Markers)
        {
            MarkerList.Items.Add(new ListBoxItem
            {
                Content = FormatMarkerLabel(m),
                Tag     = m,
            });
        }
        if (keep is not null) SelectMarkerByRef(keep);
    }

    private static string FormatMarkerLabel(MusicalMarker m)
    {
        // Compact one-liner: "Kind: Value (bar N)" with bar / number elided
        // when absent. Matches the density of the tempo list.
        var sb = new System.Text.StringBuilder();
        sb.Append(KindShort(m.Kind)).Append(": ");
        sb.Append(string.IsNullOrEmpty(m.Value)
            ? (m.BarNumber is { } bn ? $"bar {bn}" : "(no value)")
            : m.Value);
        if (!string.IsNullOrEmpty(m.Value) && m.BarNumber is { } bn2)
            sb.Append("  (bar ").Append(bn2).Append(')');
        return sb.ToString();
    }

    private static string KindShort(MarkerKind k) => k switch
    {
        MarkerKind.Tempo         => "Tempo",
        MarkerKind.FirstLine     => "First line",
        MarkerKind.RehearsalMark => "Rehearsal",
        MarkerKind.BarNumber     => "Bar",
        MarkerKind.Section       => "Section",
        _                        => k.ToString(),
    };

    private MusicalMarker? SelectedMarker =>
        (MarkerList.SelectedItem as ListBoxItem)?.Tag as MusicalMarker;

    private void OnAddMarkerClick(object sender, RoutedEventArgs e)
    {
        var editor = new MarkerEditorWindow { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _vm.Markers.Add(editor.Marker);
            RefreshMarkerList();
            SelectMarkerByRef(editor.Marker);
        }
    }

    private void OnEditMarkerClick(object sender, RoutedEventArgs e) => EditSelectedMarker();

    private void OnMarkerDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedMarker();

    private void EditSelectedMarker()
    {
        if (SelectedMarker is not { } marker) return;
        var editor = new MarkerEditorWindow(marker) { Owner = this };
        if (editor.ShowDialog() == true)
            RefreshMarkerList();
    }

    private void OnRemoveMarkerClick(object sender, RoutedEventArgs e)
    {
        if (SelectedMarker is not { } marker) return;
        _vm.Markers.Remove(marker);
        RefreshMarkerList();
    }

    private void OnMoveMarkerUpClick(object sender, RoutedEventArgs e)
    {
        if (SelectedMarker is not { } marker) return;
        var idx = _vm.Markers.IndexOf(marker);
        if (idx <= 0) return;
        (_vm.Markers[idx], _vm.Markers[idx - 1]) = (_vm.Markers[idx - 1], _vm.Markers[idx]);
        RefreshMarkerList();
        SelectMarkerByRef(marker);
    }

    private void OnMoveMarkerDownClick(object sender, RoutedEventArgs e)
    {
        if (SelectedMarker is not { } marker) return;
        var idx = _vm.Markers.IndexOf(marker);
        if (idx < 0 || idx >= _vm.Markers.Count - 1) return;
        (_vm.Markers[idx], _vm.Markers[idx + 1]) = (_vm.Markers[idx + 1], _vm.Markers[idx]);
        RefreshMarkerList();
        SelectMarkerByRef(marker);
    }

    private void SelectMarkerByRef(MusicalMarker marker)
    {
        foreach (ListBoxItem item in MarkerList.Items)
        {
            if (item.Tag == marker)
            {
                MarkerList.SelectedItem = item;
                break;
            }
        }
    }

    // --- Catalog list ---

    private void RefreshCatalogList()
    {
        CatalogList.Items.Clear();
        foreach (var cat in _vm.CatalogEntries)
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
        _vm.CatalogEntries.Add(new CatalogInfo
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
        _vm.CatalogEntries.Remove(cat);
        RefreshCatalogList();
    }


    // --- Instrumentation list ---

    private void RefreshInstrumentList()
    {
        var selectedIdx = InstrumentsList.SelectedIndex;
        InstrumentsList.Items.Clear();
        foreach (var entry in _vm.PieceInstruments)
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
            _vm.PieceInstruments.Add(entry);
            RefreshInstrumentList();
            InstrumentsList.SelectedIndex = InstrumentsList.Items.Count - 1;
            return;
        }

        var seed = preselect != null ? new InstrumentEntry { Instrument = preselect } : null;
        var editor = new InstrumentEntryEditorWindow(_pickLists, seed) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _vm.PieceInstruments.Add(editor.Entry);
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
            _vm.PieceInstruments.Add(entry);
        }
        else
        {
            _vm.PieceInstruments.Add(new InstrumentEntry { Instrument = item });
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

        var current = _vm.PieceInstruments[idx];
        if (current.IsEnsemble)
        {
            var def = _pickLists.Ensembles?.FirstOrDefault(e =>
                string.Equals(e.Name, current.Instrument, StringComparison.OrdinalIgnoreCase));
            // Fixed ensembles have nothing to edit
            if (def?.IsFixed == true) return;

            var ensEditor = new EnsembleEntryEditorWindow(_pickLists, current) { Owner = this };
            if (ensEditor.ShowDialog() == true)
            {
                _vm.PieceInstruments[idx] = ensEditor.Entry;
                RefreshInstrumentList();
                InstrumentsList.SelectedIndex = idx;
            }
            return;
        }

        var editor = new InstrumentEntryEditorWindow(_pickLists, current) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _vm.PieceInstruments[idx] = editor.Entry;
            RefreshInstrumentList();
            InstrumentsList.SelectedIndex = idx;
        }
    }

    private void OnRemoveInstrumentClick(object sender, RoutedEventArgs e)
    {
        var idx = InstrumentsList.SelectedIndex;
        if (idx < 0) return;
        _vm.PieceInstruments.RemoveAt(idx);
        RefreshInstrumentList();
        if (_vm.PieceInstruments.Count > 0)
            InstrumentsList.SelectedIndex = Math.Min(idx, _vm.PieceInstruments.Count - 1);
    }

    private void OnMoveInstrumentUpClick(object sender, RoutedEventArgs e)
    {
        var idx = InstrumentsList.SelectedIndex;
        if (idx <= 0) return;
        (_vm.PieceInstruments[idx], _vm.PieceInstruments[idx - 1]) = (_vm.PieceInstruments[idx - 1], _vm.PieceInstruments[idx]);
        RefreshInstrumentList();
        InstrumentsList.SelectedIndex = idx - 1;
    }

    private void OnMoveInstrumentDownClick(object sender, RoutedEventArgs e)
    {
        var idx = InstrumentsList.SelectedIndex;
        if (idx < 0 || idx >= _vm.PieceInstruments.Count - 1) return;
        (_vm.PieceInstruments[idx], _vm.PieceInstruments[idx + 1]) = (_vm.PieceInstruments[idx + 1], _vm.PieceInstruments[idx]);
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
        _ancestorRoles ?? (_vm.Roles.Count > 0 ? (IReadOnlyList<RoleEntry>)_vm.Roles : null);

    // --- Version management ---

    private void RefreshVersionList()
    {
        var selectedTag = (VersionList.SelectedItem as ListBoxItem)?.Tag;
        VersionList.Items.Clear();
        foreach (var v in _vm.Versions)
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
            inheritedComposers: _vm.Composers.Count > 0 ? _vm.Composers : null,
            composerCatalogs: _composerCatalogs) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _vm.Versions.Add(newVersion);
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
            inheritedComposers: _vm.Composers.Count > 0 ? _vm.Composers : null,
            composerCatalogs: _composerCatalogs) { Owner = this };
        if (editor.ShowDialog() == true) RefreshVersionList();
    }

    private void OnRemoveVersionClick(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion is not { } v) return;
        _vm.Versions.Remove(v);
        RefreshVersionList();
    }

    private void OnMoveVersionUpClick(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion is not { } v) return;
        var idx = _vm.Versions.IndexOf(v);
        if (idx <= 0) return;
        (_vm.Versions[idx], _vm.Versions[idx - 1]) = (_vm.Versions[idx - 1], _vm.Versions[idx]);
        RefreshVersionList();
    }

    private void OnMoveVersionDownClick(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion is not { } v) return;
        var idx = _vm.Versions.IndexOf(v);
        if (idx < 0 || idx >= _vm.Versions.Count - 1) return;
        (_vm.Versions[idx], _vm.Versions[idx + 1]) = (_vm.Versions[idx + 1], _vm.Versions[idx]);
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
