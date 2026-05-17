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
}
