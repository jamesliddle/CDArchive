using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// End-to-end coverage for the one-shot migration that promotes synthetic
/// single-track wrapper albums to loose tracks
/// (<see cref="SqliteCanonDataService.PromoteSingletonAlbumsToLooseTracksAsync"/>).
///
/// <list type="bullet">
///   <item>Eligible synthetic-wrapper album is promoted (album/disc rows
///     deleted; track survives with disc_id NULL; album-level performers
///     re-anchored on the track).</item>
///   <item>Each disqualifying field (Label, CatalogueNumber, Barcode,
///     ArchiveFolder, Sessions, Volumes, track-level Performers, multi-disc,
///     multi-track) causes the album to be skipped with a clear reason.</item>
///   <item>Dry-run reports the same candidates but writes nothing.</item>
///   <item>Album-level SparsCode / IsStereo are inherited by the track when
///     the track's own values are null.</item>
/// </list>
/// </summary>
public class SingletonAlbumPromotionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteCanonDataService _svc;

    public SingletonAlbumPromotionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CDArchive.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "ClassicalCanon.db");

        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var json    = new CanonDataService(_tempDir);
        _svc = new SqliteCanonDataService(factory, json);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static CanonAlbum SyntheticWrapper(string title, params AlbumPerformer[] albumPerformers) =>
        new()
        {
            Title         = title,
            IsProvisional = true,
            Performers    = albumPerformers.Length > 0 ? albumPerformers.ToList() : null,
            Discs =
            {
                new AlbumDisc
                {
                    DiscNumber = 1,
                    Tracks =
                    {
                        new AlbumTrack
                        {
                            TrackNumber   = 1,
                            Description   = title,
                            IsProvisional = true,
                        },
                    },
                },
            },
        };

    [Fact]
    public async Task SyntheticWrapper_IsPromotedToLooseTrack()
    {
        await _svc.SaveAlbumsAsync(new List<CanonAlbum> { SyntheticWrapper("Loose Download") });

        var result = await _svc.PromoteSingletonAlbumsToLooseTracksAsync(dryRun: false);

        Assert.Equal(1, result.AlbumsScanned);
        Assert.Equal(1, result.AlbumsPromoted);
        Assert.Empty(result.Skipped);

        // Album + disc rows gone; the track survives as loose (disc_id NULL).
        Assert.Empty(await _svc.LoadAlbumsAsync());
        var loose = Assert.Single(await _svc.LoadLooseTracksAsync());
        Assert.Equal("Loose Download", loose.Description);
        Assert.Equal(0, loose.TrackNumber);

        // Schema sanity: only one row in album_tracks, and disc_id is NULL.
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM album_tracks WHERE disc_id IS NULL";
        Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
        cmd.CommandText = "SELECT COUNT(*) FROM albums";
        Assert.Equal(0, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
        cmd.CommandText = "SELECT COUNT(*) FROM album_discs";
        Assert.Equal(0, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task AlbumLevelPerformers_ReAnchorOnTheTrack()
    {
        var album = SyntheticWrapper("Lang Lang Download",
            new AlbumPerformer { Name = "Lang Lang", Role = "piano" },
            new AlbumPerformer { Name = "Studio recording" });

        await _svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        var result = await _svc.PromoteSingletonAlbumsToLooseTracksAsync(dryRun: false);
        Assert.Equal(1, result.AlbumsPromoted);

        var loose = Assert.Single(await _svc.LoadLooseTracksAsync());
        Assert.NotNull(loose.Performers);
        Assert.Equal(2, loose.Performers!.Count);
        Assert.Equal("Lang Lang",        loose.Performers[0].Name);
        Assert.Equal("piano",            loose.Performers[0].Role);
        Assert.Equal("Studio recording", loose.Performers[1].Name);
    }

    [Fact]
    public async Task AlbumLevelSparsAndStereo_InheritOntoTheTrack_WhenTrackHasNull()
    {
        var album = SyntheticWrapper("Inherited Bits");
        album.SparsCode = "DDD";
        album.IsStereo  = true;
        // Track left with null values.
        await _svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        await _svc.PromoteSingletonAlbumsToLooseTracksAsync(dryRun: false);

        var loose = Assert.Single(await _svc.LoadLooseTracksAsync());
        Assert.Equal("DDD", loose.SparsCode);
        Assert.True(loose.IsStereo);
    }

    [Fact]
    public async Task DryRun_ReportsCandidatesButDoesNotMutate()
    {
        await _svc.SaveAlbumsAsync(new List<CanonAlbum> { SyntheticWrapper("Loose Download") });

        var dry = await _svc.PromoteSingletonAlbumsToLooseTracksAsync(dryRun: true);
        Assert.Equal(1, dry.AlbumsPromoted);

        // Nothing changed: the album is still there, no loose track yet.
        Assert.Single(await _svc.LoadAlbumsAsync());
        Assert.Empty(await _svc.LoadLooseTracksAsync());
    }

    [Theory]
    [InlineData("Label",            true,  false, false, false)]   // has Label
    [InlineData("CatalogueNumber",  false, true,  false, false)]   // has CatalogueNumber
    [InlineData("Barcode",          false, false, true,  false)]   // has Barcode
    [InlineData("ArchiveFolder",    false, false, false, true)]    // has ArchiveFolder
    public async Task RealAlbumMarkers_DisqualifyTheAlbum(
        string field, bool hasLabel, bool hasCatalogue, bool hasBarcode, bool hasArchiveFolder)
    {
        var album = SyntheticWrapper("Real Release");
        if (hasLabel)          album.Label           = "DG";
        if (hasCatalogue)      album.CatalogueNumber = "423 456-2";
        if (hasBarcode)        album.Barcode         = "028942345621";
        if (hasArchiveFolder)  album.ArchiveFolder   = "Some/Folder";

        await _svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        var result = await _svc.PromoteSingletonAlbumsToLooseTracksAsync(dryRun: false);
        Assert.Equal(0, result.AlbumsPromoted);
        var skip = Assert.Single(result.Skipped);
        Assert.Contains(field, skip.Reason, StringComparison.OrdinalIgnoreCase);

        // Album still in DB.
        Assert.Single(await _svc.LoadAlbumsAsync());
    }

    [Fact]
    public async Task MultiDisc_IsSkipped()
    {
        var album = new CanonAlbum
        {
            Title = "Two-Disc Set",
            Discs =
            {
                new AlbumDisc { DiscNumber = 1, Tracks = { new AlbumTrack { TrackNumber = 1 } } },
                new AlbumDisc { DiscNumber = 2, Tracks = { new AlbumTrack { TrackNumber = 1 } } },
            },
        };
        await _svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        var result = await _svc.PromoteSingletonAlbumsToLooseTracksAsync(dryRun: false);
        Assert.Equal(0, result.AlbumsPromoted);
        var skip = Assert.Single(result.Skipped);
        Assert.Contains("disc", skip.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SingleDiscMultiTrack_IsSkipped()
    {
        var album = new CanonAlbum
        {
            Title = "Two-Track CD",
            Discs =
            {
                new AlbumDisc
                {
                    DiscNumber = 1,
                    Tracks =
                    {
                        new AlbumTrack { TrackNumber = 1 },
                        new AlbumTrack { TrackNumber = 2 },
                    },
                },
            },
        };
        await _svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        var result = await _svc.PromoteSingletonAlbumsToLooseTracksAsync(dryRun: false);
        Assert.Equal(0, result.AlbumsPromoted);
        var skip = Assert.Single(result.Skipped);
        Assert.Contains("track", skip.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TrackLevelPerformersOnSynthetic_IsSkipped_AsManualOverride()
    {
        // A synthetic wrapper that's been touched: the track has its own
        // Performers list (an override). Don't auto-promote; the user probably
        // curated it.
        var album = SyntheticWrapper("Touched Wrapper");
        album.Discs[0].Tracks[0].Performers =
        [
            new AlbumPerformer { Name = "Curated Performer" },
        ];
        await _svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        var result = await _svc.PromoteSingletonAlbumsToLooseTracksAsync(dryRun: false);
        Assert.Equal(0, result.AlbumsPromoted);
        var skip = Assert.Single(result.Skipped);
        Assert.Contains("track-level Performers", skip.Reason);
    }

    [Fact]
    public async Task SessionsPresent_IsSkipped()
    {
        var album = SyntheticWrapper("Has a Session");
        album.Sessions = [new RecordingSession { Dates = "1970-01-01" }];
        await _svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        var result = await _svc.PromoteSingletonAlbumsToLooseTracksAsync(dryRun: false);
        Assert.Equal(0, result.AlbumsPromoted);
        Assert.Contains("session", Assert.Single(result.Skipped).Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MixedBatch_PromotesEligibleAndSkipsRest_InOneScan()
    {
        await _svc.SaveAlbumsAsync(new List<CanonAlbum>
        {
            SyntheticWrapper("Loose A"),
            SyntheticWrapper("Loose B"),
            new() { Title = "Real Release", Label = "DG",
                    Discs = { new AlbumDisc { DiscNumber = 1, Tracks = { new AlbumTrack { TrackNumber = 1 } } } } },
        });

        var result = await _svc.PromoteSingletonAlbumsToLooseTracksAsync(dryRun: false);

        Assert.Equal(3, result.AlbumsScanned);
        Assert.Equal(2, result.AlbumsPromoted);
        Assert.Single(result.Skipped);

        // The real release survives; the two wrappers became loose tracks.
        var remainingAlbums = await _svc.LoadAlbumsAsync();
        Assert.Single(remainingAlbums);
        Assert.Equal("Real Release", remainingAlbums[0].Title);
        Assert.Equal(2, (await _svc.LoadLooseTracksAsync()).Count);
    }
}
