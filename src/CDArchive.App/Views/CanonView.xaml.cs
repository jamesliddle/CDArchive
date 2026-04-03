using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class CanonView : UserControl
{
    // Sort state
    private string _sortColumn = "Name";
    private bool _sortAscending = true;
    private Dictionary<string, TextBlock> _sortIndicators = null!;

    public CanonView()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            _sortIndicators = new Dictionary<string, TextBlock>
            {
                ["Pieces"] = SortPieces,
                ["Name"] = SortName,
                ["Birth"] = SortBirth,
                ["Death"] = SortDeath,
            };
            UpdateSortIndicators();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is CanonViewModel vm && vm.Composers.Count == 0)
        {
            await vm.LoadDataCommand.ExecuteAsync(null);
            UpdatePieceCounts(vm);
            ApplySortedFilter(vm);
        }
    }

    private void OnColumnHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not string column) return;
        if (DataContext is not CanonViewModel vm) return;

        if (_sortColumn == column)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }

        ApplySortedFilter(vm);
    }

    /// <summary>
    /// Filters and sorts the composer list, replacing the ViewModel's default filter.
    /// </summary>
    private void ApplySortedFilter(CanonViewModel vm)
    {
        var filter = vm.ComposerFilter.Trim();
        IEnumerable<CanonComposer> filtered = string.IsNullOrEmpty(filter)
            ? vm.Composers
            : vm.Composers.Where(c =>
                c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.SortName.Contains(filter, StringComparison.OrdinalIgnoreCase));

        filtered = ApplySort(filtered);

        vm.FilteredComposers = new ObservableCollection<CanonComposer>(filtered.ToList());
        UpdateSortIndicators();
    }

    private IEnumerable<CanonComposer> ApplySort(IEnumerable<CanonComposer> composers)
    {
        return (_sortColumn, _sortAscending) switch
        {
            ("Pieces", true) => composers.OrderBy(c => c.PieceCount)
                                         .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase),
            ("Pieces", false) => composers.OrderByDescending(c => c.PieceCount)
                                          .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase),
            ("Name", true) => composers.OrderBy(c => !string.IsNullOrEmpty(c.SortName) ? c.SortName : c.Name,
                                                StringComparer.OrdinalIgnoreCase),
            ("Name", false) => composers.OrderByDescending(c => !string.IsNullOrEmpty(c.SortName) ? c.SortName : c.Name,
                                                           StringComparer.OrdinalIgnoreCase),
            ("Birth", true) => composers.OrderBy(c => c.BirthYearSort)
                                        .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase),
            ("Birth", false) => composers.OrderByDescending(c => c.BirthYearSort)
                                         .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase),
            ("Death", true) => composers.OrderBy(c => c.DeathYearSort)
                                        .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase),
            ("Death", false) => composers.OrderByDescending(c => c.DeathYearSort)
                                         .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase),
            _ => composers.OrderBy(c => c.SortName, StringComparer.OrdinalIgnoreCase),
        };
    }

    private void UpdateSortIndicators()
    {
        if (_sortIndicators == null) return;
        foreach (var (col, indicator) in _sortIndicators)
            indicator.Text = col == _sortColumn ? (_sortAscending ? "\u25B2" : "\u25BC") : "";
    }

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
            vm.SelectedComposer = window.Composer;
            await vm.SaveComposersCommand.ExecuteAsync(null);
            vm.StatusMessage = $"Added {window.Composer.Name}.";
        }
    }

    private async void OnComposerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not CanonViewModel vm) return;
        if (vm.SelectedComposer == null) return;

        var window = new ComposerEditorWindow(vm.SelectedComposer)
        {
            Owner = Window.GetWindow(this)
        };

        if (window.ShowDialog() == true)
        {
            ApplySortedFilter(vm);
            await vm.SaveComposersCommand.ExecuteAsync(null);
            vm.StatusMessage = $"Updated {vm.SelectedComposer.Name}.";
        }
    }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is CanonViewModel vm)
            ApplySortedFilter(vm);
    }

    private void OnComposerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is CanonViewModel vm)
        {
            PiecesButton.IsEnabled = vm.SelectedComposer != null;
        }
    }

    private void OnPiecesClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CanonViewModel vm) return;
        if (vm.SelectedComposer == null) return;

        var window = new PiecesWindow(vm, vm.SelectedComposer.Name)
        {
            Owner = Window.GetWindow(this)
        };

        window.ShowDialog();

        // Refresh piece counts after the modal closes (pieces may have been added/removed)
        UpdatePieceCounts(vm);
        ApplySortedFilter(vm);
    }

    /// <summary>
    /// Opens the pick list editor standalone.
    /// </summary>
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
            ApplyRenames(vm, editor);
            await SaveAllAsync(vm);
        }
    }

    private static void ApplyRenames(CanonViewModel vm, PickListEditorWindow editor)
    {
        var formRenames = editor.FormRenames;
        var categoryRenames = editor.CategoryRenames;
        var catalogRenames = editor.CatalogRenames;
        var keyRenames = editor.KeyRenames;

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
