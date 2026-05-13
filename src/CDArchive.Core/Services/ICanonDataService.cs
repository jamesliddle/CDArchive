using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

public interface ICanonDataService
{
    string ComposersFilePath { get; }
    string PiecesFilePath { get; }
    string AlbumsFilePath { get; }

    Task<List<CanonComposer>> LoadComposersAsync();
    Task<List<CanonPiece>> LoadPiecesAsync();
    Task SaveComposersAsync(List<CanonComposer> composers);
    Task SavePiecesAsync(List<CanonPiece> pieces);

    Task<List<CanonAlbum>> LoadAlbumsAsync();
    Task SaveAlbumsAsync(List<CanonAlbum> albums);

    Task<CanonPickLists> LoadPickListsAsync();
    Task SavePickListsAsync(CanonPickLists pickLists);
}
