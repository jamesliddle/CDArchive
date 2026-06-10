using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Rework H4 regression tests for <see cref="ArchiveSettings"/>:
///
/// <list type="bullet">
///   <item>Ctor performs no I/O — caller must explicitly call
///     <see cref="ArchiveSettings.Initialize"/> to read from disk.</item>
///   <item><see cref="ArchiveSettings.Initialize"/> tolerates missing /
///     corrupt / unreadable files via a wide catch; the in-memory defaults
///     survive an unparseable settings.json.</item>
///   <item><see cref="ArchiveSettings.Save"/> writes atomically via
///     temp-then-rename: a process kill mid-write can never truncate the
///     live file. The temp file is also cleaned up on the happy path.</item>
/// </list>
/// </summary>
public class ArchiveSettingsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public ArchiveSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CDArchive.Settings.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// The new contract: ctor is I/O-free. Even if a corrupt settings.json
    /// exists at the target path, instantiation must not throw and the
    /// in-memory defaults survive.
    /// </summary>
    [Fact]
    public void Constructor_DoesNotReadDisk_EvenIfFileIsCorrupt()
    {
        File.WriteAllText(_settingsPath, "{ this is not valid json");

        // No throw — pre-fix this would have called Load() inside the ctor
        // and (because the catch was JsonException only) actually been fine
        // here. But subbing in a permission-denied / file-locked scenario
        // would have crashed startup. The new contract is stricter: no disk
        // access in the ctor at all.
        var settings = new ArchiveSettings(_settingsPath);

        Assert.Equal(@"D:\CD archive", settings.ArchiveRootPath);
        Assert.Equal("ffmpeg", settings.FfmpegPath);
        Assert.Equal(320, settings.Mp3Bitrate);
        Assert.Equal(PreferredAudioFormat.Flac, settings.PreferredAudioFormat);
        Assert.Equal(1.0f, settings.PlayerVolume);
    }

    [Fact]
    public void Initialize_MissingFile_KeepsDefaults()
    {
        Assert.False(File.Exists(_settingsPath));

        var settings = new ArchiveSettings(_settingsPath);
        settings.Initialize();

        Assert.Equal(@"D:\CD archive", settings.ArchiveRootPath);
    }

    [Fact]
    public void Initialize_ValidFile_AppliesPersistedValues()
    {
        File.WriteAllText(_settingsPath, """
            {
              "ArchiveRootPath": "E:\\Music\\CDs",
              "FfmpegPath": "C:\\ffmpeg\\bin\\ffmpeg.exe",
              "Mp3Bitrate": 256,
              "PreferredAudioFormat": 1,
              "PlayerVolume": 0.42
            }
            """);

        var settings = new ArchiveSettings(_settingsPath);
        settings.Initialize();

        Assert.Equal(@"E:\Music\CDs", settings.ArchiveRootPath);
        Assert.Equal(@"C:\ffmpeg\bin\ffmpeg.exe", settings.FfmpegPath);
        Assert.Equal(256, settings.Mp3Bitrate);
        Assert.Equal(PreferredAudioFormat.Mp3, settings.PreferredAudioFormat);
        Assert.Equal(0.42f, settings.PlayerVolume, 0.001);
    }

    /// <summary>
    /// The H4 regression: pre-fix the catch was <c>JsonException</c> only,
    /// so a truncated file (which JsonSerializer reads as JsonException) was
    /// handled, but a file with the wrong shape (which often parses
    /// successfully then trips on a sub-property) could still crash startup.
    /// The new wide catch keeps defaults on any failure mode.
    /// </summary>
    [Fact]
    public void Initialize_CorruptJson_KeepsDefaults()
    {
        File.WriteAllText(_settingsPath, "{ this is not valid json");

        var settings = new ArchiveSettings(_settingsPath);
        settings.Initialize();

        // Defaults intact — no exception escaped.
        Assert.Equal(@"D:\CD archive", settings.ArchiveRootPath);
        Assert.Equal("ffmpeg", settings.FfmpegPath);
    }

    /// <summary>
    /// New "Stop after current track" player setting defaults to false and
    /// survives a settings.json that doesn't mention it (back-compat with
    /// files written before the setting existed).
    /// </summary>
    [Fact]
    public void StopAfterCurrentTrack_DefaultsFalse_AndSurvivesMissingProperty()
    {
        var fresh = new ArchiveSettings(_settingsPath);
        Assert.False(fresh.StopAfterCurrentTrack);

        // A pre-existing file with no StopAfterCurrentTrack key must leave the
        // default untouched.
        File.WriteAllText(_settingsPath, """
            { "ArchiveRootPath": "E:\\Music\\CDs" }
            """);
        var loaded = new ArchiveSettings(_settingsPath);
        loaded.Initialize();

        Assert.False(loaded.StopAfterCurrentTrack);
    }

    [Fact]
    public void StopAfterCurrentTrack_RoundTripsThroughSaveAndInitialize()
    {
        var writer = new ArchiveSettings(_settingsPath) { StopAfterCurrentTrack = true };
        writer.Save();

        var reader = new ArchiveSettings(_settingsPath);
        reader.Initialize();

        Assert.True(reader.StopAfterCurrentTrack);
    }

    [Fact]
    public void ShowPlayingFilePath_DefaultsFalse_AndRoundTrips()
    {
        Assert.False(new ArchiveSettings(_settingsPath).ShowPlayingFilePath);

        // Missing key in an older file leaves the default untouched.
        File.WriteAllText(_settingsPath, """{ "ArchiveRootPath": "E:\\X" }""");
        var loaded = new ArchiveSettings(_settingsPath);
        loaded.Initialize();
        Assert.False(loaded.ShowPlayingFilePath);

        // Round-trip true.
        var writer = new ArchiveSettings(_settingsPath) { ShowPlayingFilePath = true };
        writer.Save();
        var reader = new ArchiveSettings(_settingsPath);
        reader.Initialize();
        Assert.True(reader.ShowPlayingFilePath);
    }

    [Fact]
    public void SeekAndRestartSettings_DefaultsAndRoundTrip()
    {
        var fresh = new ArchiveSettings(_settingsPath);
        Assert.Equal(10, fresh.SeekForwardSeconds);
        Assert.Equal(10, fresh.SeekBackwardSeconds);
        Assert.Equal(2, fresh.PreviousRestartThresholdSeconds);

        var writer = new ArchiveSettings(_settingsPath)
        {
            SeekForwardSeconds = 30,
            SeekBackwardSeconds = 15,
            PreviousRestartThresholdSeconds = 5,
        };
        writer.Save();

        var reader = new ArchiveSettings(_settingsPath);
        reader.Initialize();
        Assert.Equal(30, reader.SeekForwardSeconds);
        Assert.Equal(15, reader.SeekBackwardSeconds);
        Assert.Equal(5, reader.PreviousRestartThresholdSeconds);
    }

    [Fact]
    public void SeekSettings_OutOfRange_AreClampedOnLoad()
    {
        File.WriteAllText(_settingsPath, """
            {
              "SeekForwardSeconds": 999,
              "SeekBackwardSeconds": 0,
              "PreviousRestartThresholdSeconds": 200
            }
            """);
        var s = new ArchiveSettings(_settingsPath);
        s.Initialize();

        Assert.Equal(60, s.SeekForwardSeconds);   // clamped to max
        Assert.Equal(10, s.SeekBackwardSeconds);  // 0 < 1 → ignored, keeps default
        Assert.Equal(60, s.PreviousRestartThresholdSeconds); // clamped to max
    }

    [Fact]
    public void Save_CreatesFile_AndRoundTripsThroughInitialize()
    {
        var writer = new ArchiveSettings(_settingsPath)
        {
            ArchiveRootPath      = @"F:\TestRoot",
            FfmpegPath           = "test-ffmpeg",
            Mp3Bitrate           = 192,
            PreferredAudioFormat = PreferredAudioFormat.Mp3,
            PlayerVolume         = 0.5f,
        };
        writer.Save();

        Assert.True(File.Exists(_settingsPath));

        var reader = new ArchiveSettings(_settingsPath);
        reader.Initialize();

        Assert.Equal(@"F:\TestRoot", reader.ArchiveRootPath);
        Assert.Equal("test-ffmpeg", reader.FfmpegPath);
        Assert.Equal(192, reader.Mp3Bitrate);
        Assert.Equal(PreferredAudioFormat.Mp3, reader.PreferredAudioFormat);
        Assert.Equal(0.5f, reader.PlayerVolume, 0.001);
    }

    /// <summary>
    /// The atomic-write half of H4: Save writes to a `.tmp` sibling, then
    /// File.Move's it into place. On the happy path the temp file must not
    /// linger — if it did, a subsequent re-run of an old-format upgrade
    /// might see a stale partial file (it wouldn't actually hurt anything,
    /// but the contract is "no leftovers on success").
    /// </summary>
    [Fact]
    public void Save_LeavesNoTempFile_AfterSuccess()
    {
        var settings = new ArchiveSettings(_settingsPath)
        {
            ArchiveRootPath = @"G:\AnotherRoot",
        };
        settings.Save();

        Assert.True(File.Exists(_settingsPath));
        Assert.False(File.Exists(_settingsPath + ".tmp"),
            "Expected no .tmp file lingering after a successful Save.");
    }

    /// <summary>
    /// Save creates the parent directory if it doesn't exist. Pre-fix the
    /// original code did `Directory.Exists + CreateDirectory`; the new
    /// implementation uses `Directory.CreateDirectory` which is idempotent.
    /// </summary>
    [Fact]
    public void Save_CreatesParentDirectory_IfMissing()
    {
        var nestedPath = Path.Combine(_tempDir, "nested", "deeper", "settings.json");
        var settings = new ArchiveSettings(nestedPath);
        settings.Save();

        Assert.True(File.Exists(nestedPath));
    }
}
