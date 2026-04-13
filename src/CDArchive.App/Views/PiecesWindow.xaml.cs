using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using Key = System.Windows.Input.Key;

namespace CDArchive.App.Views;

public partial class PiecesWindow : Window
{
    private readonly CanonViewModel _vm;
    private readonly string _composerName;

    // Sort state
    private string _sortColumn = "Catalog";
    private bool _sortAscending = true;

    // Sort indicator TextBlocks, mapped by column name
    private Dictionary<string, TextBlock> _sortIndicators = null!;

    // Saved tree expansion state (keyed by data-object identity)
    private readonly HashSet<object> _expandedItems = new(ReferenceEqualityComparer.Instance);

    public PiecesWindow(CanonViewModel vm, string composerName)
    {
        InitializeComponent();

        _vm = vm;
        _composerName = composerName;
        Title = $"Pieces — {composerName}";

        Loaded += (_, _) =>
        {
            _sortIndicators = new Dictionary<string, TextBlock>
            {
                ["Title"] = SortTitle,
                ["Catalog"] = SortCatalog,
                ["Category"] = SortCategory,
                ["Year"] = SortYear,
            };
            ApplyFilter();
        };
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void OnColumnHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not string column) return;

        if (_sortColumn == column)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }

        ApplyFilter();
    }

    private void ApplyFilter(bool preserveExpansion = false)
    {
        if (preserveExpansion) SaveExpansionState();

        var textFilter = FilterBox.Text.Trim();

        IEnumerable<CanonPiece> filtered = _vm.Pieces
            .Where(p => string.Equals(p.Composer, _composerName, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(textFilter))
        {
            filtered = filtered.Where(p =>
                (p.Title ?? "").Contains(textFilter, StringComparison.OrdinalIgnoreCase) ||
                (p.Form ?? "").Contains(textFilter, StringComparison.OrdinalIgnoreCase) ||
                p.Summary.Contains(textFilter, StringComparison.OrdinalIgnoreCase));
        }

        // Apply sort
        filtered = ApplySort(filtered);

        PiecesTree.ItemsSource = new ObservableCollection<CanonPiece>(filtered.ToList());
        UpdateSortIndicators();

        if (preserveExpansion) RestoreExpansionState();
    }

    // ── Expansion state preservation ────────────────────────────────────────

    /// <summary>
    /// Walks the live tree and records which items are currently expanded.
    /// Items are identified by their underlying data object (CanonPiece or
    /// CanonPieceVersion reference), which survives the ItemsSource replacement.
    /// </summary>
    private void SaveExpansionState()
    {
        _expandedItems.Clear();
        CollectExpandedItems(PiecesTree, PiecesTree.Items);
    }

    private void CollectExpandedItems(ItemsControl parent, ItemCollection items)
    {
        foreach (var item in items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) continue;
            if (!container.IsExpanded) continue;
            var key = ExpansionKey(item);
            if (key != null) _expandedItems.Add(key);
            if (container.HasItems)
                CollectExpandedItems(container, container.Items);
        }
    }

    /// <summary>
    /// Re-expands nodes whose underlying data keys were saved by
    /// <see cref="SaveExpansionState"/>. Calls UpdateLayout() at each level so
    /// that child containers exist before we recurse into them.
    /// </summary>
    private void RestoreExpansionState()
    {
        PiecesTree.UpdateLayout();          // ensure top-level containers exist
        ApplyExpandedItems(PiecesTree, PiecesTree.Items);
    }

    private void ApplyExpandedItems(ItemsControl parent, ItemCollection items)
    {
        foreach (var item in items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) continue;
            var key = ExpansionKey(item);
            if (key == null || !_expandedItems.Contains(key)) continue;
            container.IsExpanded = true;
            container.UpdateLayout();       // ensure child containers exist before recursing
            if (container.HasItems)
                ApplyExpandedItems(container, container.Items);
        }
    }

    /// <summary>
    /// Returns the stable identity key for an item in the tree.
    /// SubpieceDisplayNode and VersionDisplayNode are recreated on every tree
    /// refresh, but their inner Piece/Version references are the same objects.
    /// </summary>
    private static object? ExpansionKey(object item) => item switch
    {
        CanonPiece p          => p,
        SubpieceDisplayNode n => n.Piece,
        VersionDisplayNode v  => (object)v.Version,
        _                     => null
    };

    private IEnumerable<CanonPiece> ApplySort(IEnumerable<CanonPiece> pieces)
    {
        return (_sortColumn, _sortAscending) switch
        {
            ("Title", true)    => pieces.OrderBy(p => p.DisplayTitle, StringComparer.OrdinalIgnoreCase),
            ("Title", false)   => pieces.OrderByDescending(p => p.DisplayTitle, StringComparer.OrdinalIgnoreCase),
            ("Catalog", true)  => CatalogAsc(pieces),
            ("Catalog", false) => CatalogDesc(pieces),
            ("Category", true) => pieces
                .OrderBy(p => p.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.CatalogSortPrefix, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.CatalogSortNumber)
                .ThenBy(p => p.CatalogSortSuffix, StringComparer.OrdinalIgnoreCase),
            ("Category", false) => pieces
                .OrderByDescending(p => p.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.CatalogSortPrefix, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.CatalogSortNumber)
                .ThenBy(p => p.CatalogSortSuffix, StringComparer.OrdinalIgnoreCase),
            ("Year", true) => pieces
                .OrderBy(p => p.PublicationYear ?? int.MaxValue)
                .ThenBy(p => p.CatalogSortPrefix, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.CatalogSortNumber)
                .ThenBy(p => p.CatalogSortSuffix, StringComparer.OrdinalIgnoreCase),
            ("Year", false) => pieces
                .OrderByDescending(p => p.PublicationYear ?? 0)
                .ThenBy(p => p.CatalogSortPrefix, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.CatalogSortNumber)
                .ThenBy(p => p.CatalogSortSuffix, StringComparer.OrdinalIgnoreCase),
            _ => CatalogAsc(pieces),
        };
    }

    private static IOrderedEnumerable<CanonPiece> CatalogAsc(IEnumerable<CanonPiece> pieces) =>
        pieces.OrderBy(p => p.CatalogSortPrefix, StringComparer.OrdinalIgnoreCase)
              .ThenBy(p => p.CatalogSortNumber)
              .ThenBy(p => p.CatalogSortSuffix, StringComparer.OrdinalIgnoreCase);

    private static IOrderedEnumerable<CanonPiece> CatalogDesc(IEnumerable<CanonPiece> pieces) =>
        pieces.OrderByDescending(p => p.CatalogSortPrefix, StringComparer.OrdinalIgnoreCase)
              .ThenByDescending(p => p.CatalogSortNumber)
              .ThenByDescending(p => p.CatalogSortSuffix, StringComparer.OrdinalIgnoreCase);

    private void UpdateSortIndicators()
    {
        if (_sortIndicators == null) return;

        foreach (var (col, indicator) in _sortIndicators)
        {
            if (col == _sortColumn)
                indicator.Text = _sortAscending ? "\u25B2" : "\u25BC"; // ▲ or ▼
            else
                indicator.Text = "";
        }
    }

    private async void OnNewPieceClick(object sender, RoutedEventArgs e)
    {
        var composerCatalogs = BuildComposerCatalogDict();
        var window = new PieceEditorWindow(_vm.PickLists, _composerName,
            composerCatalogs: composerCatalogs)
        {
            Owner = this
        };

        if (window.ShowDialog() == true)
        {
            _vm.Pieces.Add(window.Piece);
            ApplyFilter();
            await SaveAllAsync();
            _vm.StatusMessage = $"Added new piece: {window.Piece.DisplayTitle}.";
        }
    }

    private async void OnPieceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item) return;
        if (!item.IsSelected) return;
        e.Handled = true;

        var composerCatalogs = BuildComposerCatalogDict();

        if (item.DataContext is CanonPiece piece)
        {
            var window = new PieceEditorWindow(_vm.PickLists, _composerName, piece,
                composerCatalogs: composerCatalogs)
            {
                Owner = this
            };

            if (window.ShowDialog() == true)
            {
                ApplyFilter(preserveExpansion: true);
                await SaveAllAsync();
                _vm.StatusMessage = $"Updated piece: {piece.DisplayTitle}.";
            }
        }
        else if (item.DataContext is SubpieceDisplayNode node)
        {
            var ancestorRoles = ParseAncestorRoles(node.ParentPiece, node.Piece);
            var window = new PieceEditorWindow(
                _vm.PickLists, node.Piece.Composer ?? "", node.Piece, null, PieceEditorMode.Subpiece,
                inheritedComposer: _composerName,
                composerCatalogs: composerCatalogs,
                ancestorRoles: ancestorRoles)
            {
                Owner = this
            };

            if (window.ShowDialog() == true)
            {
                ApplyFilter(preserveExpansion: true);
                await SaveAllAsync();
                _vm.StatusMessage = $"Updated: {node.Piece.SubpieceDisplayTitle}.";
            }
        }
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        DeletePieceButton.IsEnabled = PiecesTree.SelectedItem is CanonPiece;
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && PiecesTree.SelectedItem is CanonPiece)
        {
            e.Handled = true;
            DeleteSelectedPiece();
        }
    }

    private async void OnDeletePieceClick(object sender, RoutedEventArgs e)
    {
        DeleteSelectedPiece();
        await SaveAllAsync();
    }

    private void DeleteSelectedPiece()
    {
        if (PiecesTree.SelectedItem is not CanonPiece piece) return;

        var title = piece.DisplayTitle;
        var result = MessageBox.Show(
            $"Delete \"{title}\"?\n\nThis cannot be undone.",
            "Delete Piece",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (result != MessageBoxResult.OK) return;

        _vm.Pieces.Remove(piece);
        ApplyFilter(preserveExpansion: false);
        _vm.StatusMessage = $"Deleted: {title}.";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task SaveAllAsync()
    {
        await _vm.SavePiecesCommand.ExecuteAsync(null);
        await _vm.SavePickListsCommand.ExecuteAsync(null);
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> BuildComposerCatalogDict()
    {
        var dict = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var composer in _vm.Composers)
        {
            if (composer.CatalogPrefixes is { Count: > 0 })
                dict[composer.Name] = composer.CatalogPrefixes;
        }
        return dict;
    }

    private static IReadOnlyList<RoleEntry>? ParseAncestorRoles(
        CanonPiece? piece, CanonPiece? subpieceContext = null)
    {
        if (piece == null) return null;

        if (piece.Roles is { } roles)
        {
            var parsed = RoleEntry.ParseRoles(roles);
            if (parsed.Count > 0) return parsed;
        }

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
}
