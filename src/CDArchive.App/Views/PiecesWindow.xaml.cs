using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

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

    private void ApplyFilter()
    {
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
    }

    private IEnumerable<CanonPiece> ApplySort(IEnumerable<CanonPiece> pieces)
    {
        return (_sortColumn, _sortAscending) switch
        {
            ("Title", true) => pieces.OrderBy(p => p.DisplayTitle, StringComparer.OrdinalIgnoreCase),
            ("Title", false) => pieces.OrderByDescending(p => p.DisplayTitle, StringComparer.OrdinalIgnoreCase),
            ("Catalog", true) => CatalogAsc(pieces),
            ("Catalog", false) => CatalogDesc(pieces),
            ("Category", true) => CatalogAsc(pieces.OrderBy(p => p.Category, StringComparer.OrdinalIgnoreCase)),
            ("Category", false) => CatalogAsc(pieces.OrderByDescending(p => p.Category, StringComparer.OrdinalIgnoreCase)),
            ("Year", true) => CatalogAsc(pieces.OrderBy(p => p.PublicationYear ?? int.MaxValue)),
            ("Year", false) => CatalogAsc(pieces.OrderByDescending(p => p.PublicationYear ?? 0)),
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
        var window = new PieceEditorWindow(_vm.PickLists, _composerName)
        {
            Owner = this
        };

        if (window.ShowDialog() == true)
        {
            _vm.Pieces.Add(window.Piece);
            ApplyRenamesFromPieceEditor(window);
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

        if (item.DataContext is CanonPiece piece)
        {
            var window = new PieceEditorWindow(_vm.PickLists, _composerName, piece)
            {
                Owner = this
            };

            if (window.ShowDialog() == true)
            {
                ApplyRenamesFromPieceEditor(window);
                ApplyFilter();
                await SaveAllAsync();
                _vm.StatusMessage = $"Updated piece: {piece.DisplayTitle}.";
            }
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ApplyRenamesFromPieceEditor(PieceEditorWindow window)
    {
        if (window.FormRenames.Count == 0 && window.CategoryRenames.Count == 0 &&
            window.CatalogRenames.Count == 0 && window.KeyRenames.Count == 0)
            return;

        var count = 0;
        foreach (var piece in _vm.Pieces)
            count += ApplyRenamesToPiece(piece,
                window.FormRenames, window.CategoryRenames,
                window.CatalogRenames, window.KeyRenames);

        if (count > 0)
            _vm.StatusMessage = $"Renamed values propagated to {count} field(s). Save to persist.";
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
        {
            piece.Form = newForm;
            count++;
        }

        if (piece.InstrumentationCategory != null &&
            categoryRenames.TryGetValue(piece.InstrumentationCategory, out var newCat))
        {
            piece.InstrumentationCategory = newCat;
            count++;
        }

        if (piece.KeyTonality != null && keyRenames.TryGetValue(piece.KeyTonality, out var newKey))
        {
            piece.KeyTonality = newKey;
            count++;
        }

        if (piece.CatalogInfo != null)
        {
            foreach (var cat in piece.CatalogInfo)
            {
                if (catalogRenames.TryGetValue(cat.Catalog, out var newPrefix))
                {
                    cat.Catalog = newPrefix;
                    count++;
                }
            }
        }

        if (piece.Subpieces != null)
        {
            foreach (var sub in piece.Subpieces)
                count += ApplyRenamesToPiece(sub, formRenames, categoryRenames, catalogRenames, keyRenames);
        }

        return count;
    }

    private async Task SaveAllAsync()
    {
        await _vm.SavePiecesCommand.ExecuteAsync(null);
        await _vm.SavePickListsCommand.ExecuteAsync(null);
    }
}
