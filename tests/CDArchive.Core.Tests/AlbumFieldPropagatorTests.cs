using CDArchive.Core.Helpers;
using CDArchive.Core.Models;

namespace CDArchive.Core.Tests;

/// <summary>
/// Tests for <see cref="AlbumFieldPropagator"/>. Locks in the two propagation
/// rules documented in CLAUDE.md's "Inheritable album-level fields":
/// <list type="bullet">
///   <item><b>Push</b>: when the album-level value changed since the snapshot,
///     overwrite every track's value — including non-null overrides.</item>
///   <item><b>Backfill</b>: when the album-level value didn't change, write
///     into tracks whose value is still null; leave non-null overrides
///     alone.</item>
/// </list>
/// </summary>
public class AlbumFieldPropagatorTests
{
    private static CanonAlbum AlbumWithTracks(
        string? sparsCode,
        bool? isStereo,
        List<AlbumPerformer>? performers,
        params AlbumTrack[] tracks)
        => new()
        {
            SparsCode = sparsCode,
            IsStereo  = isStereo,
            Performers = performers,
            Discs = [new AlbumDisc { DiscNumber = 1, Tracks = tracks.ToList() }],
        };

    /// <summary>
    /// SparsCode push: when the album-level SparsCode changed since the snapshot,
    /// every track gets the new value — including a track that had a different
    /// non-null override (representing the user's intent: "I changed this at the
    /// album level, apply it everywhere").
    /// </summary>
    [Fact]
    public void SparsCode_ChangedAtAlbumLevel_PushesToEveryTrack_OverwritingOverrides()
    {
        var album = AlbumWithTracks(
            sparsCode: "DDD",
            isStereo:  true,
            performers: null,
            new AlbumTrack { TrackNumber = 1, SparsCode = "AAD" },                  // override
            new AlbumTrack { TrackNumber = 2, SparsCode = null  },                  // null
            new AlbumTrack { TrackNumber = 3, SparsCode = "DDD" });                 // matches album

        var snapshot = new AlbumFieldPropagator.InheritableSnapshot(
            SparsCode: "ADD", IsStereo: true, PerformersFingerprint: "");

        AlbumFieldPropagator.Propagate(album, snapshot);

        Assert.All(album.Discs[0].Tracks, t => Assert.Equal("DDD", t.SparsCode));
    }

    /// <summary>
    /// SparsCode backfill: when the album-level value didn't change, only the
    /// null track gets a value; the override stays intact.
    /// </summary>
    [Fact]
    public void SparsCode_UnchangedAtAlbumLevel_BackfillsNullTracksOnly()
    {
        var album = AlbumWithTracks(
            sparsCode: "DDD",
            isStereo:  true,
            performers: null,
            new AlbumTrack { TrackNumber = 1, SparsCode = "AAD" },                  // override survives
            new AlbumTrack { TrackNumber = 2, SparsCode = null  });                 // gets backfilled

        var snapshot = AlbumFieldPropagator.Snapshot(album);   // snapshot == current → no change

        AlbumFieldPropagator.Propagate(album, snapshot);

        Assert.Equal("AAD", album.Discs[0].Tracks[0].SparsCode);
        Assert.Equal("DDD", album.Discs[0].Tracks[1].SparsCode);
    }

    /// <summary>
    /// IsStereo push: changing the album-level IsStereo overwrites all tracks.
    /// nullable bool comparison must treat true→false (or any change including
    /// to/from null) as a change.
    /// </summary>
    [Fact]
    public void IsStereo_ChangedAtAlbumLevel_PushesToEveryTrack()
    {
        var album = AlbumWithTracks(
            sparsCode: null,
            isStereo:  false,
            performers: null,
            new AlbumTrack { TrackNumber = 1, IsStereo = true },
            new AlbumTrack { TrackNumber = 2, IsStereo = null });

        var snapshot = new AlbumFieldPropagator.InheritableSnapshot(
            SparsCode: null, IsStereo: true, PerformersFingerprint: "");

        AlbumFieldPropagator.Propagate(album, snapshot);

        Assert.All(album.Discs[0].Tracks, t => Assert.Equal(false, t.IsStereo));
    }

    /// <summary>
    /// Performers push: changing the album-level performer list pushes a clone
    /// to every track (overwriting overrides). Each track must get its OWN
    /// list instance — not a reference share — so subsequent edits don't bleed
    /// across tracks.
    /// </summary>
    [Fact]
    public void Performers_ChangedAtAlbumLevel_PushesClonesToEveryTrack()
    {
        var albumPerformers = new List<AlbumPerformer>
        {
            new() { Name = "Alice", Role = "Conductor" },
            new() { Name = "Bob",   Role = "Soloist"   },
        };
        var album = AlbumWithTracks(
            sparsCode: null,
            isStereo:  null,
            performers: albumPerformers,
            new AlbumTrack { TrackNumber = 1, Performers = [new() { Name = "Old" }] },
            new AlbumTrack { TrackNumber = 2, Performers = null });

        var snapshot = new AlbumFieldPropagator.InheritableSnapshot(
            SparsCode: null, IsStereo: null, PerformersFingerprint: "[]");   // different from current

        AlbumFieldPropagator.Propagate(album, snapshot);

        foreach (var t in album.Discs[0].Tracks)
        {
            Assert.NotNull(t.Performers);
            Assert.Equal(2, t.Performers!.Count);
            Assert.Equal("Alice", t.Performers[0].Name);
            // Each track owns its own list — not the same instance as the album's.
            Assert.NotSame(albumPerformers, t.Performers);
        }
        // Mutating one track's list doesn't bleed to other tracks.
        album.Discs[0].Tracks[0].Performers![0].Name = "Mutated";
        Assert.Equal("Alice", album.Discs[0].Tracks[1].Performers![0].Name);
    }

    /// <summary>
    /// Performers backfill: when album-level performers didn't change, only
    /// the null track gets a clone; the override stays intact.
    /// </summary>
    [Fact]
    public void Performers_UnchangedAtAlbumLevel_BackfillsNullTracksOnly()
    {
        var album = AlbumWithTracks(
            sparsCode: null,
            isStereo:  null,
            performers: [new AlbumPerformer { Name = "Album-default" }],
            new AlbumTrack { TrackNumber = 1, Performers = [new() { Name = "Override" }] },
            new AlbumTrack { TrackNumber = 2, Performers = null });

        var snapshot = AlbumFieldPropagator.Snapshot(album);   // unchanged

        AlbumFieldPropagator.Propagate(album, snapshot);

        Assert.Equal("Override",      album.Discs[0].Tracks[0].Performers![0].Name);
        Assert.Equal("Album-default", album.Discs[0].Tracks[1].Performers![0].Name);
    }

    /// <summary>
    /// Empty album-level performers + a track with null performers → after
    /// propagation the track stays null (no need to materialise an empty list).
    /// </summary>
    [Fact]
    public void Performers_EmptyAlbum_LeavesNullTrackNull()
    {
        var album = AlbumWithTracks(
            sparsCode: null,
            isStereo:  null,
            performers: null,
            new AlbumTrack { TrackNumber = 1, Performers = null });

        var snapshot = AlbumFieldPropagator.Snapshot(album);

        AlbumFieldPropagator.Propagate(album, snapshot);

        Assert.Null(album.Discs[0].Tracks[0].Performers);
    }
}
