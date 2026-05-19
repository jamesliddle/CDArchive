using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

public interface ICanonDataService
{
    string ComposersFilePath { get; }
    string PiecesFilePath { get; }
    string AlbumsFilePath { get; }
    string LooseTracksFilePath { get; }

    Task<List<CanonComposer>> LoadComposersAsync();
    Task<List<CanonPiece>> LoadPiecesAsync();
    Task SaveComposersAsync(List<CanonComposer> composers);
    Task SavePiecesAsync(List<CanonPiece> pieces);

    Task<List<CanonAlbum>> LoadAlbumsAsync();
    Task SaveAlbumsAsync(List<CanonAlbum> albums);

    /// <summary>
    /// Loose tracks — singletons that don't belong to any album. Stored in
    /// the same <c>album_tracks</c> table as album-bound tracks, but with
    /// <c>disc_id NULL</c>. JSON snapshot lives at <see cref="LooseTracksFilePath"/>.
    /// </summary>
    Task<List<AlbumTrack>> LoadLooseTracksAsync();
    Task SaveLooseTracksAsync(List<AlbumTrack> tracks);

    Task<CanonPickLists> LoadPickListsAsync();
    Task SavePickListsAsync(CanonPickLists pickLists);

    /// <summary>
    /// Save any subset of the four cross-referenced subsystems atomically. All
    /// non-null inputs persist inside a single <see cref="CanonDbContext"/> +
    /// <c>BeginTransactionAsync</c>, so a failure on any one rolls every
    /// preceding write back. Use this for any flow that would otherwise chain
    /// two or more <c>Save*Async</c> calls (e.g. iTunes import, Tracks save) —
    /// the chained form leaves the canon partially written on mid-sequence
    /// failure. Order applied: composers → pieces → albums → loose tracks
    /// (data-dependency order; piece refs need composers and pieces).
    /// </summary>
    Task SaveBatchAsync(
        List<CanonComposer>? composers = null,
        List<CanonPiece>? pieces = null,
        List<CanonAlbum>? albums = null,
        List<AlbumTrack>? looseTracks = null);
}
