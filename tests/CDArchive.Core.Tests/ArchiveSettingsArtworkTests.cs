using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Persistence tests for the cover-art display settings (per-surface show
/// flag + size). Covers defaults, round-trip, and tolerant size parse.
/// </summary>
public class ArchiveSettingsArtworkTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public ArchiveSettingsArtworkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CDArchive.SettingsArt.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Defaults_AlbumAndPlayerOn_TrackOff_SensibleSizes()
    {
        var s = new ArchiveSettings(_settingsPath);
        Assert.True(s.ShowAlbumListArtwork);
        Assert.False(s.ShowTrackListArtwork);     // dense list — opt-in
        Assert.True(s.ShowPlayerArtwork);
        Assert.Equal(ArtworkSize.Medium, s.AlbumListArtworkSize);
        Assert.Equal(ArtworkSize.Small,  s.TrackListArtworkSize);
        Assert.Equal(ArtworkSize.Medium, s.PlayerArtworkSize);
    }

    [Fact]
    public void RoundTrip_PreservesAllSixFields()
    {
        var s = new ArchiveSettings(_settingsPath)
        {
            ShowAlbumListArtwork = false,
            ShowTrackListArtwork = true,
            ShowPlayerArtwork    = false,
            AlbumListArtworkSize = ArtworkSize.Large,
            TrackListArtworkSize = ArtworkSize.Medium,
            PlayerArtworkSize    = ArtworkSize.Large,
        };
        s.Save();

        var s2 = new ArchiveSettings(_settingsPath);
        s2.Initialize();

        Assert.False(s2.ShowAlbumListArtwork);
        Assert.True(s2.ShowTrackListArtwork);
        Assert.False(s2.ShowPlayerArtwork);
        Assert.Equal(ArtworkSize.Large,  s2.AlbumListArtworkSize);
        Assert.Equal(ArtworkSize.Medium, s2.TrackListArtworkSize);
        Assert.Equal(ArtworkSize.Large,  s2.PlayerArtworkSize);
    }

    [Fact]
    public void Save_WritesSizeAsString_ReadableByHand()
    {
        var s = new ArchiveSettings(_settingsPath) { PlayerArtworkSize = ArtworkSize.Large };
        s.Save();
        var json = File.ReadAllText(_settingsPath);
        Assert.Contains("\"PlayerArtworkSize\": \"Large\"", json);
    }

    [Fact]
    public void Initialize_UnknownSize_KeepsDefault_DoesNotResetOthers()
    {
        File.WriteAllText(_settingsPath,
            "{ \"AlbumListArtworkSize\": \"Gigantic\", \"ShowTrackListArtwork\": true }");
        var s = new ArchiveSettings(_settingsPath);
        s.Initialize();

        Assert.Equal(ArtworkSize.Medium, s.AlbumListArtworkSize); // bad value ignored
        Assert.True(s.ShowTrackListArtwork);                       // other field still applied
    }
}
