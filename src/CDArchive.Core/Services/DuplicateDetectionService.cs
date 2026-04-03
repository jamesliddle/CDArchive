using CDArchive.Core.Helpers;

namespace CDArchive.Core.Services;

public class DuplicateDetectionService : IDuplicateDetectionService
{
    private readonly IArchiveSettings _settings;
    private readonly IFileSystemService _fs;

    public DuplicateDetectionService(IArchiveSettings settings, IFileSystemService fs)
    {
        _settings = settings;
        _fs = fs;
    }

    public List<string> FindPotentialDuplicates(string albumName)
    {
        var results = new List<string>();
        var archiveRoot = _settings.ArchiveRootPath;

        if (!_fs.DirectoryExists(archiveRoot))
            return results;

        var normalizedInput = StringSimilarity.Normalize(albumName);
        var inputPath = _fs.CombinePath(archiveRoot, albumName);

        foreach (var dir in _fs.EnumerateDirectories(archiveRoot))
        {
            // Don't return the album itself
            if (string.Equals(dir, inputPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var dirName = _fs.GetFileName(dir);
            var normalizedDir = StringSimilarity.Normalize(dirName);

            if (string.IsNullOrEmpty(normalizedInput) || string.IsNullOrEmpty(normalizedDir))
                continue;

            bool isMatch = normalizedInput == normalizedDir
                || normalizedInput.Contains(normalizedDir)
                || normalizedDir.Contains(normalizedInput)
                || StringSimilarity.LevenshteinDistance(normalizedInput, normalizedDir) <= 3;

            if (isMatch)
                results.Add(dir);
        }

        return results;
    }
}
