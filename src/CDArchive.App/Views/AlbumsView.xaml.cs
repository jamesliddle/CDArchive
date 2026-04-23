using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDArchive.App.Helpers;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class AlbumsView : UserControl
{
    // ── Column sort state ─────────────────────────────────────────────────────

    private GridViewColumnHeader? _lastSortHeader;

    // Maps the base header text (no arrow) to the CanonAlbum property name used for sorting.
    private static readonly Dictionary<string, string> SortKeys = new()
    {
        ["Title"]      = "DisplayTitle",
        ["Label"]      = "Label",
        ["Cat. No."]   = "CatalogueNumber",
        ["Performers"] = "PerformerSummary",
        ["SPARS"]      = "SparsCode",
        ["Discs"]      = "DiscCount",
        ["Tracks"]     = "TotalTrackCount",
    };

    public AlbumsView()
    {
        InitializeComponent();
    }

    // ── Initial load ─────────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AlbumsViewModel vm)
            await vm.LoadDataCommand.ExecuteAsync(null);
    }

    // ── Toolbar: filter ───────────────────────────────────────────────────────

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is AlbumsViewModel vm)
            vm.FilterText = FilterBox.Text;
    }

    // ── Toolbar: refresh ──────────────────────────────────────────────────────

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AlbumsViewModel vm)
            await vm.LoadDataCommand.ExecuteAsync(null);
    }

    // ── List selection ────────────────────────────────────────────────────────

    private void OnAlbumSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = AlbumList.SelectedItems.Count;
        EditButton.IsEnabled   = count > 0;
        DeleteButton.IsEnabled = count > 0;
    }

    // ── Double-click → edit (single item only) ───────────────────────────────

    private async void OnAlbumDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // MouseDoubleClick bubbles — verify the click originated on a row, not on
        // scroll chrome (scrollbar arrows, track, etc.) or the column header area.
        var hit = e.OriginalSource as DependencyObject;
        if (hit == null) return;
        if (hit.FindAncestorOrSelf<ListViewItem>() == null) return;

        if (AlbumList.SelectedItems.Count != 1) return;
        e.Handled = true;
        await EditSelectedAlbumsAsync();
    }

    // ── Toolbar: New / Edit / Delete ──────────────────────────────────────────

    private async void OnNewAlbumClick(object sender, RoutedEventArgs e) =>
        await NewAlbumAsync();

    private async void OnEditAlbumClick(object sender, RoutedEventArgs e) =>
        await EditSelectedAlbumsAsync();

    private async void OnDeleteAlbumClick(object sender, RoutedEventArgs e) =>
        await DeleteSelectedAlbumsAsync();

    // ── Column header sort ────────────────────────────────────────────────────

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header) return;
        if (header.Role == GridViewColumnHeaderRole.Padding) return;  // far-right filler
        if (DataContext is not AlbumsViewModel vm) return;

        // Strip any existing arrow from the content to get the base name
        var baseName = (header.Content as string ?? "").TrimEnd().TrimEnd('↑', '↓').TrimEnd();
        if (!SortKeys.TryGetValue(baseName, out var sortKey)) return;

        if (ReferenceEquals(_lastSortHeader, header))
        {
            // Same column: flip direction
            vm.SortAscending = !vm.SortAscending;
        }
        else
        {
            // Different column: reset previous header, start ascending
            if (_lastSortHeader != null)
            {
                var old = (_lastSortHeader.Content as string ?? "").TrimEnd().TrimEnd('↑', '↓').TrimEnd();
                _lastSortHeader.Content = old;
            }
            vm.SortAscending = true;
            _lastSortHeader  = header;
        }

        header.Content   = baseName + (vm.SortAscending ? " ↑" : " ↓");
        vm.SortColumn    = sortKey;
        vm.ApplyFilter();
    }

    // ── Consistency check ─────────────────────────────────────────────────────

    private async void OnCheckReferencesClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AlbumsViewModel vm) return;

        if (vm.AllAlbums.Count == 0)
        {
            MessageBox.Show("No albums loaded.", "Check References",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var report = await vm.RunConsistencyCheckAsync();
        MessageBox.Show(report, "Check Album References",
            MessageBoxButton.OK,
            report.StartsWith("All") ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    // ── New album ─────────────────────────────────────────────────────────────

    private async Task NewAlbumAsync()
    {
        if (DataContext is not AlbumsViewModel vm) return;

        var (pieces, pickLists) = await vm.LoadEditorDataAsync();
        var dlg = new AlbumEditorWindow(pickLists, pieces)
        {
            Owner = Window.GetWindow(this)
        };

        if (dlg.ShowDialog() != true || dlg.Result is not CanonAlbum result) return;

        vm.AllAlbums.Add(result);
        vm.ApplyFilter();
        await vm.SaveAsync();
    }

    // ── Edit selected album(s) ────────────────────────────────────────────────

    private async Task EditSelectedAlbumsAsync()
    {
        if (DataContext is not AlbumsViewModel vm) return;

        var selected = AlbumList.SelectedItems.Cast<CanonAlbum>().ToList();
        if (selected.Count == 0) return;

        var (pieces, pickLists) = await vm.LoadEditorDataAsync();

        if (selected.Count == 1)
        {
            // ── Single album ──────────────────────────────────────────────────
            var album = selected[0];
            var dlg = new AlbumEditorWindow(pickLists, pieces, album)
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() != true || dlg.Result is not CanonAlbum result) return;

            var idx = vm.AllAlbums.IndexOf(album);
            if (idx >= 0) vm.AllAlbums[idx] = result;
            else          vm.AllAlbums.Add(result);
        }
        else
        {
            // ── Multiple albums ───────────────────────────────────────────────
            // The editor modifies the albums in-place; no Result needed.
            var dlg = new AlbumEditorWindow(pickLists, selected, pieces)
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() != true) return;
        }

        vm.ApplyFilter();
        await vm.SaveAsync();

        // ApplyFilter() replaces vm.Albums with a new ObservableCollection, which causes
        // WPF to clear the multi-selection down to at most one item (the SelectedItem
        // binding restores only vm.SelectedAlbum).  Re-add every previously selected
        // album that still appears in the filtered list so the user can see which albums
        // were affected.
        if (selected.Count > 1)
        {
            foreach (var a in selected)
            {
                if (vm.Albums.Contains(a) && !AlbumList.SelectedItems.Contains(a))
                    AlbumList.SelectedItems.Add(a);
            }
        }
    }

    // ── Delete selected album(s) ──────────────────────────────────────────────

    private async Task DeleteSelectedAlbumsAsync()
    {
        if (DataContext is not AlbumsViewModel vm) return;

        var selected = AlbumList.SelectedItems.Cast<CanonAlbum>().ToList();
        if (selected.Count == 0) return;

        string prompt;
        if (selected.Count == 1)
        {
            var a = selected[0];
            var label = string.IsNullOrWhiteSpace(a.Label)
                ? $"\"{a.DisplayTitle}\""
                : $"\"{a.Label} {a.CatalogueNumber}\"";
            prompt = $"Delete {label}?\n\nThis cannot be undone.";
        }
        else
        {
            prompt = $"Delete {selected.Count} albums?\n\nThis cannot be undone.";
        }

        var result = MessageBox.Show(prompt, "Delete Album",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

        if (result != MessageBoxResult.OK) return;

        foreach (var album in selected)
            vm.AllAlbums.Remove(album);

        vm.ApplyFilter();
        await vm.SaveAsync();
    }
}
