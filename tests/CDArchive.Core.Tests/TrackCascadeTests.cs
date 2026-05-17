using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// Coverage for <see cref="TrackCascade"/>. The pure in-memory methods are
/// exercised first; an integration test then drives a real SQLite DB end-to-end
/// to confirm that the subsequent SaveAlbumsAsync / SaveLooseTracksAsync calls
/// actually persist the removals via their orphan-delete passes.
/// </summary>
public class TrackCascadeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteCanonDataService _svc;

    public TrackCascadeTests()
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

    private sealed class SimpleDbContextFactory : IDbContextFactory<CanonDbContext>
    {
        private readonly DbContextOptions<CanonDbContext> _options;
        public SimpleDbContextFactory(DbContextOptions<CanonDbContext> options) => _options = options;
        public CanonDbContext CreateDbContext() => new(_options);
    }

    // ── Pure in-memory tests (TrackCascade.Approve / .Reject) ─────────────────

    [Fact]
    public void Approve_ClearsProvisional_AndReportsOnlyChangedCount()
    {
        var a = new AlbumTrack { IsProvisional = true };
        var b = new AlbumTrack { IsProvisional = true };
        var c = new AlbumTrack { IsProvisional = false };   // already accepted

        var changed = TrackCascade.Approve([a, b, c]);

        Assert.Equal(2, changed);
        Assert.False(a.IsProvisional);
        Assert.False(b.IsProvisional);
        Assert.False(c.IsProvisional);
    }

    [Fact]
    public void Reject_RemovesAlbumBoundFromDisc_AndLooseFromList()
    {
        var disc = new AlbumDisc { DiscNumber = 1 };
        var albumBoundTrack = new AlbumTrack { TrackNumber = 1 };
        disc.Tracks.Add(albumBoundTrack);

        var looseTrack = new AlbumTrack { Description = "Loose" };
        var looseList  = new List<AlbumTrack> { looseTrack };

        var result = TrackCascade.Reject(
            entries: [(albumBoundTrack, disc), (looseTrack, null)],
            looseTracks: looseList);

        Assert.Equal(1, result.AlbumBoundRemoved);
        Assert.Equal(1, result.LooseRemoved);
        Assert.Equal(2, result.Total);
        Assert.Empty(disc.Tracks);
        Assert.Empty(looseList);
    }

    [Fact]
    public void Reject_IgnoresEntriesNotPresentInOwner()
    {
        // A buggy caller hands in a (track, disc) pair where the track isn't
        // actually in the disc's Tracks. We shouldn't blow up — just not count it.
        var disc       = new AlbumDisc { DiscNumber = 1 };
        var phantom    = new AlbumTrack { TrackNumber = 1 };  // never added
        var looseList  = new List<AlbumTrack>();

        var result = TrackCascade.Reject(
            entries: [(phantom, disc), (phantom, null)],
            looseTracks: looseList);

        Assert.Equal(0, result.AlbumBoundRemoved);
        Assert.Equal(0, result.LooseRemoved);
    }

    // ── Integration: cascade + save persists the deletion ─────────────────────

    [Fact]
    public async Task RejectAndSave_DeletesAlbumBoundTrackFromDb()
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
                        new AlbumTrack { TrackNumber = 1, Description = "Keep me" },
                        new AlbumTrack { TrackNumber = 2, Description = "Delete me" },
                    },
                },
            },
        };
        await _svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        // Mutation: remove the second track in-memory, then re-save the same
        // (single-element) albums list. The save's orphan-delete pass drops
        // the doomed track's row.
        var doomed = album.Discs[0].Tracks[1];
        var looseList = new List<AlbumTrack>();
        TrackCascade.Reject([(doomed, album.Discs[0])], looseList);
        await _svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        // Reload and confirm: one album, one disc, one remaining track.
        var reloaded = await _svc.LoadAlbumsAsync();
        var disc     = Assert.Single(Assert.Single(reloaded).Discs);
        var track    = Assert.Single(disc.Tracks);
        Assert.Equal("Keep me", track.Description);
    }

    [Fact]
    public async Task RejectAndSave_DeletesLooseTrackFromDb()
    {
        var keep   = new AlbumTrack { Description = "Keep me",   IsProvisional = true };
        var doomed = new AlbumTrack { Description = "Delete me", IsProvisional = true };
        await _svc.SaveLooseTracksAsync(new List<AlbumTrack> { keep, doomed });

        var looseList = (await _svc.LoadLooseTracksAsync()).ToList();
        // Find the doomed one by description (load returned fresh instances).
        var doomedReloaded = looseList.Single(t => t.Description == "Delete me");

        TrackCascade.Reject([(doomedReloaded, null)], looseList);
        await _svc.SaveLooseTracksAsync(looseList);

        var after = await _svc.LoadLooseTracksAsync();
        var survivor = Assert.Single(after);
        Assert.Equal("Keep me", survivor.Description);
    }

    [Fact]
    public async Task ApproveAndSave_PersistsTheClearedFlag()
    {
        var track = new AlbumTrack { Description = "Provisional me", IsProvisional = true };
        await _svc.SaveLooseTracksAsync(new List<AlbumTrack> { track });

        var looseList = (await _svc.LoadLooseTracksAsync()).ToList();
        TrackCascade.Approve(looseList);
        await _svc.SaveLooseTracksAsync(looseList);

        var reloaded = Assert.Single(await _svc.LoadLooseTracksAsync());
        Assert.False(reloaded.IsProvisional);
    }
}
