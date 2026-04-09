using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class CanonView : UserControl
{
    // ── Composer sort state ──────────────────────────────────────────────────

    private string _sortColumn = "Name";
    private bool   _sortAscending = true;
    private Dictionary<string, TextBlock> _sortIndicators = null!;

    // ── Piece sort state ─────────────────────────────────────────────────────

    private string _pieceSortField = "Catalogue";

    // ── Current selection ────────────────────────────────────────────────────

    private CanonComposer? _activeComposer;
    private CanonPiece?    _activePiece;

    // ── Expansion state (all three levels) ───────────────────────────────────

    /// <summary>Which composers are currently expanded (level 1).</summary>
    private readonly HashSet<CanonComposer> _expandedComposers =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Which pieces are currently expanded to show subpieces (level 2).</summary>
    private readonly HashSet<CanonPiece> _expandedPieces =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Which subpiece/version nodes are expanded (level 3+), keyed by model object.</summary>
    private readonly HashSet<object> _expandedSubpieces =
        new(ReferenceEqualityComparer.Instance);

    // ── Constructor ──────────────────────────────────────────────────────────

    public CanonView()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            _sortIndicators = new Dictionary<string, TextBlock>
            {
                ["Pieces"] = SortPieces,
                ["Name"]   = SortName,
                ["Birth"]  = SortBirth,
                ["Death"]  = SortDeath,
            };
            UpdateSortIndicators();
        };
    }

    // ── Initial data load ────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CanonViewModel vm) return;

        // CanonView is now a permanent element (never recreated), so Loaded fires exactly once.
        // Subscribe for all future reloads (Refresh button, NavigateToCanon, etc.).
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;

        // Initial data load.
        await vm.LoadDataCommand.ExecuteAsync(null);
        UpdatePieceCounts(vm);
        ApplySortedFilter(vm);
    }

    /// <summary>
    /// Rebuilds the tree when a reload triggered externally (e.g. Refresh button) finishes.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CanonViewModel.IsLoading)) return;
        if (sender is not CanonViewModel vm) return;
        if (vm.IsLoading) return;   // only act on the transition to false

        UpdatePieceCounts(vm);
        ApplySortedFilter(vm);
    }

    // ── Toolbar handlers ─────────────────────────────────────────────────────

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is CanonViewModel vm)
            ApplySortedFilter(vm);
    }

    private void OnPieceSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PieceSortCombo.SelectedItem is not ComboBoxItem item) return;
        _pieceSortField = item.Content?.ToString() ?? "Catalogue";
        if (DataContext is CanonViewModel vm)
            ApplySortedFilter(vm);
    }

    private void OnColumnHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not string column) return;
        if (DataContext is not CanonViewModel vm) return;

        if (_sortColumn == column)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn    = column;
            _sortAscending = true;
        }

        ApplySortedFilter(vm);
    }

    // ── Tree: build / refresh ─────────────────────────────────────────────────

    /// <summary>
    /// Saves all expansion state, rebuilds the composer tree with current filter
    /// and sort order, then restores expansion state.
    /// </summary>
    private void ApplySortedFilter(CanonViewModel vm)
    {
        SaveAllExpansionState();

        var filter = vm.ComposerFilter.Trim();
        IEnumerable<CanonComposer> filtered = string.IsNullOrEmpty(filter)
            ? vm.Composers
            : vm.Composers.Where(c =>
                c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.SortName.Contains(filter, StringComparison.OrdinalIgnoreCase));

        filtered = ApplyComposerSort(filtered);

        var nodes = filtered
            .Select(c => new ComposerTreeNode(c, GetSortedPieces(vm, c.Name)))
            .ToList();

        ComposerTree.ItemsSource = nodes;
        UpdateSortIndicators();
        RestoreAllExpansionState(nodes);
    }

    private IEnumerable<CanonComposer> ApplyComposerSort(IEnumerable<CanonComposer> composers) =>
        (_sortColumn, _sortAscending) switch
        {
            ("Pieces", true)  => composers.OrderBy(c => c.PieceCount)
                                          .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase),
            ("Pieces", false) => composers.OrderByDescending(c => c.PieceCount)
                                          .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase),
            ("Name",   true)  => composers.OrderBy(c => !string.IsNullOrEmpty(c.SortName) ? c.SortName : c.Name,
                                                   StringComparer.OrdinalIgnoreCase),
            ("Name",   false) => composers.OrderByDescending(c => !string.IsNullOrEmpty(c.SortName) ? c.SortName : c.Name,
                                                             StringComparer.OrdinalIgnoreCase),
            ("Birth",  true)  => composers.OrderBy(c => c.BirthYearSort)
                                          .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase),
            ("Birth",  false) => composers.OrderByDescending(c => c.BirthYearSort)
                                          .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase),
            ("Death",  true)  => composers.OrderBy(c => c.DeathYearSort)
                                          .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase),
            ("Death",  false) => composers.OrderByDescending(c => c.DeathYearSort)
                                          .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase),
            _                 => composers.OrderBy(c => c.SortName, StringComparer.OrdinalIgnoreCase),
        };

    private List<CanonPiece> GetSortedPieces(CanonViewModel vm, string composerName)
    {
        var pieces = vm.Pieces.Where(p =>
            string.Equals(p.Composer, composerName, StringComparison.OrdinalIgnoreCase));
        return OrderPieces(pieces).ToList();
    }

    private IEnumerable<CanonPiece> OrderPieces(IEnumerable<CanonPiece> pieces) =>
        _pieceSortField switch
        {
            "Title"    => pieces.OrderBy(p => p.DisplayTitle, StringComparer.OrdinalIgnoreCase),
            "Category" => pieces
                .OrderBy(p => p.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.CatalogSortPrefix, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.CatalogSortNumber)
                .ThenBy(p => p.CatalogSortSuffix, StringComparer.OrdinalIgnoreCase),
            "Year"     => pieces
                .OrderBy(p => p.PublicationYear ?? int.MaxValue)
                .ThenBy(p => p.CatalogSortPrefix, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.CatalogSortNumber)
                .ThenBy(p => p.CatalogSortSuffix, StringComparer.OrdinalIgnoreCase),
            _          => CatalogSort(pieces),   // "Catalogue" (default)
        };

    private static IOrderedEnumerable<CanonPiece> CatalogSort(IEnumerable<CanonPiece> pieces) =>
        pieces.OrderBy(p => p.CatalogSortPrefix, StringComparer.OrdinalIgnoreCase)
              .ThenBy(p => p.CatalogSortNumber)
              .ThenBy(p => p.CatalogSortSuffix, StringComparer.OrdinalIgnoreCase)
              .ThenBy(p => p.DisplayTitle, StringComparer.OrdinalIgnoreCase)
              .ThenBy(p => p.Form, StringComparer.OrdinalIgnoreCase)
              .ThenBy(p => p.Number ?? int.MaxValue)
              .ThenBy(p => p.FirstLine, StringComparer.OrdinalIgnoreCase);

    private void UpdateSortIndicators()
    {
        if (_sortIndicators == null) return;
        foreach (var (col, indicator) in _sortIndicators)
            indicator.Text = col == _sortColumn ? (_sortAscending ? "\u25B2" : "\u25BC") : "";
    }

    // ── Expansion state: save / restore (all three levels) ───────────────────

    /// <summary>
    /// Walks the visible tree and records which composers, pieces, and
    /// subpiece/version nodes are currently expanded.
    /// </summary>
    private void SaveAllExpansionState()
    {
        _expandedComposers.Clear();
        _expandedPieces.Clear();
        _expandedSubpieces.Clear();

        if (ComposerTree.ItemsSource is not IEnumerable<ComposerTreeNode> nodes) return;

        foreach (var node in nodes)
        {
            if (ComposerTree.ItemContainerGenerator.ContainerFromItem(node)
                    is not TreeViewItem ci) continue;

            if (ci.IsExpanded)
                _expandedComposers.Add(node.Composer);

            // Level 2: pieces under this composer
            foreach (var piece in node.Pieces)
            {
                if (ci.ItemContainerGenerator.ContainerFromItem(piece)
                        is not TreeViewItem pi) continue;

                if (pi.IsExpanded)
                    _expandedPieces.Add(piece);

                // Level 3+: subpiece / version nodes
                CollectExpandedSubpieces(pi, pi.Items);
            }
        }
    }

    /// <summary>
    /// After the tree has been rebuilt, re-expands composers, pieces, and
    /// subpiece nodes that were previously expanded.
    /// </summary>
    private void RestoreAllExpansionState(List<ComposerTreeNode> nodes)
    {
        if (_expandedComposers.Count == 0 && _expandedPieces.Count == 0) return;
        ComposerTree.UpdateLayout();

        foreach (var node in nodes)
        {
            if (!_expandedComposers.Contains(node.Composer)) continue;
            if (ComposerTree.ItemContainerGenerator.ContainerFromItem(node)
                    is not TreeViewItem ci) continue;

            ci.IsExpanded = true;
            ci.UpdateLayout();

            foreach (var piece in node.Pieces)
            {
                if (!_expandedPieces.Contains(piece)) continue;
                if (ci.ItemContainerGenerator.ContainerFromItem(piece)
                        is not TreeViewItem pi) continue;

                pi.IsExpanded = true;
                pi.UpdateLayout();
                ApplyExpandedSubpieces(pi, pi.Items);
            }
        }
    }

    // Recursive helpers for subpiece/version nodes (level 3+)

    private void CollectExpandedSubpieces(ItemsControl parent, ItemCollection items)
    {
        foreach (var item in items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item)
                    is not TreeViewItem container) continue;
            if (!container.IsExpanded) continue;
            var key = SubpieceKey(item);
            if (key != null) _expandedSubpieces.Add(key);
            if (container.HasItems)
                CollectExpandedSubpieces(container, container.Items);
        }
    }

    private void ApplyExpandedSubpieces(ItemsControl parent, ItemCollection items)
    {
        foreach (var item in items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item)
                    is not TreeViewItem container) continue;
            var key = SubpieceKey(item);
            if (key == null || !_expandedSubpieces.Contains(key)) continue;
            container.IsExpanded = true;
            container.UpdateLayout();
            if (container.HasItems)
                ApplyExpandedSubpieces(container, container.Items);
        }
    }

    private static object? SubpieceKey(object item) => item switch
    {
        SubpieceDisplayNode n => n.Piece,
        VersionDisplayNode  v => (object)v.Version,
        PieceOriginalNode   o => (o.Piece, "original"),   // value-tuple is a struct; stable across rebuilds
        _                     => null,
    };

    // ── Tree: selection ───────────────────────────────────────────────────────

    private void OnComposerTreeSelectionChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is not CanonViewModel vm) return;

        if (e.NewValue is ComposerTreeNode node)
        {
            _activeComposer = node.Composer;
            _activePiece    = null;
            NewPieceButton.IsEnabled    = true;
            DeletePieceButton.IsEnabled = false;
        }
        else if (e.NewValue is CanonPiece piece)
        {
            _activeComposer = vm.Composers.FirstOrDefault(c =>
                string.Equals(c.Name, piece.Composer, StringComparison.OrdinalIgnoreCase));
            _activePiece    = piece;
            NewPieceButton.IsEnabled    = true;
            DeletePieceButton.IsEnabled = true;
        }
        else if (e.NewValue is PieceOriginalNode origNode)
        {
            _activeComposer = vm.Composers.FirstOrDefault(c =>
                string.Equals(c.Name, origNode.Piece.Composer, StringComparison.OrdinalIgnoreCase));
            _activePiece    = origNode.Piece;
            NewPieceButton.IsEnabled    = true;
            DeletePieceButton.IsEnabled = true;
        }
        else if (e.NewValue is VersionDisplayNode versionNode)
        {
            // Track the parent piece so New/Delete Piece still work sensibly.
            if (versionNode.ParentPiece != null)
            {
                _activePiece = versionNode.ParentPiece;
                _activeComposer = vm.Composers.FirstOrDefault(c =>
                    string.Equals(c.Name, versionNode.ParentPiece.Composer, StringComparison.OrdinalIgnoreCase));
            }
            NewPieceButton.IsEnabled    = true;
            DeletePieceButton.IsEnabled = true;
        }
        else if (e.NewValue is SubpieceDisplayNode)
        {
            // Keep _activeComposer / _activePiece and button state from the
            // most recently selected piece — no change needed.
        }
        else
        {
            _activeComposer = null;
            _activePiece    = null;
            NewPieceButton.IsEnabled    = false;
            DeletePieceButton.IsEnabled = false;
        }
    }

    // ── Double-click dispatcher ───────────────────────────────────────────────

    private async void OnTreeItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item || !item.IsSelected) return;
        e.Handled = true;

        if (item.DataContext is ComposerTreeNode node)
            await EditComposerAsync(node.Composer);
        else if (item.DataContext is CanonPiece piece)
            await EditPieceAsync(piece);
        else if (item.DataContext is PieceOriginalNode origNode)
            await EditPieceAsync(origNode.Piece);
        else if (item.DataContext is VersionDisplayNode versionNode)
            await EditVersionAsync(versionNode);
        else if (item.DataContext is SubpieceDisplayNode subNode)
            await EditSubpieceAsync(subNode.Piece, subNode.ParentPiece);
    }

    // ── Edit: composer ───────────────────────────────────────────────────────

    private async Task EditComposerAsync(CanonComposer composer)
    {
        if (DataContext is not CanonViewModel vm) return;

        var window = new ComposerEditorWindow(composer)
        {
            Owner = Window.GetWindow(this)
        };

        if (window.ShowDialog() == true)
        {
            UpdatePieceCounts(vm);
            ApplySortedFilter(vm);
            await vm.SaveComposersCommand.ExecuteAsync(null);
            vm.StatusMessage = $"Updated {composer.Name}.";
        }
    }

    // ── Edit: piece ──────────────────────────────────────────────────────────

    private async Task EditPieceAsync(CanonPiece piece)
    {
        if (DataContext is not CanonViewModel vm) return;

        var composerNames = vm.Composers.Select(c => c.Name).ToList();
        var window = new PieceEditorWindow(vm.PickLists, piece.Composer ?? "", piece, composerNames)
        {
            Owner = Window.GetWindow(this)
        };

        if (window.ShowDialog() == true)
        {
            ApplyRenamesFromDicts(vm,
                window.FormRenames, window.CategoryRenames,
                window.CatalogRenames, window.KeyRenames);
            UpdatePieceCounts(vm);
            ApplySortedFilter(vm);
            await SaveAllAsync(vm);
            vm.StatusMessage = $"Updated piece: {piece.DisplayTitle}.";
        }
    }

    // ── Edit: version (direct from tree) ────────────────────────────────

    private async Task EditVersionAsync(VersionDisplayNode versionNode)
    {
        if (DataContext is not CanonViewModel vm) return;
        if (versionNode.ParentPiece is not { } parentPiece) return;

        var composerNames = vm.Composers.Select(c => c.Name).ToList();
        var window = new PieceEditorWindow(
            vm.PickLists,
            versionNode.Version,
            showSubpieceNumbers: parentPiece.EffectiveSubpiecesNumbered,
            composerNames: composerNames,
            inheritedComposer: parentPiece.Composer,
            inheritedComposers: parentPiece.Composers)
        {
            Owner = Window.GetWindow(this)
        };

        if (window.ShowDialog() == true)
        {
            ApplySortedFilter(vm);
            await SaveAllAsync(vm);
            vm.StatusMessage = $"Updated version: {versionNode.Version.Description ?? "(no description)"}.";
        }
    }

    // ── Edit: subpiece ───────────────────────────────────────────────────────

    private async Task EditSubpieceAsync(CanonPiece subpiece, CanonPiece? parentPiece = null)
    {
        if (DataContext is not CanonViewModel vm) return;

        // Prefer the authoritative parent reference from the node; fall back to _activePiece.
        var parent = parentPiece ?? _activePiece;

        var composerNames = vm.Composers.Select(c => c.Name).ToList();
        var window = new PieceEditorWindow(
            vm.PickLists, subpiece.Composer ?? "", subpiece, composerNames, PieceEditorMode.Subpiece,
            inheritedComposer: parent?.Composer,
            inheritedComposers: parent?.Composers)
        {
            Owner = Window.GetWindow(this)
        };

        if (window.ShowDialog() == true)
        {
            // ApplySortedFilter saves expansion state, rebuilds the tree, then
            // restores it — so the expanded piece and any expanded sub-nodes
            // are all preserved across the refresh.
            ApplySortedFilter(vm);
            await SaveAllAsync(vm);
            vm.StatusMessage = $"Updated: {subpiece.SubpieceDisplayTitle}.";
        }
    }

    // ── Toolbar: New Composer ────────────────────────────────────────────────

    private async void OnNewComposerClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CanonViewModel vm) return;

        var window = new ComposerEditorWindow
        {
            Owner = Window.GetWindow(this)
        };

        if (window.ShowDialog() == true)
        {
            vm.Composers.Add(window.Composer);
            UpdatePieceCounts(vm);
            ApplySortedFilter(vm);
            await vm.SaveComposersCommand.ExecuteAsync(null);
            vm.StatusMessage = $"Added {window.Composer.Name}.";
        }
    }

    // ── Toolbar: Delete Composer ─────────────────────────────────────────────

    private async void OnDeleteComposerClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CanonViewModel vm) return;
        if (_activeComposer == null) return;

        var name = _activeComposer.Name;
        var result = MessageBox.Show(
            $"Delete composer \"{name}\"?\n\nThis cannot be undone.",
            "Delete Composer",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (result != MessageBoxResult.OK) return;

        vm.Composers.Remove(_activeComposer);
        _activeComposer = null;
        UpdatePieceCounts(vm);
        ApplySortedFilter(vm);
        await vm.SaveComposersCommand.ExecuteAsync(null);
        vm.StatusMessage = $"Deleted {name}.";
    }

    // ── Toolbar: New Piece ───────────────────────────────────────────────────

    private async void OnNewPieceClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CanonViewModel vm) return;

        var composerName = _activeComposer?.Name ?? _activePiece?.Composer ?? "";
        var composerNames = vm.Composers.Select(c => c.Name).ToList();
        var window = new PieceEditorWindow(vm.PickLists, composerName, null, composerNames)
        {
            Owner = Window.GetWindow(this)
        };

        if (window.ShowDialog() == true)
        {
            vm.Pieces.Add(window.Piece);
            ApplyRenamesFromDicts(vm,
                window.FormRenames, window.CategoryRenames,
                window.CatalogRenames, window.KeyRenames);
            UpdatePieceCounts(vm);
            ApplySortedFilter(vm);
            await SaveAllAsync(vm);
            vm.StatusMessage = $"Added new piece: {window.Piece.DisplayTitle}.";
        }
    }

    // ── Toolbar: Delete Piece ────────────────────────────────────────────────

    private async void OnDeletePieceClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CanonViewModel vm) return;
        if (_activePiece == null) return;

        var title = _activePiece.DisplayTitle;
        var result = MessageBox.Show(
            $"Delete \"{title}\"?\n\nThis cannot be undone.",
            "Delete Piece",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (result != MessageBoxResult.OK) return;

        vm.Pieces.Remove(_activePiece);
        _activePiece = null;
        DeletePieceButton.IsEnabled = false;
        UpdatePieceCounts(vm);
        ApplySortedFilter(vm);
        await SaveAllAsync(vm);
        vm.StatusMessage = $"Deleted: {title}.";
    }

    // ── Pick list editor (called from MainWindow menu) ───────────────────────

    internal async void OpenPickListEditor()
    {
        if (DataContext is not CanonViewModel vm) return;

        var editor = new PickListEditorWindow(vm.PickLists)
        {
            Owner = Window.GetWindow(this)
        };

        if (editor.ShowDialog() == true)
        {
            editor.ApplyTo(vm.PickLists);
            ApplyPickListRenames(vm, editor);
            await SaveAllAsync(vm);
        }
    }

    // ── Rename propagation ───────────────────────────────────────────────────

    private void ApplyPickListRenames(CanonViewModel vm, PickListEditorWindow editor)
    {
        ApplyRenamesFromDicts(vm,
            editor.FormRenames, editor.CategoryRenames,
            editor.CatalogRenames, editor.KeyRenames);
    }

    private static void ApplyRenamesFromDicts(
        CanonViewModel vm,
        Dictionary<string, string> formRenames,
        Dictionary<string, string> categoryRenames,
        Dictionary<string, string> catalogRenames,
        Dictionary<string, string> keyRenames)
    {
        if (formRenames.Count == 0 && categoryRenames.Count == 0 &&
            catalogRenames.Count == 0 && keyRenames.Count == 0)
            return;

        var count = 0;
        foreach (var piece in vm.Pieces)
            count += ApplyRenamesToPiece(piece, formRenames, categoryRenames, catalogRenames, keyRenames);

        if (count > 0)
            vm.StatusMessage = $"Renamed values propagated to {count} field(s). Save to persist.";
    }

    private static int ApplyRenamesToPiece(
        CanonPiece piece,
        Dictionary<string, string> formRenames,
        Dictionary<string, string> categoryRenames,
        Dictionary<string, string> catalogRenames,
        Dictionary<string, string> keyRenames)
    {
        var count = 0;

        if (piece.Form != null && formRenames.TryGetValue(piece.Form, out var newForm))
        { piece.Form = newForm; count++; }

        if (piece.InstrumentationCategory != null &&
            categoryRenames.TryGetValue(piece.InstrumentationCategory, out var newCat))
        { piece.InstrumentationCategory = newCat; count++; }

        if (piece.KeyTonality != null && keyRenames.TryGetValue(piece.KeyTonality, out var newKey))
        { piece.KeyTonality = newKey; count++; }

        if (piece.CatalogInfo != null)
        {
            foreach (var cat in piece.CatalogInfo)
            {
                if (catalogRenames.TryGetValue(cat.Catalog, out var newPrefix))
                { cat.Catalog = newPrefix; count++; }
            }
        }

        if (piece.Subpieces != null)
        {
            foreach (var sub in piece.Subpieces)
                count += ApplyRenamesToPiece(sub, formRenames, categoryRenames, catalogRenames, keyRenames);
        }

        return count;
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    private static void UpdatePieceCounts(CanonViewModel vm)
    {
        var counts = vm.Pieces
            .Where(p => !string.IsNullOrEmpty(p.Composer))
            .GroupBy(p => p.Composer!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var composer in vm.Composers)
            composer.PieceCount = counts.TryGetValue(composer.Name, out var c) ? c : 0;
    }

    private static async Task SaveAllAsync(CanonViewModel vm)
    {
        await vm.SavePiecesCommand.ExecuteAsync(null);
        await vm.SavePickListsCommand.ExecuteAsync(null);
    }
}
