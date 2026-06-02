using System.Text.Json;
using CDArchive.Core.Helpers;
using CDArchive.Core.Models;

namespace CDArchive.Core.Tests.Helpers;

/// <summary>
/// Pins the contract for <see cref="PieceRoleCollector"/>: the helper that
/// feeds cast names (Florestan, Don Giovanni, …) into the Performer editor's
/// Role dropdown based on the canon pieces an album / track references.
/// </summary>
public class PieceRoleCollectorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static JsonElement Json(string raw) =>
        JsonDocument.Parse(raw).RootElement.Clone();

    private static CanonPiece OperaWithCast(
        string composer, string title, params (string name, string voiceType)[] cast)
    {
        var array = string.Join(",", cast.Select(c =>
            $"{{\"name\":\"{c.name}\",\"voice_type\":\"{c.voiceType}\"}}"));
        return new CanonPiece
        {
            Composer = composer,
            Title    = title,
            Roles    = Json($"[{array}]"),
        };
    }

    private static CanonPiece SubpieceWithStringRoles(params string[] roleNames)
    {
        var array = string.Join(",", roleNames.Select(n => $"\"{n}\""));
        return new CanonPiece { Roles = Json($"[{array}]") };
    }

    private static AlbumTrack TrackRef(string composer, string title) => new()
    {
        TrackNumber = 1,
        PieceRefs   = [new TrackPieceRef { Composer = composer, PieceTitle = title }],
    };

    // ── Single piece, top-level cast ─────────────────────────────────────────

    [Fact]
    public void TopLevelCast_ReturnedInDeclaredOrder_WithVoiceTypes()
    {
        var fidelio = OperaWithCast("Beethoven, Ludwig van", "Fidelio",
            ("Florestan", "tenor"),
            ("Leonore",   "soprano"),
            ("Pizarro",   "baritone"));

        var roles = PieceRoleCollector.CollectFromPieceRefs(
            [new TrackPieceRef { Composer = "Beethoven, Ludwig van", PieceTitle = "Fidelio" }],
            [fidelio]);

        Assert.Equal(
            new[]
            {
                new CastRole("Florestan", "tenor"),
                new CastRole("Leonore",   "soprano"),
                new CastRole("Pizarro",   "baritone"),
            },
            roles);
    }

    [Fact]
    public void ComposerOrTitleLookup_IsCaseInsensitive()
    {
        var fidelio = OperaWithCast("Beethoven, Ludwig van", "Fidelio", ("Florestan", "tenor"));

        var roles = PieceRoleCollector.CollectFromPieceRefs(
            [new TrackPieceRef { Composer = "BEETHOVEN, LUDWIG VAN", PieceTitle = "fidelio" }],
            [fidelio]);

        Assert.Single(roles, new CastRole("Florestan", "tenor"));
    }

    // ── Subpiece string-only role references contribute ────────────────────

    [Fact]
    public void SubpieceStringRoles_AreCollectedAlongsideTopCast()
    {
        var aria = SubpieceWithStringRoles("Chorus");
        var opera = OperaWithCast("Verdi, Giuseppe", "Aida",
            ("Aida",       "soprano"),
            ("Radamès",    "tenor"));
        opera.Subpieces = [aria];

        var roles = PieceRoleCollector.CollectFromPieceRefs(
            [new TrackPieceRef { Composer = "Verdi, Giuseppe", PieceTitle = "Aida" }],
            [opera]);

        Assert.Equal(
            new[]
            {
                new CastRole("Aida",    "soprano"),
                new CastRole("Radamès", "tenor"),
                new CastRole("Chorus",  null),   // string-form ref: no voice type
            },
            roles);
    }

    [Fact]
    public void VersionRoles_AreCollected()
    {
        var opera = OperaWithCast("Mozart", "Don Giovanni",
            ("Don Giovanni", "baritone"));
        opera.Versions =
        [
            new CanonPieceVersion
            {
                Description = "Vienna version",
                Roles       = Json("[{\"name\":\"Donna Elvira\",\"voice_type\":\"soprano\"}]"),
            },
        ];

        var roles = PieceRoleCollector.CollectFromPieceRefs(
            [new TrackPieceRef { Composer = "Mozart", PieceTitle = "Don Giovanni" }],
            [opera]);

        Assert.Equal(
            new[]
            {
                new CastRole("Don Giovanni", "baritone"),
                new CastRole("Donna Elvira", "soprano"),
            },
            roles);
    }

    // ── Deduplication ────────────────────────────────────────────────────────

    [Fact]
    public void DuplicateNames_AreDedupedCaseInsensitivelyFirstWins()
    {
        // Subpiece restates "Florestan" with different casing; first
        // occurrence wins — and crucially keeps the top-level entry's
        // voice type rather than losing it to the bare-string ref.
        var aria = SubpieceWithStringRoles("FLORESTAN", "Marzelline");
        var opera = OperaWithCast("Beethoven, Ludwig van", "Fidelio",
            ("Florestan", "tenor"));
        opera.Subpieces = [aria];

        var roles = PieceRoleCollector.CollectFromPieceRefs(
            [new TrackPieceRef { Composer = "Beethoven, Ludwig van", PieceTitle = "Fidelio" }],
            [opera]);

        Assert.Equal(
            new[]
            {
                new CastRole("Florestan",   "tenor"),
                new CastRole("Marzelline",  null),
            },
            roles);
    }

    [Fact]
    public void SamePieceReferencedTwice_IsWalkedOnce()
    {
        var fidelio = OperaWithCast("Beethoven, Ludwig van", "Fidelio",
            ("Florestan", "tenor"),
            ("Leonore",   "soprano"));

        var roles = PieceRoleCollector.CollectFromPieceRefs(
            [
                new TrackPieceRef { Composer = "Beethoven, Ludwig van", PieceTitle = "Fidelio" },
                new TrackPieceRef { Composer = "Beethoven, Ludwig van", PieceTitle = "Fidelio" },
            ],
            [fidelio]);

        Assert.Equal(
            new[]
            {
                new CastRole("Florestan", "tenor"),
                new CastRole("Leonore",   "soprano"),
            },
            roles);
    }

    // ── Multi-piece union ────────────────────────────────────────────────────

    [Fact]
    public void MultiplePieces_UnionInEncounterOrder()
    {
        var fidelio = OperaWithCast("Beethoven, Ludwig van", "Fidelio",
            ("Florestan", "tenor"));
        var giovanni = OperaWithCast("Mozart", "Don Giovanni",
            ("Don Giovanni", "baritone"));

        var roles = PieceRoleCollector.CollectFromPieceRefs(
            [
                new TrackPieceRef { Composer = "Beethoven, Ludwig van", PieceTitle = "Fidelio" },
                new TrackPieceRef { Composer = "Mozart",                PieceTitle = "Don Giovanni" },
            ],
            [fidelio, giovanni]);

        Assert.Equal(
            new[]
            {
                new CastRole("Florestan",    "tenor"),
                new CastRole("Don Giovanni", "baritone"),
            },
            roles);
    }

    // ── Missing piece / no Roles ─────────────────────────────────────────────

    [Fact]
    public void UnresolvedPieceRef_IsSilentlySkipped()
    {
        var fidelio = OperaWithCast("Beethoven, Ludwig van", "Fidelio",
            ("Florestan", "tenor"));

        var roles = PieceRoleCollector.CollectFromPieceRefs(
            [
                new TrackPieceRef { Composer = "Unknown", PieceTitle = "Unknown" },
                new TrackPieceRef { Composer = "Beethoven, Ludwig van", PieceTitle = "Fidelio" },
            ],
            [fidelio]);

        Assert.Equal(new[] { new CastRole("Florestan", "tenor") }, roles);
    }

    [Fact]
    public void PieceWithoutRoles_ContributesNothing()
    {
        var sonata = new CanonPiece { Composer = "Beethoven", Title = "Piano Sonata #1" };

        var roles = PieceRoleCollector.CollectFromPieceRefs(
            [new TrackPieceRef { Composer = "Beethoven", PieceTitle = "Piano Sonata #1" }],
            [sonata]);

        Assert.Empty(roles);
    }

    [Fact]
    public void NullOrEmptyPieceRefs_ReturnsEmpty()
    {
        Assert.Empty(PieceRoleCollector.CollectFromPieceRefs(null, []));
        Assert.Empty(PieceRoleCollector.CollectFromPieceRefs(Array.Empty<TrackPieceRef>(), []));
    }

    // ── Track / album wrappers ───────────────────────────────────────────────

    [Fact]
    public void CollectFromTrack_UsesItsPieceRefs()
    {
        var fidelio = OperaWithCast("Beethoven", "Fidelio", ("Florestan", "tenor"));
        var track = TrackRef("Beethoven", "Fidelio");

        var roles = PieceRoleCollector.CollectFromTrack(track, [fidelio]);

        Assert.Equal(new[] { new CastRole("Florestan", "tenor") }, roles);
    }

    [Fact]
    public void CollectFromAlbum_UnionsAcrossEveryTrack()
    {
        var fidelio  = OperaWithCast("Beethoven", "Fidelio",      ("Florestan", "tenor"));
        var giovanni = OperaWithCast("Mozart",    "Don Giovanni", ("Don Giovanni", "baritone"));

        var album = new CanonAlbum
        {
            Title = "Recital",
            Discs =
            [
                new AlbumDisc
                {
                    DiscNumber = 1,
                    Tracks =
                    [
                        TrackRef("Beethoven", "Fidelio"),
                        TrackRef("Mozart",    "Don Giovanni"),
                    ],
                },
            ],
        };

        var roles = PieceRoleCollector.CollectFromAlbum(album, [fidelio, giovanni]);

        Assert.Equal(
            new[]
            {
                new CastRole("Florestan",    "tenor"),
                new CastRole("Don Giovanni", "baritone"),
            },
            roles);
    }

    [Fact]
    public void CollectFromAlbum_TracksWithoutPieceRefs_AreIgnored()
    {
        var fidelio = OperaWithCast("Beethoven", "Fidelio", ("Florestan", "tenor"));
        var album = new CanonAlbum
        {
            Discs =
            [
                new AlbumDisc
                {
                    DiscNumber = 1,
                    Tracks =
                    [
                        new AlbumTrack { TrackNumber = 1, Description = "Applause" },
                        TrackRef("Beethoven", "Fidelio"),
                    ],
                },
            ],
        };

        var roles = PieceRoleCollector.CollectFromAlbum(album, [fidelio]);

        Assert.Equal(new[] { new CastRole("Florestan", "tenor") }, roles);
    }

    [Fact]
    public void NullOrWhitespaceVoiceType_StoredAsNull()
    {
        var opera = OperaWithCast("X", "Y", ("Lead", ""));

        var roles = PieceRoleCollector.CollectFromPieceRefs(
            [new TrackPieceRef { Composer = "X", PieceTitle = "Y" }],
            [opera]);

        Assert.Equal(new[] { new CastRole("Lead", null) }, roles);
    }
}
