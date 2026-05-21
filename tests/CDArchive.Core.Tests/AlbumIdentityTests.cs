using System.Text.Json;
using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// Identity tests for the album save path. Covers the bug where editing an
/// album with no Label / CatalogueNumber and saving created a duplicate row
/// instead of updating the original — see also the Lessons Learned entry on
/// album identity in CLAUDE.md.
/// <para>
/// The editor's clone-and-substitute pattern (<c>AlbumEditorWindow</c>
/// JSON-clones the album for editing and exposes the clone via
/// <c>Result</c>) breaks the data service's <c>ConditionalWeakTable</c>
/// identity tracking, so save-time dedup falls back to
/// <see cref="CanonAlbum.IdentityKey"/>. The widened key now folds in
/// Title+Subtitle so albums without Label/CatalogueNumber still match.
/// </para>
/// </summary>
public class AlbumIdentityTests
{
    private static SqliteCanonDataService NewServiceOnFreshDb(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(),
            $"cdarchive_album_identity_{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var json    = new CanonDataService(Path.GetTempPath());
        return new SqliteCanonDataService(factory, json);
    }

    /// <summary>
    /// IdentityKey: an album with no Label / CatalogueNumber but a Title
    /// returns a stable, non-null key. Pre-fix this returned null and the
    /// save path's dedup couldn't find existing rows for these albums.
    /// </summary>
    [Fact]
    public void IdentityKey_TitleOnly_ReturnsStableComposite()
    {
        var a = new CanonAlbum { Title = "Beethoven Symphony 9 Böhm" };
        Assert.NotNull(a.IdentityKey);
        Assert.Equal("||Beethoven Symphony 9 Böhm|", a.IdentityKey);
    }

    /// <summary>
    /// IdentityKey: a fully-empty album returns null (it'd always insert fresh
    /// — no way to dedup it).
    /// </summary>
    [Fact]
    public void IdentityKey_AllBlank_ReturnsNull()
    {
        var a = new CanonAlbum();
        Assert.Null(a.IdentityKey);
    }

    /// <summary>
    /// IdentityKey: composites match across Trim — leading/trailing whitespace
    /// in the source data doesn't cause spurious mismatches at save time.
    /// </summary>
    [Fact]
    public void IdentityKey_Trims_Components()
    {
        var a = new CanonAlbum { Title = "  Live in Vienna  " };
        var b = new CanonAlbum { Title = "Live in Vienna" };
        Assert.Equal(a.IdentityKey, b.IdentityKey);
    }

    /// <summary>
    /// End-to-end regression for the Böhm bug: save an album, then JSON-clone
    /// it (mimicking <c>AlbumEditorWindow</c>'s round-trip), edit a track on
    /// the clone, and save the clone. The DB must end up with exactly ONE row
    /// — the new content — not two. Pre-fix this produced two rows.
    /// </summary>
    [Fact]
    public async Task SaveTwice_AfterJsonCloneAndEdit_DoesNotDuplicate()
    {
        var dbPath = "";
        try
        {
            var svc = NewServiceOnFreshDb(out dbPath);

            // Need at least one composer + one piece for the album track to ref.
            var composer = new CanonComposer { Name = "Beethoven, Ludwig van", SortName = "Beethoven, Ludwig van" };
            await svc.SaveComposersAsync(new List<CanonComposer> { composer });
            var piece = new CanonPiece
            {
                Composer = "Beethoven, Ludwig van",
                Title    = "Symphony No. 9",
            };
            await svc.SavePiecesAsync(new List<CanonPiece> { piece });

            // First save: original album with Title only (no Label / CatalogueNumber).
            var album = new CanonAlbum
            {
                Title = "Beethoven Symphony 9 Böhm",
                Discs = [new AlbumDisc
                {
                    DiscNumber = 1,
                    Tracks =
                    [
                        new AlbumTrack { TrackNumber = 1 },
                        new AlbumTrack { TrackNumber = 2 },
                    ],
                }],
            };
            await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

            var afterFirst = await svc.LoadAlbumsAsync();
            Assert.Single(afterFirst);

            // Mimic AlbumEditorWindow: clone via JSON round-trip, then edit.
            var json  = JsonSerializer.Serialize(album);
            var clone = JsonSerializer.Deserialize<CanonAlbum>(json)!;
            // Add a piece-ref to track 2 on the clone.
            clone.Discs[0].Tracks[1].PieceRefs =
            [
                new TrackPieceRef
                {
                    Composer   = "Beethoven, Ludwig van",
                    PieceTitle = "Symphony No. 9",
                },
            ];

            // The caller (AlbumsView) replaces the original with the clone in
            // its working list. Save the new list.
            await svc.SaveAlbumsAsync(new List<CanonAlbum> { clone });

            var afterSecond = await svc.LoadAlbumsAsync();
            Assert.Single(afterSecond);                                          // ← was 2 pre-fix
            var only = afterSecond.Single();
            Assert.Equal("Beethoven Symphony 9 Böhm", only.Title);
            Assert.Single(only.Discs[0].Tracks[1].PieceRefs ?? new());           // edit landed on the survivor
        }
        finally
        {
            // Close all pooled SQLite connections before deleting the file —
            // Microsoft.Data.Sqlite holds a process-wide pool and a fresh
            // CreateDbContext keeps the connection cached after the context
            // is disposed.
            SqliteConnection.ClearAllPools();
            if (!string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }
}
