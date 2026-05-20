using System.Text.RegularExpressions;
using CDArchive.Core.Helpers;
using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

public class ArchiveScannerService : IArchiveScannerService
{
    private static readonly Regex DiscFolderRegex = new(@"^Disc \d+(-\d+)?$", RegexOptions.Compiled);

    private readonly IArchiveSettings _settings;
    private readonly IFileSystemService _fs;

    public ArchiveScannerService(IArchiveSettings settings, IFileSystemService fs)
    {
        _settings = settings;
        _fs = fs;
    }

    public Task<List<AlbumInfo>> ScanArchiveAsync(CancellationToken ct = default)
    {
        // Rework H28: pre-fix this method built the result list synchronously
        // on the calling thread and wrapped the return in Task.FromResult —
        // so the signature was async-in-name-only. At the 3,000-CD target on
        // a slow drive (network share, USB, sleepy disk) it froze the UI
        // thread for tens of seconds with no await yielding. Task.Run hops
        // the directory enumeration off-thread, matching ValidateArchiveAsync's
        // pattern below.
        return Task.Run(() =>
        {
            var albums = new List<AlbumInfo>();
            var archiveRoot = _settings.ArchiveRootPath;

            if (!_fs.DirectoryExists(archiveRoot))
                return albums;

            foreach (var albumDir in _fs.EnumerateDirectories(archiveRoot))
            {
                ct.ThrowIfCancellationRequested();

                var albumName = _fs.GetFileName(albumDir);
                if (ShouldSkip(albumName))
                    continue;
                var subDirs = _fs.EnumerateDirectories(albumDir).ToList();
                var subDirNames = subDirs.Select(d => _fs.GetFileName(d)).ToList();

                // Rework H29: numeric disc-folder sort. Pre-fix .OrderBy(d => d)
                // landed "Disc 10" between "Disc 1" and "Disc 2"; any 10+ disc
                // box set scanned in the wrong order, with disc-number
                // assignment below following the (wrong) ordering.
                var discDirs = DiscFolderOrdering.OrderByDiscNumber(
                        subDirs.Where(d => DiscFolderRegex.IsMatch(_fs.GetFileName(d))))
                    .ToList();

                bool hasFlacFolder = subDirNames.Contains("FLAC", StringComparer.OrdinalIgnoreCase);
                bool hasMp3Folder = subDirNames.Contains("MP3", StringComparer.OrdinalIgnoreCase);
                bool isMultiDisc = discDirs.Count > 0;

                var album = new AlbumInfo
                {
                    Name = albumName,
                    FullPath = albumDir
                };

                if (isMultiDisc)
                {
                    album.DiscCount = discDirs.Count;
                    int discNumber = 1;
                    foreach (var discDir in discDirs)
                    {
                        var disc = ScanDisc(discDir, discNumber);
                        album.Discs.Add(disc);
                        discNumber++;
                    }
                }
                else
                {
                    album.DiscCount = 1;
                    var disc = ScanDisc(albumDir, 1);
                    disc.FolderName = albumName;
                    album.Discs.Add(disc);
                }

                albums.Add(album);
            }

            return albums;
        }, ct);
    }

    private static bool ShouldSkip(string name) =>
        name.StartsWith("aa", StringComparison.Ordinal);

    private DiscInfo ScanDisc(string discPath, int discNumber)
    {
        var disc = new DiscInfo
        {
            DiscNumber = discNumber,
            FolderName = _fs.GetFileName(discPath),
            FullPath = discPath
        };

        var flacDir = _fs.CombinePath(discPath, "FLAC");
        var mp3Dir = _fs.CombinePath(discPath, "MP3");

        if (_fs.DirectoryExists(flacDir))
        {
            disc.HasFlacFolder = true;
            foreach (var file in _fs.EnumerateFiles(flacDir, "*.flac")
                .Where(f => !ShouldSkip(_fs.GetFileName(f))))
            {
                disc.FlacTracks.Add(new TrackInfo
                {
                    FileName = _fs.GetFileName(file),
                    FullPath = file,
                    Format = AudioFormat.Flac
                });
            }
        }

        if (_fs.DirectoryExists(mp3Dir))
        {
            disc.HasMp3Folder = true;
            foreach (var file in _fs.EnumerateFiles(mp3Dir, "*.mp3")
                .Where(f => !ShouldSkip(_fs.GetFileName(f))))
            {
                disc.Mp3Tracks.Add(new TrackInfo
                {
                    FileName = _fs.GetFileName(file),
                    FullPath = file,
                    Format = AudioFormat.Mp3
                });
            }
        }

        return disc;
    }

    public Task<List<ValidationResult>> ValidateArchiveAsync(
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        // Directory enumeration over a large archive is CPU/IO bound and
        // would otherwise block the UI thread; Task.Run hops it off-thread
        // so the caller's await actually yields.
        return Task.Run(() =>
        {
            var results = new List<ValidationResult>();
            var archiveRoot = _settings.ArchiveRootPath;

            if (!_fs.DirectoryExists(archiveRoot))
                return results;

            var albumDirs = _fs.EnumerateDirectories(archiveRoot)
                .Where(d => !ShouldSkip(_fs.GetFileName(d)))
                .ToList();
            int totalAlbums = albumDirs.Count;

            for (int i = 0; i < totalAlbums; i++)
            {
                ct.ThrowIfCancellationRequested();

                var albumDir = albumDirs[i];
                var albumName = _fs.GetFileName(albumDir);
                var result = new ValidationResult
                {
                    AlbumPath = albumDir,
                    AlbumName = albumName
                };

                ValidateAlbum(albumDir, result);

                if (result.Issues.Count > 0)
                    results.Add(result);

                progress?.Report(totalAlbums == 0 ? 100 : (int)((i + 1) * 100.0 / totalAlbums));
            }

            return results;
        }, ct);
    }

    private void ValidateAlbum(string albumDir, ValidationResult result)
    {
        var subDirs = _fs.EnumerateDirectories(albumDir).ToList();
        var subDirNames = subDirs.Select(d => _fs.GetFileName(d)).ToList();

        var discDirs = subDirs
            .Where(d => DiscFolderRegex.IsMatch(_fs.GetFileName(d)))
            .ToList();

        bool hasFlacFolder = subDirNames.Any(n => n.Equals("FLAC", StringComparison.OrdinalIgnoreCase));
        bool hasMp3Folder = subDirNames.Any(n => n.Equals("MP3", StringComparison.OrdinalIgnoreCase));
        bool hasDirectAudio = hasFlacFolder || hasMp3Folder;
        bool hasDiscFolders = discDirs.Count > 0;

        // Rule: must not mix direct audio folders with disc subfolders
        if (hasDirectAudio && hasDiscFolders)
        {
            result.Issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "Album has both direct FLAC/MP3 folders and Disc subfolders. Use one structure or the other.",
                albumDir));
        }

        // Rule: must have either direct audio or disc subfolders
        if (!hasDirectAudio && !hasDiscFolders)
        {
            result.Issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "Album has neither FLAC/MP3 folders nor Disc subfolders.",
                albumDir));
        }

        // Validate disc folder naming consistency
        if (hasDiscFolders)
        {
            ValidateDiscNaming(discDirs, result);

            foreach (var discDir in discDirs)
            {
                ValidateDiscContents(discDir, result);
            }
        }

        if (hasDirectAudio && !hasDiscFolders)
        {
            ValidateDiscContents(albumDir, result);
        }

        // Flag unexpected subfolders
        var expectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FLAC", "MP3" };
        foreach (var dir in subDirs)
        {
            var name = _fs.GetFileName(dir);
            if (!expectedNames.Contains(name) && !DiscFolderRegex.IsMatch(name))
            {
                result.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    $"Unexpected subfolder: {name}",
                    dir));
            }
        }
    }

    private void ValidateDiscNaming(List<string> discDirs, ValidationResult result)
    {
        var names = discDirs.Select(d => _fs.GetFileName(d)).ToList();

        // Check consistent padding
        bool hasPadded = names.Any(n => Regex.IsMatch(n, @"^Disc \d{2,}$"));
        bool hasUnpadded = names.Any(n => Regex.IsMatch(n, @"^Disc \d$"));

        if (hasPadded && hasUnpadded)
        {
            result.Issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "Inconsistent disc folder naming: mix of padded and unpadded numbers.",
                result.AlbumPath));
        }
    }

    private void ValidateDiscContents(string discPath, ValidationResult result)
    {
        var flacDir = _fs.CombinePath(discPath, "FLAC");
        var mp3Dir = _fs.CombinePath(discPath, "MP3");

        bool hasFlac = _fs.DirectoryExists(flacDir);
        bool hasMp3 = _fs.DirectoryExists(mp3Dir);

        if (!hasFlac && !hasMp3)
        {
            result.Issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "Disc folder has no FLAC or MP3 subfolder.",
                discPath));
        }

        // Flag empty FLAC/MP3 folders
        if (hasFlac && !_fs.EnumerateFiles(flacDir, "*.flac").Any())
        {
            result.Issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "FLAC folder is empty.",
                flacDir));
        }

        if (hasMp3 && !_fs.EnumerateFiles(mp3Dir, "*.mp3").Any())
        {
            result.Issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "MP3 folder is empty.",
                mp3Dir));
        }
    }
}
