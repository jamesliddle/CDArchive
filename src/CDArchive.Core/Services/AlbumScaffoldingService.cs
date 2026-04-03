using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

public class AlbumScaffoldingService : IAlbumScaffoldingService
{
    private readonly IArchiveSettings _settings;
    private readonly IFileSystemService _fs;

    public AlbumScaffoldingService(IArchiveSettings settings, IFileSystemService fs)
    {
        _settings = settings;
        _fs = fs;
    }

    public string GetAlbumPath(string albumName) =>
        _fs.CombinePath(_settings.ArchiveRootPath, albumName);

    public string GetDiscFolderName(int discNumber, int totalDiscs)
    {
        if (totalDiscs >= 10)
            return $"Disc {discNumber:D2}";

        return $"Disc {discNumber}";
    }

    public AlbumInfo CreateAlbumStructure(string albumName, int discCount)
    {
        var albumPath = GetAlbumPath(albumName);

        if (!_fs.DirectoryExists(albumPath))
            _fs.CreateDirectory(albumPath);

        var album = new AlbumInfo
        {
            Name = albumName,
            FullPath = albumPath,
            DiscCount = discCount
        };

        if (discCount == 1)
        {
            var flacPath = _fs.CombinePath(albumPath, "FLAC");
            var mp3Path = _fs.CombinePath(albumPath, "MP3");

            _fs.CreateDirectory(flacPath);
            _fs.CreateDirectory(mp3Path);

            album.Discs.Add(new DiscInfo
            {
                DiscNumber = 1,
                FolderName = albumName,
                FullPath = albumPath,
                HasFlacFolder = true,
                HasMp3Folder = true
            });
        }
        else
        {
            for (int i = 1; i <= discCount; i++)
            {
                var discFolder = GetDiscFolderName(i, discCount);
                var discPath = _fs.CombinePath(albumPath, discFolder);
                var flacPath = _fs.CombinePath(discPath, "FLAC");
                var mp3Path = _fs.CombinePath(discPath, "MP3");

                _fs.CreateDirectory(flacPath);
                _fs.CreateDirectory(mp3Path);

                album.Discs.Add(new DiscInfo
                {
                    DiscNumber = i,
                    FolderName = discFolder,
                    FullPath = discPath,
                    HasFlacFolder = true,
                    HasMp3Folder = true
                });
            }
        }

        return album;
    }
}
