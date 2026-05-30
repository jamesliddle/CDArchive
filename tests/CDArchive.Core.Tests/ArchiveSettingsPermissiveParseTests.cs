using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// M10 regression: <see cref="ArchiveSettings.Initialize"/> must accept the
/// string form of <see cref="PreferredAudioFormat"/> ("Flac" / "Mp3") that
/// a user would expect to see in a hand-edited <c>settings.json</c>, and
/// must NOT discard every other setting when the format value happens to be
/// unknown. Pre-fix any non-integer or unknown enum value threw a
/// <c>JsonException</c> that the catch-all swallowed — reverting ArchiveRootPath
/// / FfmpegPath / Mp3Bitrate / PlayerVolume back to their defaults too, with
/// no signal to the user that anything was wrong.
/// </summary>
public class ArchiveSettingsPermissiveParseTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public ArchiveSettingsPermissiveParseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "CDArchive.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private ArchiveSettings NewSettings() =>
        new(_settingsPath);

    [Theory]
    [InlineData("Flac", PreferredAudioFormat.Flac)]
    [InlineData("Mp3",  PreferredAudioFormat.Mp3)]
    [InlineData("flac", PreferredAudioFormat.Flac)]    // case-insensitive
    [InlineData("MP3",  PreferredAudioFormat.Mp3)]
    public void Initialize_AcceptsStringFormatValue(string jsonValue, PreferredAudioFormat expected)
    {
        File.WriteAllText(_settingsPath, $$"""
            {
                "ArchiveRootPath": "C:\\Music",
                "PreferredAudioFormat": "{{jsonValue}}"
            }
            """);
        var settings = NewSettings();
        settings.Initialize();
        Assert.Equal(expected, settings.PreferredAudioFormat);
    }

    [Fact]
    public void Initialize_AcceptsIntegerFormatValue_BackCompat()
    {
        // Pre-M10 settings files used the integer form; existing user files
        // must still parse.
        File.WriteAllText(_settingsPath, """
            {
                "PreferredAudioFormat": 1
            }
            """);
        var settings = NewSettings();
        settings.Initialize();
        Assert.Equal(PreferredAudioFormat.Mp3, settings.PreferredAudioFormat);
    }

    [Fact]
    public void Initialize_UnknownStringFormatValue_KeepsDefault_ButPreservesOtherSettings()
    {
        // The headline regression: a single bad value used to wipe every
        // other setting back to defaults. Post-fix the format reverts but
        // ArchiveRootPath survives.
        File.WriteAllText(_settingsPath, """
            {
                "ArchiveRootPath": "D:\\Custom\\Music",
                "FfmpegPath": "C:\\ffmpeg\\ffmpeg.exe",
                "Mp3Bitrate": 256,
                "PreferredAudioFormat": "Wma"
            }
            """);
        var settings = NewSettings();
        settings.Initialize();

        Assert.Equal(PreferredAudioFormat.Flac, settings.PreferredAudioFormat);  // reverted to default
        Assert.Equal(@"D:\Custom\Music", settings.ArchiveRootPath);             // preserved
        Assert.Equal(@"C:\ffmpeg\ffmpeg.exe", settings.FfmpegPath);             // preserved
        Assert.Equal(256, settings.Mp3Bitrate);                                  // preserved
    }

    [Fact]
    public void Initialize_OutOfRangeIntegerFormat_KeepsDefault_ButPreservesOtherSettings()
    {
        File.WriteAllText(_settingsPath, """
            {
                "ArchiveRootPath": "D:\\Music",
                "PreferredAudioFormat": 99
            }
            """);
        var settings = NewSettings();
        settings.Initialize();

        Assert.Equal(PreferredAudioFormat.Flac, settings.PreferredAudioFormat);  // reverted
        Assert.Equal(@"D:\Music", settings.ArchiveRootPath);                    // preserved
    }

    [Fact]
    public void Initialize_MissingFormatProperty_KeepsDefault()
    {
        File.WriteAllText(_settingsPath, """
            {
                "ArchiveRootPath": "D:\\Music"
            }
            """);
        var settings = NewSettings();
        settings.Initialize();
        Assert.Equal(PreferredAudioFormat.Flac, settings.PreferredAudioFormat);
        Assert.Equal(@"D:\Music", settings.ArchiveRootPath);
    }

    [Fact]
    public void SaveAndInitialize_RoundTripsViaStringForm()
    {
        // M10 also switched the Save path to emit the string form so a user
        // who opens settings.json by hand sees "Flac"/"Mp3" instead of an
        // opaque integer. Verify the round trip works.
        var first = NewSettings();
        first.Initialize();
        first.PreferredAudioFormat = PreferredAudioFormat.Mp3;
        first.ArchiveRootPath = @"D:\Music";
        first.Save();

        var json = File.ReadAllText(_settingsPath);
        Assert.Contains("\"Mp3\"", json);   // string form, not integer

        var reload = NewSettings();
        reload.Initialize();
        Assert.Equal(PreferredAudioFormat.Mp3, reload.PreferredAudioFormat);
        Assert.Equal(@"D:\Music", reload.ArchiveRootPath);
    }
}
