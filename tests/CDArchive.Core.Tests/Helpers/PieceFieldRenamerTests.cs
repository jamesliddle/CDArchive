using System.Text.Json;
using CDArchive.Core.Helpers;
using CDArchive.Core.Models;

namespace CDArchive.Core.Tests.Helpers;

/// <summary>
/// Locks in the version-walk extension to the pick-list rename
/// propagator. The pre-extraction in-VM walker recursed into
/// <see cref="CanonPiece.Subpieces"/> only; <see cref="CanonPiece.Versions"/>
/// (and each version's subpieces) silently dropped renames.
/// <see cref="PieceFieldRenamer.ApplyToPiece"/> now covers that surface.
/// </summary>
public class PieceFieldRenamerTests
{
    private static readonly IReadOnlyDictionary<string, string> Empty
        = new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, string> R(params (string From, string To)[] pairs)
        => pairs.ToDictionary(p => p.From, p => p.To);

    private static JsonElement ParseInst(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    // ── Top-level piece (regression cover for the existing four kinds) ───────

    [Fact]
    public void ApplyToPiece_TopLevelFields_AllKinds()
    {
        var piece = new CanonPiece
        {
            Form                    = "Symphony",
            InstrumentationCategory = "Orchestral",
            KeyTonality             = "C",
            CatalogInfo             = new List<CatalogInfo>
            {
                new() { Catalog = "Op.", CatalogNumber = "55" },
            },
            Instrumentation = ParseInst("""["violin"]"""),
        };

        var count = PieceFieldRenamer.ApplyToPiece(
            piece,
            R(("Symphony",   "Sinfonia")),
            R(("Orchestral", "Symphonic")),
            R(("Op.",        "Opus")),
            R(("C",          "C major")),
            R(("violin",     "violino")));

        Assert.Equal(5, count);
        Assert.Equal("Sinfonia",   piece.Form);
        Assert.Equal("Symphonic",  piece.InstrumentationCategory);
        Assert.Equal("C major",    piece.KeyTonality);
        Assert.Equal("Opus",       piece.CatalogInfo![0].Catalog);
        Assert.Contains("violino", piece.Instrumentation!.Value.GetRawText());
    }

    [Fact]
    public void ApplyToPiece_AllRenamesEmpty_ZeroCount()
    {
        var piece = new CanonPiece { Form = "Symphony" };
        var count = PieceFieldRenamer.ApplyToPiece(piece, Empty, Empty, Empty, Empty, Empty);

        Assert.Equal(0, count);
        Assert.Equal("Symphony", piece.Form);
    }

    [Fact]
    public void ApplyToPiece_NoMatch_ZeroCount()
    {
        var piece = new CanonPiece { Form = "Symphony" };
        var count = PieceFieldRenamer.ApplyToPiece(
            piece, R(("Concerto", "Concertino")), Empty, Empty, Empty, Empty);

        Assert.Equal(0, count);
        Assert.Equal("Symphony", piece.Form);
    }

    // ── Subpieces (pre-existing recursion) ───────────────────────────────────

    [Fact]
    public void ApplyToPiece_Subpieces_AreWalkedRecursively()
    {
        var piece = new CanonPiece
        {
            Form = "Sonata",
            Subpieces = new List<CanonPiece>
            {
                new()
                {
                    Form = "Sonata",
                    Subpieces = new List<CanonPiece>
                    {
                        new() { Form = "Sonata" },
                    },
                },
            },
        };

        var count = PieceFieldRenamer.ApplyToPiece(
            piece, R(("Sonata", "Sonate")), Empty, Empty, Empty, Empty);

        Assert.Equal(3, count);
        Assert.Equal("Sonate", piece.Form);
        Assert.Equal("Sonate", piece.Subpieces![0].Form);
        Assert.Equal("Sonate", piece.Subpieces[0].Subpieces![0].Form);
    }

    // ── Versions (NEW — the follow-up's headline coverage) ──────────────────

    [Fact]
    public void ApplyToPiece_VersionFields_AreRenamed()
    {
        var piece = new CanonPiece
        {
            Form    = "Concerto",
            Versions = new List<CanonPieceVersion>
            {
                new()
                {
                    Description             = "string-quartet arrangement",
                    Form                    = "Concerto",
                    InstrumentationCategory = "Chamber",
                    KeyTonality             = "C",
                    CatalogInfo             = new List<CatalogInfo>
                    {
                        new() { Catalog = "Op.", CatalogNumber = "55" },
                    },
                    Instrumentation = ParseInst("""["violin", "viola"]"""),
                },
            },
        };

        var count = PieceFieldRenamer.ApplyToPiece(
            piece,
            R(("Concerto", "Concertino")),
            R(("Chamber",  "Chamber Music")),
            R(("Op.",      "Opus")),
            R(("C",        "C major")),
            R(("viola",    "violetta")));

        // 5 on the version (Form, Category, Key, CatalogInfo entry,
        // Instrumentation rewrite) + 1 on the top-level piece (Form).
        Assert.Equal(6, count);
        Assert.Equal("Concertino",     piece.Form);
        Assert.Equal("Concertino",     piece.Versions![0].Form);
        Assert.Equal("Chamber Music",  piece.Versions[0].InstrumentationCategory);
        Assert.Equal("C major",        piece.Versions[0].KeyTonality);
        Assert.Equal("Opus",           piece.Versions[0].CatalogInfo![0].Catalog);
        Assert.Contains("violetta",    piece.Versions[0].Instrumentation!.Value.GetRawText());
    }

    [Fact]
    public void ApplyToPiece_VersionSubpieces_AreWalkedRecursively()
    {
        // Movement-level subpieces under a version are a real shape in
        // the user's data — version-specific arrangements often re-cast
        // movement titles and key signatures. The walker recurses.
        var piece = new CanonPiece
        {
            Form = "Sonata",
            Versions = new List<CanonPieceVersion>
            {
                new()
                {
                    Description = "transcription",
                    Form        = "Sonata",
                    Subpieces   = new List<CanonPiece>
                    {
                        new() { Form = "Sonata" },
                        new() { Form = "Sonata" },
                    },
                },
            },
        };

        var count = PieceFieldRenamer.ApplyToPiece(
            piece, R(("Sonata", "Sonate")), Empty, Empty, Empty, Empty);

        // Top-level + version + 2 version-subpieces = 4 rewrites.
        Assert.Equal(4, count);
        Assert.Equal("Sonate", piece.Form);
        Assert.Equal("Sonate", piece.Versions![0].Form);
        Assert.Equal("Sonate", piece.Versions[0].Subpieces![0].Form);
        Assert.Equal("Sonate", piece.Versions[0].Subpieces![1].Form);
    }

    [Fact]
    public void ApplyToPiece_NoVersions_StillWorks()
    {
        var piece = new CanonPiece { Form = "Symphony", Versions = null };

        var count = PieceFieldRenamer.ApplyToPiece(
            piece, R(("Symphony", "Sinfonia")), Empty, Empty, Empty, Empty);

        Assert.Equal(1, count);
        Assert.Equal("Sinfonia", piece.Form);
    }

    [Fact]
    public void ApplyToPiece_EmptyVersionList_StillWorks()
    {
        var piece = new CanonPiece
        {
            Form     = "Symphony",
            Versions = new List<CanonPieceVersion>(),
        };

        var count = PieceFieldRenamer.ApplyToPiece(
            piece, R(("Symphony", "Sinfonia")), Empty, Empty, Empty, Empty);

        Assert.Equal(1, count);
    }
}
