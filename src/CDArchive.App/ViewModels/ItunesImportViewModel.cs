using System.Collections.ObjectModel;
using CDArchive.App.Services;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

public partial class ItunesImportViewModel : ObservableObject
{
    private readonly ICanonDataService _data;
    private readonly ItunesLibraryReference _itunes;
    private readonly IDialogService _dialogs;

    /// <summary>Full unfiltered set, kept around so we can re-apply the filter cheaply.</summary>
    private List<ItunesTrack> _allTracks = [];

    /// <summary>
    /// Composite identity for an iTunes-or-canon track within an album:
    /// (album-title-lowercased, disc#, track#). Built from the canon's albums
    /// once at load time; used by <see cref="ApplyFilter"/> to hide iTunes
    /// tracks whose key already exists in the canon. Trimmed to ordinal-
    /// invariant lowercase for the title so casing differences don't cause
    /// spurious mismatches.
    /// </summary>
    private HashSet<(string album, int disc, int track)> _importedKeys = new();

    [ObservableProperty]
    private ObservableCollection<ItunesTrack> _tracks = [];

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private bool _hideAlreadyImported = true;

    [ObservableProperty]
    private string _statusMessage = "Click 'Load from iTunes' to read your library.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    private bool _isBusy;

    /// <summary>Inverse of <see cref="IsBusy"/>; used for IsEnabled bindings on toolbar buttons.</summary>
    public bool IsReady => !IsBusy;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _shownCount;

    public ItunesImportViewModel(ICanonDataService data, ItunesLibraryReference itunes, IDialogService dialogs)
    {
        _data    = data;
        _itunes  = itunes;
        _dialogs = dialogs;
    }

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnHideAlreadyImportedChanged(bool value) => ApplyFilter();

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = "Reading iTunes library…";
            var loaded = await _itunes.LoadAllTracksAsync();
            // Default sort: most-recently-added first, then album, then disc, then
            // track. Stored on _allTracks so ApplyFilter's text/provisional pass
            // preserves it (LINQ where + ToList keeps the source's ordering).
            // Clicking a column header in the DataGrid still re-sorts on the fly.
            _allTracks = loaded
                .OrderByDescending(t => t.DateAdded ?? DateTime.MinValue)
                .ThenBy(t => t.Album ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.DiscNumber ?? 0)
                .ThenBy(t => t.TrackNumber ?? 0)
                .ToList();
            TotalCount = _allTracks.Count;

            // Build the "already imported" index: every canon (album, disc#, track#)
            // tuple. Hashes are fast — building this for ~22k canon tracks takes
            // microseconds, and lookups in ApplyFilter are O(1).
            var canonAlbums = await _data.LoadAlbumsAsync();
            RebuildImportedKeys(canonAlbums);

            ApplyFilter();
            StatusMessage = $"Loaded {TotalCount} tracks from iTunes.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to read iTunes library: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Command wrapper around <see cref="ImportTracksAsync"/> for the
    /// "Import Selected" toolbar button (H36, ItunesImportView slice). The
    /// XAML passes <c>TracksGrid.SelectedItems</c> as the parameter; this
    /// method projects to <see cref="ItunesTrack"/> defensively and
    /// delegates. Pre-fix the toolbar's Load button used a Command binding
    /// but this button used a code-behind <c>Click</c> handler — an
    /// inconsistency the original H36 review specifically flagged.
    /// </summary>
    [RelayCommand]
    private async Task ImportSelectedTracksAsync(System.Collections.IList? selection)
    {
        var tracks = (selection ?? Array.Empty<object>())
            .OfType<ItunesTrack>()
            .ToList();
        await ImportTracksAsync(tracks);
    }

    /// <summary>
    /// Imports the given tracks into the canon: groups by album, infers piece structure,
    /// creates provisional composers / pieces / albums as needed, and saves everything.
    /// </summary>
    public async Task ImportTracksAsync(IReadOnlyList<ItunesTrack> selected)
    {
        if (selected.Count == 0)
        {
            _dialogs.ShowInfo("No tracks selected.", "Nothing to import");
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = $"Importing {selected.Count} track(s)…";

            // Load current canon state.
            var composers   = (await _data.LoadComposersAsync()).ToList();
            var pieces      = (await _data.LoadPiecesAsync()).ToList();
            var albums      = (await _data.LoadAlbumsAsync()).ToList();
            var looseTracks = (await _data.LoadLooseTracksAsync()).ToList();

            // Run inference + entity creation (mutates composers + pieces in place).
            var result = ItunesImporter.Import(selected, composers, pieces);

            // Append the new albums + loose tracks and persist everything that changed.
            foreach (var newAlbum in result.NewAlbums)
                albums.Add(newAlbum);
            foreach (var loose in result.NewLooseTracks)
                looseTracks.Add(loose);

            // One transaction across all four subsystems. The previous 4-call
            // chain could leave the canon half-written: composers + pieces
            // persisted, then a constraint failure on albums would leave
            // orphan composers/pieces visible while the UI reported "Import
            // failed". SaveBatchAsync rolls everything back on any failure.
            await _data.SaveBatchAsync(composers, pieces, albums, looseTracks);

            // Refresh the "already imported" index so the just-imported tracks
            // drop out of the visible list on the next ApplyFilter pass (and
            // immediately, via the call below).
            RebuildImportedKeys(albums);
            ApplyFilter();

            StatusMessage =
                $"Imported {result.TracksImported} tracks: "
                + $"{result.NewAlbums.Count} new album(s), "
                + $"{result.NewLooseTracks.Count} loose track(s), "
                + $"{result.NewComposers} composer(s), "
                + $"{result.NewPieces} piece(s), "
                + $"{result.NewSubpieces} subpiece(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import failed: {ex.Message}";
            _dialogs.ShowError(ex.ToString(), "Import error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Repopulates <see cref="_importedKeys"/> with every (album-title-lowercased,
    /// disc#, track#) tuple from the supplied canon albums. Called on load and
    /// after every successful import so the visible iTunes list reflects what's
    /// already in the canon.
    /// </summary>
    private void RebuildImportedKeys(IEnumerable<CanonAlbum> canonAlbums)
    {
        _importedKeys = new HashSet<(string, int, int)>();
        foreach (var album in canonAlbums)
        {
            var title = (album.Title ?? "").Trim().ToLowerInvariant();
            if (title.Length == 0) continue;
            foreach (var disc in album.Discs)
                foreach (var track in disc.Tracks)
                    _importedKeys.Add((title, disc.DiscNumber, track.TrackNumber));
        }
    }

    private void ApplyFilter()
    {
        var f = Filter.Trim();
        IEnumerable<ItunesTrack> filtered = _allTracks;

        if (!string.IsNullOrEmpty(f))
        {
            filtered = filtered.Where(t =>
                (t.Name      ?? "").Contains(f, StringComparison.OrdinalIgnoreCase) ||
                (t.Album     ?? "").Contains(f, StringComparison.OrdinalIgnoreCase) ||
                (t.Composer  ?? "").Contains(f, StringComparison.OrdinalIgnoreCase) ||
                (t.Artist    ?? "").Contains(f, StringComparison.OrdinalIgnoreCase) ||
                (t.Genre     ?? "").Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        int hiddenAsImported = 0;
        if (HideAlreadyImported && _importedKeys.Count > 0)
        {
            filtered = filtered.Where(t =>
            {
                if (string.IsNullOrWhiteSpace(t.Album)) return true;
                var key = (t.Album.Trim().ToLowerInvariant(),
                           t.DiscNumber ?? 1,
                           t.TrackNumber ?? 0);
                if (_importedKeys.Contains(key))
                {
                    hiddenAsImported++;
                    return false;
                }
                return true;
            });
        }

        var list = filtered.ToList();
        Tracks = new ObservableCollection<ItunesTrack>(list);
        ShownCount = list.Count;

        if (HideAlreadyImported && hiddenAsImported > 0)
            StatusMessage = $"Showing {list.Count} of {TotalCount} tracks ({hiddenAsImported} already in canon).";
        else
            StatusMessage = $"Showing {list.Count} of {TotalCount} tracks.";
    }
}
