using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Covers C12 (per-file error handling in <c>WriteTagsAsync</c>) and C13
/// (atomic-rename in <c>WriteFileTag</c>). The TagLib integration itself is
/// exercised by the existing manual smoke-test flow; here we test the
/// safety primitives that protect the user's source audio from a process
/// kill mid-write or a single corrupt file mid-batch.
/// </summary>
public class CataloguingServiceTagWriteTests : IDisposable
{
    private readonly string _tempDir;

    public CataloguingServiceTagWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "cdarchive-tagwrite-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// CompositeCatalogueReference would normally be DI-resolved; for write-path
    /// tests it's never touched. Passing null! is ugly but functional and
    /// avoids constructing three real reference services just to test file I/O.
    /// </summary>
    private static CataloguingService NewService() => new(reference: null!);

    // ── TryAtomicWrite ───────────────────────────────────────────────────────

    [Fact]
    public void TryAtomicWrite_HappyPath_AppliesMutationAndReturnsSuccess()
    {
        var svc  = NewService();
        var path = Path.Combine(_tempDir, "happy.txt");
        File.WriteAllText(path, "original");

        var result = svc.TryAtomicWrite(path, tempPath =>
        {
            // Append to the temp copy — the equivalent of TagLib mutating the
            // in-memory buffer of the temp file before saving.
            File.AppendAllText(tempPath, " + mutated");
        });

        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(path, result.FilePath);
        Assert.Equal("original + mutated", File.ReadAllText(path));
        AssertNoLeakedTempFiles(_tempDir);
    }

    [Fact]
    public void TryAtomicWrite_MutationThrows_PreservesOriginalBytes()
    {
        // C13's core safety guarantee: a TagLib failure (or a power loss
        // simulated as an exception) must leave the user's source audio
        // bit-identical to its prior state.
        var svc  = NewService();
        var path = Path.Combine(_tempDir, "preserve.bin");
        var originalBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42, 0x00, 0xFF };
        File.WriteAllBytes(path, originalBytes);

        var result = svc.TryAtomicWrite(path, tempPath =>
        {
            // Simulate TagLib partially writing then crashing: corrupt the
            // temp file, then throw. The original must be untouched.
            File.WriteAllBytes(tempPath, new byte[] { 0xCA, 0xFE });
            throw new InvalidOperationException("Simulated TagLib failure");
        });

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Simulated TagLib failure", result.ErrorMessage);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        AssertNoLeakedTempFiles(_tempDir);
    }

    [Fact]
    public void TryAtomicWrite_SourceMissing_ReturnsFailureWithoutThrowing()
    {
        var svc        = NewService();
        var missing    = Path.Combine(_tempDir, "does-not-exist.mp3");
        var mutateCalls = 0;

        var result = svc.TryAtomicWrite(missing, _ => mutateCalls++);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Equal(0, mutateCalls);  // mutation never invoked when copy fails
        AssertNoLeakedTempFiles(_tempDir);
    }

    [Fact]
    public void TryAtomicWrite_PathWithNoDirectory_FailsCleanly()
    {
        // A bare filename has no directory component to host the sibling
        // temp file. The helper should fail gracefully rather than throw.
        var svc = NewService();

        var result = svc.TryAtomicWrite("bare-filename.txt", _ => { });

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    // ── EnumerateWriteTargets ────────────────────────────────────────────────

    [Fact]
    public void EnumerateWriteTargets_NoFlacSibling_YieldsOnlyMp3()
    {
        var svc      = NewService();
        var albumDir = Path.Combine(_tempDir, "Album");
        var mp3Dir   = Path.Combine(albumDir, "MP3");
        Directory.CreateDirectory(mp3Dir);
        var mp3 = Path.Combine(mp3Dir, "01 Track.mp3");
        File.WriteAllText(mp3, "");

        var targets = svc.EnumerateWriteTargets(mp3).ToList();

        Assert.Single(targets);
        Assert.Equal(mp3, targets[0]);
    }

    [Fact]
    public void EnumerateWriteTargets_FlacSiblingPresent_YieldsBoth()
    {
        var svc      = NewService();
        var albumDir = Path.Combine(_tempDir, "Album");
        var mp3Dir   = Path.Combine(albumDir, "MP3");
        var flacDir  = Path.Combine(albumDir, "FLAC");
        Directory.CreateDirectory(mp3Dir);
        Directory.CreateDirectory(flacDir);
        var mp3  = Path.Combine(mp3Dir,  "01 Track.mp3");
        var flac = Path.Combine(flacDir, "01 Track.flac");
        File.WriteAllText(mp3,  "");
        File.WriteAllText(flac, "");

        var targets = svc.EnumerateWriteTargets(mp3).ToList();

        Assert.Equal(2, targets.Count);
        Assert.Equal(mp3,  targets[0]);
        Assert.Equal(flac, targets[1]);
    }

    [Fact]
    public void EnumerateWriteTargets_FlacFolderButNoMatchingFile_YieldsOnlyMp3()
    {
        // The sibling FLAC folder exists but doesn't contain a stem-matching
        // file. That's the common "MP3-only rip" shape; should be a no-op
        // for the FLAC side.
        var svc      = NewService();
        var albumDir = Path.Combine(_tempDir, "Album");
        var mp3Dir   = Path.Combine(albumDir, "MP3");
        var flacDir  = Path.Combine(albumDir, "FLAC");
        Directory.CreateDirectory(mp3Dir);
        Directory.CreateDirectory(flacDir);
        var mp3 = Path.Combine(mp3Dir, "01 Track.mp3");
        File.WriteAllText(mp3, "");
        // FLAC dir exists but is empty.

        var targets = svc.EnumerateWriteTargets(mp3).ToList();

        Assert.Single(targets);
        Assert.Equal(mp3, targets[0]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Atomic-write contract: when the helper returns (success or failure),
    /// no <c>*.tmp-*</c> sidecar should remain in the target directory.
    /// </summary>
    private static void AssertNoLeakedTempFiles(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var leaks = Directory.GetFiles(dir, "*.tmp-*", SearchOption.AllDirectories);
        Assert.Empty(leaks);
    }
}
