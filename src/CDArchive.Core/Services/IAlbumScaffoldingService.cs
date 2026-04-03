using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

public interface IAlbumScaffoldingService
{
    AlbumInfo CreateAlbumStructure(string albumName, int discCount);
    string GetAlbumPath(string albumName);
    string GetDiscFolderName(int discNumber, int totalDiscs);
}
