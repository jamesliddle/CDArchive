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

    private string _sortColumn    = "Pieces";
    private bool   _sortAscending = false;   // most pieces first by default

    // ── Piece sort state ─────────────────────────────────────────────────────

    private string _pieceSortField = "Catalogue";

    // ── Current selection ────────────────────────────────────────────────────

    private CanonComposer? _activeComposer;
    private CanonPiece?    _activePiece;

    // ── Context-menu target (set on right-click, independent of selection) ────
    // Tracking this separately avoids calling tvi.IsSelected = true inside any
    // mouse-button handler.  Any selection change during mouse-button routing
    // triggers a SelectedItemChanged→layout cascade that corrupts expander state
    // on unrelated rows, so we never touch IsSelected from a mouse handler.

    private object?      _ctxTarget;   // data item that was right-clicked
    private TreeViewItem? _ctxTvi;     // its container

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

    /// <summary>Which contributed-role group headers are expanded, keyed by (role, composerName).</summary>
    private readonly HashSet<(string, string)> _expandedContributedGroups = [];

    // ── Constructor ──────────────────────────────────────────────────────────

    public CanonView()
    {
        InitializeComponent();

        Loaded += (_, _) => { };
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

    private void OnComposerSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComposerSortCombo.SelectedItem is not ComboBoxItem item) return;
        if (DataContext is not CanonViewModel vm) return;

        (_sortColumn, _sortAscending) = item.Content?.ToString() switch
        {
            "Pieces" => ("Pieces", false),   // most pieces first
            "Name"   => ("Name",   true),    // A → Z
            "Born"   => ("Birth",  true),    // oldest first
            "Died"   => ("Death",  true),    // oldest death first
            _        => (_sortColumn, _sortAscending),
        };

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
            .Select(c =>
            {
                var node = new ComposerTreeNode(c, GetSortedPieces(vm, c.Name));
                node.ContributedGroups = ContributedWorksFinder.FindContributedGroups(vm.Pieces, c.Name);
                return node;
            })
            .ToList();

        ComposerTree.ItemsSource = nodes;
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
        _expandedContributedGroups.Clear();

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

            // Contributed-work groups
            foreach (var group in node.ContributedGroups)
            {
                if (ci.ItemContainerGenerator.ContainerFromItem(group)
                        is not TreeViewItem gi) continue;

                if (gi.IsExpanded)
                    _expandedContributedGroups.Add((group.Role, group.ComposerName));

                foreach (var contribPiece in group.Pieces)
                {
                    if (gi.ItemContainerGenerator.ContainerFromItem(contribPiece)
                            is not TreeViewItem cpi) continue;

                    if (cpi.IsExpanded)
                        _expandedPieces.Add(contribPiece.Piece);

                    CollectExpandedSubpieces(cpi, cpi.Items);
                }
            }
        }
    }

    /// <summary>
    /// After the tree has been rebuilt, re-expands composers, pieces, and
    /// subpiece nodes that were previously expanded.
    /// </summary>
    private void RestoreAllExpansionState(List<ComposerTreeNode> nodes)
    {
        if (_expandedComposers.Count == 0 && _expandedPieces.Count == 0
            && _expandedContributedGroups.Count == 0) return;
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

            // Restore contributed-group expansion
            foreach (var group in node.ContributedGroups)
            {
                if (!_expandedContributedGroups.Contains((group.Role, group.ComposerName))) continue;
                if (ci.ItemContainerGenerator.ContainerFromItem(group)
                        is not TreeViewItem gi) continue;

                gi.IsExpanded = true;
                gi.UpdateLayout();

                foreach (var contribPiece in group.Pieces)
                {
                    if (!_expandedPieces.Contains(contribPiece.Piece)) continue;
                    if (gi.ItemContainerGenerator.ContainerFromItem(contribPiece)
                            is not TreeViewItem cpi) continue;

                    cpi.IsExpanded = true;
                    cpi.UpdateLayout();
                    ApplyExpandedSubpieces(cpi, cpi.Items);
                }
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
        else if (e.NewValue is ContributedRoleGroupNode)
        {
            // Group header — no piece/composer change, disable New/Delete
            NewPieceButton.IsEnabled    = false;
            DeletePieceButton.IsEnabled = false;
        }
        else if (e.NewValue is ContributedPieceNode contribNode)
        {
            _activePiece = contribNode.Piece;
            _activeComposer = vm.Composers.FirstOrDefault(c =>
                string.Equals(c.Name, contribNode.Piece.Composer, StringComparison.OrdinalIgnoreCase));
            NewPieceButton.IsEnabled    = false;
            DeletePieceButton.IsEnabled = false;
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
        await EditSelectedItemAsync(item.DataContext);
    }

    // ── Enter key ─────────────────────────────────────────────────────────────

    private async void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var selected = ComposerTree.SelectedItem;
        if (selected == null) return;
        e.Handled = true;
        await EditSelectedItemAsync(selected);
    }

    // ── Shared edit dispatcher ────────────────────────────────────────────────

    private async Task EditSelectedItemAsync(object? item)
    {
        switch (item)
        {
            case ComposerTreeNode node:
                await EditComposerAsync(node.Composer);
                break;
            case ContributedPieceNode contribNode:
                await EditPieceAsync(contribNode.Piece);
                break;
            case ContributedRoleGroupNode:
                break;   // group header — no edit
            case CanonPiece piece:
                await EditPieceAsync(piece);
                break;
            case PieceOriginalNode origNode:
                await EditPieceAsync(origNode.Piece);
                break;
            case VersionDisplayNode versionNode:
                await EditVersionAsync(versionNode);
                break;
            case SubpieceDisplayNode subNode:
                await EditSubpieceAsync(subNode.Piece, subNode.ParentPiece);
                break;
        }
    }

    // ── Context menu: record right-clicked item (never touch IsSelected) ────────

    private void OnTreeMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var hit = e.OriginalSource as DependencyObject;
        while (hit != null && hit is not TreeViewItem)
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);

        if (hit is TreeViewItem tvi)
        {
            _ctxTarget = tvi.DataContext;
            _ctxTvi    = tvi;
        }
        else
        {
            _ctxTarget = null;
            _ctxTvi    = null;
        }
    }

    // ── Context menu: enable/disable items before showing ────────────────────

    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Suppress menu if right-click landed on empty space
        if (_ctxTarget == null)
        {
            e.Handled = true;
            return;
        }

        bool canEdit = _ctxTarget is not ContributedRoleGroupNode;
        bool hasChildren = _ctxTarget switch
        {
            ComposerTreeNode n          => n.AllItems.Count > 0,
            CanonPiece p                => p.HasTreeChildren,
            SubpieceDisplayNode s       => s.HasChildren,
            PieceOriginalNode o         => o.HasChildren,
            VersionDisplayNode v        => v.HasSubpieces,
            ContributedRoleGroupNode g  => g.Pieces.Count > 0,
            ContributedPieceNode cp     => cp.HasChildren,
            _                           => false,
        };

        CtxEdit.IsEnabled        = canEdit;
        CtxExpandAll.IsEnabled   = hasChildren;
        CtxCollapseAll.IsEnabled = hasChildren;
    }

    // ── Context menu: handlers ────────────────────────────────────────────────

    private async void OnContextEdit(object sender, RoutedEventArgs e) =>
        await EditSelectedItemAsync(_ctxTarget);

    private void OnContextExpandAll(object sender, RoutedEventArgs e)
    {
        if (_ctxTvi == null) return;
        _ctxTvi.IsExpanded = true;
        SetExpandedRecursive(_ctxTvi, expand: true);
    }

    private void OnContextCollapseAll(object sender, RoutedEventArgs e)
    {
        if (_ctxTvi == null) return;
        SetExpandedRecursive(_ctxTvi, expand: false);
        _ctxTvi.IsExpanded = false;
    }

    // ── Expand / collapse helpers ─────────────────────────────────────────────

    private static void SetExpandedRecursive(TreeViewItem parent, bool expand)
    {
        parent.UpdateLayout();   // ensure child containers are generated
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item)
                    is not TreeViewItem child) continue;
            child.IsExpanded = expand;
            SetExpandedRecursive(child, expand);
        }
    }

    // ── Edit: composer ───────────────────────────────────────────────────────

    private async Task EditComposerAsync(CanonComposer composer)
    {
        if (DataContext is not CanonViewModel vm) return;

        var window = new ComposerEditorWindow(vm.PickLists, composer)
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
        var composerCatalogs = BuildComposerCatalogDict(vm);
        var window = new PieceEditorWindow(vm.PickLists, piece.Composer ?? "", piece, composerNames,
            composerCatalogs: composerCatalogs)
        {
            Owner = Window.GetWindow(this)
        };

        if (window.ShowDialog() == true)
        {
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
        var composerCatalogs = BuildComposerCatalogDict(vm);
        var window = new PieceEditorWindow(
            vm.PickLists,
            versionNode.Version,
            showSubpieceNumbers: parentPiece.EffectiveSubpiecesNumbered,
            composerNames: composerNames,
            inheritedComposer: parentPiece.Composer,
            inheritedComposers: parentPiece.Composers,
            composerCatalogs: composerCatalogs)
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
        var composerCatalogs = BuildComposerCatalogDict(vm);
        var ancestorRoles = ParseAncestorRoles(parent, subpiece);
        var window = new PieceEditorWindow(
            vm.PickLists, subpiece.Composer ?? "", subpiece, composerNames, PieceEditorMode.Subpiece,
            inheritedComposer: parent?.Composer,
            inheritedComposers: parent?.Composers,
            composerCatalogs: composerCatalogs,
            ancestorRoles: ancestorRoles)
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

        var window = new ComposerEditorWindow(vm.PickLists)
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
        var composerCatalogs = BuildComposerCatalogDict(vm);
        var window = new PieceEditorWindow(vm.PickLists, composerName, null, composerNames,
            composerCatalogs: composerCatalogs)
        {
            Owner = Window.GetWindow(this)
        };

        if (window.ShowDialog() == true)
        {
            vm.Pieces.Add(window.Piece);
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

    // ── Shared helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a case-insensitive dictionary from composer name to their permitted catalogue
    /// prefixes. Composers with no restrictions are omitted (callers treat a missing key as
    /// "no restriction — show all prefixes").
    /// </summary>
    /// <summary>
    /// Parses the roles from a parent piece into a flat list suitable for passing
    /// as <c>ancestorRoles</c> to a subpiece editor. Returns null if the piece has
    /// no roles defined.
    /// </summary>
    /// <summary>
    /// Returns the ancestor roles visible to <paramref name="subpieceContext"/>.
    /// Checks the top-level piece's roles first; if absent, walks the piece's versions
    /// to find whichever version contains the subpiece (by reference) and returns
    /// that version's roles instead.
    /// </summary>
    private static IReadOnlyList<RoleEntry>? ParseAncestorRoles(
        CanonPiece? piece, CanonPiece? subpieceContext = null)
    {
        if (piece == null) return null;

        if (piece.Roles is { } roles)
        {
            var parsed = RoleEntry.ParseRoles(roles);
            if (parsed.Count > 0) return parsed;
        }

        // Fall back to whichever version contains the subpiece
        if (subpieceContext != null && piece.Versions != null)
        {
            foreach (var v in piece.Versions)
            {
                if (v.Roles != null && SubpieceExistsInTree(v.Subpieces, subpieceContext))
                {
                    var parsed = RoleEntry.ParseRoles(v.Roles.Value);
                    if (parsed.Count > 0) return parsed;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true if <paramref name="target"/> exists anywhere in the subpiece tree
    /// rooted at <paramref name="subpieces"/> (reference equality, recursive).
    /// </summary>
    private static bool SubpieceExistsInTree(List<CanonPiece>? subpieces, CanonPiece target)
    {
        if (subpieces == null) return false;
        foreach (var sp in subpieces)
        {
            if (ReferenceEquals(sp, target)) return true;
            if (SubpieceExistsInTree(sp.Subpieces, target)) return true;
        }
        return false;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildComposerCatalogDict(CanonViewModel vm)
    {
        var dict = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var composer in vm.Composers)
        {
            if (composer.CatalogPrefixes is { Count: > 0 })
                dict[composer.Name] = composer.CatalogPrefixes;
        }
        return dict;
    }

    private static void UpdatePieceCounts(CanonViewModel vm)
    {
        var ownCounts = vm.Pieces
            .Where(p => !string.IsNullOrEmpty(p.Composer))
            .GroupBy(p => p.Composer!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        // For each piece, collect all Other Contributor names from the full hierarchy,
        // then credit each contributor with +1 (excluding the piece's primary composer).
        var contributedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var piece in vm.Pieces)
        {
            var contributors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectAllContributorNames(piece, contributors);
            contributors.Remove(piece.Composer ?? "");   // don't double-count own pieces

            foreach (var name in contributors)
            {
                contributedCounts.TryGetValue(name, out var c);
                contributedCounts[name] = c + 1;
            }
        }

        foreach (var composer in vm.Composers)
        {
            ownCounts.TryGetValue(composer.Name, out var own);
            contributedCounts.TryGetValue(composer.Name, out var contrib);
            composer.PieceCount = own + contrib;
        }
    }

    /// <summary>
    /// Recursively collects all Other Contributor names (non-null role) from a piece,
    /// its versions, and all subpieces at every depth.
    /// </summary>
    private static void CollectAllContributorNames(CanonPiece piece, HashSet<string> names)
    {
        if (piece.Composers != null)
            foreach (var c in piece.Composers)
                if (!string.IsNullOrEmpty(c.Role)) names.Add(c.Name);

        if (piece.Versions != null)
            foreach (var v in piece.Versions)
            {
                if (v.Composers != null)
                    foreach (var c in v.Composers)
                        if (!string.IsNullOrEmpty(c.Role)) names.Add(c.Name);
                if (v.Subpieces != null)
                    foreach (var sp in v.Subpieces)
                        CollectAllContributorNames(sp, names);
            }

        if (piece.Subpieces != null)
            foreach (var sp in piece.Subpieces)
                CollectAllContributorNames(sp, names);
    }

    private static async Task SaveAllAsync(CanonViewModel vm)
    {
        await vm.SavePiecesCommand.ExecuteAsync(null);
        await vm.SavePickListsCommand.ExecuteAsync(null);
    }
}
