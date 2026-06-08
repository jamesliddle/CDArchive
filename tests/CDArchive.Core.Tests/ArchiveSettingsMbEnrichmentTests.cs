using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Slice 6: persistence tests for the six MB-enrichment settings added
/// to <see cref="IArchiveSettings"/>. Covers defaults, round-trip, and
/// permissive parse (a bad value in one field doesn't reset the others).
/// </summary>
public class ArchiveSettingsMbEnrichmentTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public ArchiveSettingsMbEnrichmentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CDArchive.SettingsMb.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Defaults_MatchDocumentedValues()
    {
        var s = new ArchiveSettings(_settingsPath);
        // Master switch defaults OFF — feature is opt-in.
        Assert.False(s.EnableMusicBrainzImportEnrichment);
        Assert.Equal(3, s.MusicBrainzCandidatesPerProposal);
        Assert.True(s.ApplyMbAlbumMetadata);
        Assert.True(s.ApplyMbPerformerCredits);
        Assert.True(s.ApplyMbRecordingSessions);
        // User decision: canonical-work-structure defaults ON.
        Assert.True(s.ApplyMbCanonicalWorkStructure);
    }

    [Fact]
    public void RoundTrip_PreservesAllSixFields()
    {
        var s = new ArchiveSettings(_settingsPath);
        s.EnableMusicBrainzImportEnrichment = true;
        s.MusicBrainzCandidatesPerProposal = 5;
        s.ApplyMbAlbumMetadata = false;
        s.ApplyMbPerformerCredits = false;
        s.ApplyMbRecordingSessions = false;
        s.ApplyMbCanonicalWorkStructure = false;
        s.Save();

        var s2 = new ArchiveSettings(_settingsPath);
        s2.Initialize();

        Assert.True(s2.EnableMusicBrainzImportEnrichment);
        Assert.Equal(5, s2.MusicBrainzCandidatesPerProposal);
        Assert.False(s2.ApplyMbAlbumMetadata);
        Assert.False(s2.ApplyMbPerformerCredits);
        Assert.False(s2.ApplyMbRecordingSessions);
        Assert.False(s2.ApplyMbCanonicalWorkStructure);
    }

    [Fact]
    public void CandidatesPerProposal_OutOfRange_ClampedNotReset()
    {
        // Caller writes 99; loader clamps to 25 (the new upper bound).
        // Other fields untouched — sibling fields survive under the
        // permissive-parse contract.
        File.WriteAllText(_settingsPath, """
            {
              "EnableMusicBrainzImportEnrichment": true,
              "MusicBrainzCandidatesPerProposal": 99,
              "ApplyMbAlbumMetadata": true
            }
            """);

        var s = new ArchiveSettings(_settingsPath);
        s.Initialize();

        Assert.True(s.EnableMusicBrainzImportEnrichment);
        Assert.Equal(25, s.MusicBrainzCandidatesPerProposal);
        Assert.True(s.ApplyMbAlbumMetadata);
    }

    [Fact]
    public void BadJsonValueInOneField_PreservesOthers()
    {
        // The bool field for ApplyMbPerformerCredits is "yes" (a string),
        // which TryReadBool rejects → default true survives. Every other
        // field is honoured normally.
        File.WriteAllText(_settingsPath, """
            {
              "EnableMusicBrainzImportEnrichment": true,
              "MusicBrainzCandidatesPerProposal": 5,
              "ApplyMbAlbumMetadata": false,
              "ApplyMbPerformerCredits": "yes",
              "ApplyMbRecordingSessions": false,
              "ApplyMbCanonicalWorkStructure": false
            }
            """);

        var s = new ArchiveSettings(_settingsPath);
        s.Initialize();

        Assert.True(s.EnableMusicBrainzImportEnrichment);
        Assert.Equal(5, s.MusicBrainzCandidatesPerProposal);
        Assert.False(s.ApplyMbAlbumMetadata);
        // Default preserved.
        Assert.True(s.ApplyMbPerformerCredits);
        Assert.False(s.ApplyMbRecordingSessions);
        Assert.False(s.ApplyMbCanonicalWorkStructure);
    }
}
