using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests.Services;

/// <summary>
/// Rework H28 + H29 regression: ArchiveScannerService.ScanArchiveAsync
/// now wraps its disk walk in <c>Task.Run</c> (H28) and orders disc
/// folders numerically (H29). Verified end-to-end via a fake
/// <see cref="IFileSystemService"/> so the test is deterministic and
/// doesn't depend on the real filesystem.
/// </summary>
public class ArchiveScannerServiceTests
{
    private sealed class FakeSettings : IArchiveSettings
    {
        public string ArchiveRootPath { get; set; } = @"C:\Archive";
        public string FfmpegPath { get; set; } = "ffmpeg";
        public int Mp3Bitrate { get; set; } = 320;
        public PreferredAudioFormat PreferredAudioFormat { get; set; } = PreferredAudioFormat.Flac;
        public float PlayerVolume { get; set; } = 1.0f;
        public void Save() { }
        public void Initialize() { }
    }

    /// <summary>
    /// In-memory file system stub: maps each directory path to its list
    /// of immediate subdirectories and files. Enough surface for the
    /// scanner; doesn't try to be a general FS abstraction.
    /// </summary>
    private sealed class FakeFs : IFileSystemService
    {
        public Dictionary<string, List<string>> Dirs  { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool DirectoryExists(string path) => Dirs.ContainsKey(path);
        public void CreateDirectory(string path) => Dirs.TryAdd(path, new List<string>());
        public IEnumerable<string> EnumerateDirectories(string path) =>
            Dirs.TryGetValue(path, out var list) ? list : Enumerable.Empty<string>();
        public IEnumerable<string> EnumerateFiles(string path, string searchPattern)
        {
            if (!Files.TryGetValue(path, out var list)) return Enumerable.Empty<string>();
            // We only ever pass "*.flac" or "*.mp3"; the FakeFs files are
            // stored already-typed so a contains check on the extension
            // works without parsing the glob.
            var ext = "." + searchPattern.TrimStart('*').TrimStart('.');
            return list.Where(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        }
        public string GetFileName(string path)
        {
            var sep = path.LastIndexOf('\\');
            return sep < 0 ? path : path[(sep + 1)..];
        }
        public string GetFileNameWithoutExtension(string path)
        {
            var name = GetFileName(path);
            var dot = name.LastIndexOf('.');
            return dot < 0 ? name : name[..dot];
        }
        public string GetDirectoryName(string path)
        {
            var sep = path.LastIndexOf('\\');
            return sep < 0 ? "" : path[..sep];
        }
        public string CombinePath(params string[] paths) => string.Join('\\', paths);

        public void AddDir(string path, params string[] children)
        {
            Dirs[path] = new List<string>(children);
            foreach (var c in children)
                Dirs.TryAdd(c, new List<string>());
        }
        public void AddFiles(string path, params string[] fileNames)
        {
            Files[path] = fileNames.Select(f => path + "\\" + f).ToList();
        }
    }

    /// <summary>
    /// The H29 regression: pre-fix, a 12-disc box-set scanned with
    /// <c>.OrderBy(d => d)</c> got "Disc 10" / "Disc 11" / "Disc 12" between
    /// "Disc 1" and "Disc 2" — and since the loop counter assigns
    /// DiscNumber 1..N in iteration order, the on-disk Disc 2 ended up
    /// labelled DiscNumber 5 (or wherever it landed lexicographically).
    /// </summary>
    [Fact]
    public async Task ScanArchiveAsync_TwelveDiscAlbum_AssignsDiscNumbersInNumericOrder()
    {
        var fs = new FakeFs();
        var root = @"C:\Archive";
        var album = root + @"\Mahler Symphonies Bernstein";
        fs.AddDir(root, album);

        // Twelve disc folders, in deliberately-shuffled creation order so a
        // pre-fix lexicographic sort would scramble them.
        var discFolders = new[]
        {
            album + @"\Disc 5",
            album + @"\Disc 10",
            album + @"\Disc 1",
            album + @"\Disc 12",
            album + @"\Disc 7",
            album + @"\Disc 3",
            album + @"\Disc 11",
            album + @"\Disc 2",
            album + @"\Disc 8",
            album + @"\Disc 4",
            album + @"\Disc 6",
            album + @"\Disc 9",
        };
        fs.AddDir(album, discFolders);
        foreach (var d in discFolders)
        {
            var flacDir = d + @"\FLAC";
            fs.AddDir(d, flacDir);
            fs.AddFiles(flacDir, "01 Track.flac");
        }

        var scanner = new ArchiveScannerService(
            new FakeSettings { ArchiveRootPath = root }, fs);

        var albums = await scanner.ScanArchiveAsync();

        Assert.Single(albums);
        var info = albums[0];
        Assert.Equal(12, info.DiscCount);
        Assert.Equal(12, info.Discs.Count);

        // The contract: discs come back with DiscNumber 1..12 in order, and
        // each DiscNumber maps to the correctly-named on-disk folder.
        for (int i = 0; i < 12; i++)
        {
            Assert.Equal(i + 1, info.Discs[i].DiscNumber);
            Assert.Equal($"Disc {i + 1}", info.Discs[i].FolderName);
        }
    }

    [Fact]
    public async Task ScanArchiveAsync_SingleDiscAlbum_StillScansCorrectly()
    {
        // Sanity: H28 + H29 mustn't regress the single-disc happy path.
        var fs = new FakeFs();
        var root = @"C:\Archive";
        var album = root + @"\Simple";
        fs.AddDir(root, album);
        var flacDir = album + @"\FLAC";
        fs.AddDir(album, flacDir);
        fs.AddFiles(flacDir, "01 a.flac", "02 b.flac");

        var scanner = new ArchiveScannerService(
            new FakeSettings { ArchiveRootPath = root }, fs);
        var albums = await scanner.ScanArchiveAsync();

        Assert.Single(albums);
        var info = albums[0];
        Assert.Equal(1, info.DiscCount);
        Assert.Single(info.Discs);
        Assert.Equal(1, info.Discs[0].DiscNumber);
        Assert.Equal(2, info.Discs[0].FlacTracks.Count);
    }

    [Fact]
    public async Task ScanArchiveAsync_RespectsCancellation()
    {
        // H28's Task.Run wrap passes the CancellationToken through so a
        // pre-loop cancel propagates as expected.
        var fs = new FakeFs();
        fs.AddDir(@"C:\Archive");
        var scanner = new ArchiveScannerService(
            new FakeSettings { ArchiveRootPath = @"C:\Archive" }, fs);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            scanner.ScanArchiveAsync(cts.Token));
    }
}
