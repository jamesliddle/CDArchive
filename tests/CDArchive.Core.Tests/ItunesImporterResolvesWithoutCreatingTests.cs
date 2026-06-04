using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Regression tests for <see cref="ItunesImporter.ResolvesWithoutCreating"/>,
/// the dry-run check the iTunes-import dialog uses to decide whether a track
/// with an ambiguous "<c>. </c>" separator needs the user to pick an
/// interpretation. The dialog should only appear when importing the track
/// would create NEW canon structure under both interpretations.
///
/// <para>The headline case: the curated canon stores Beethoven's Piano Sonata
/// #15 movement 4 structurally — <c>Form="Rondo", Number=4</c>, empty Title,
/// no deeper subpieces. The iTunes name is
/// "<c>… - 4. Rondo. Allegro, ma non troppo - Più allegro quasi presto</c>".
/// Under FormAndTempo the parsed movement carries MusicNumber=4 and matches
/// that leaf by number → creates nothing → no prompt needed. Under
/// SubpieceHierarchy it would try to descend "Rondo" → "Allegro…" which
/// doesn't exist → would create. Pre-fix the check used
/// PieceReferenceIndex.TryResolve (string-only), which failed BOTH ways and
/// needlessly prompted.</para>
/// </summary>
public class ItunesImporterResolvesWithoutCreatingTests
{
    private static ItunesTrack Track(string name, string composer) => new(
        TrackId:      1,
        PersistentId: null,
        DiscNumber:   1,
        TrackNumber:  19,
        Name:         name,
        DurationMs:   180_000,
        Genre:        null,
        Composer:     composer,
        Album:        "Beethoven Piano Sonatas Jandó 10",
        AlbumArtist:  null,
        Artist:       null,
        DateAdded:    DateTime.UtcNow,
        Location:     null);

    /// <summary>
    /// Builds Sonata #15 the way the curated canon stores it: a top-level
    /// piece with movement #4 as a structural leaf (Form="Rondo", Number=4,
    /// empty Title, no subpieces).
    /// </summary>
    private static (PieceReferenceIndex resolver, string composer) BuildCanon()
    {
        var sonata = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Form     = "Piano Sonata",
            Number   = 15,
            KeyTonality = "D",
            Nickname = "Pastoral",
            CatalogInfo = new List<CatalogInfo>
            {
                new() { Catalog = "Op.", CatalogNumber = "28" },
            },
            Subpieces = new List<CanonPiece>
            {
                new() { Composer = "Beethoven, Ludwig van", Number = 1 },
                new() { Composer = "Beethoven, Ludwig van", Number = 2 },
                new() { Composer = "Beethoven, Ludwig van", Form = "Scherzo", Number = 3 },
                new() { Composer = "Beethoven, Ludwig van", Form = "Rondo",   Number = 4 },
            },
        };

        var resolver = new PieceReferenceIndex(registerAsCurrent: false);
        resolver.BuildResolver(new[] { sonata });
        return (resolver, "Beethoven, Ludwig van");
    }

    [Fact]
    public void FormAndTempo_MatchesStructuralMovementByNumber_ResolvesWithoutCreating()
    {
        var (resolver, composer) = BuildCanon();
        var track = Track(
            "Piano Sonata #15 in D, Op. 28 \"Pastoral\" - 4. Rondo. Allegro, ma non troppo - Più allegro quasi presto",
            "Beethoven, Ludwig van (1770–1827)");

        Assert.True(ItunesImporter.ResolvesWithoutCreating(
            track, composer, resolver,
            ItunesImportInference.DotSeparatorInterpretation.FormAndTempo));
    }

    [Fact]
    public void SubpieceHierarchy_WouldDescendIntoNonexistentChild_DoesNotResolve()
    {
        var (resolver, composer) = BuildCanon();
        var track = Track(
            "Piano Sonata #15 in D, Op. 28 \"Pastoral\" - 4. Rondo. Allegro, ma non troppo - Più allegro quasi presto",
            "Beethoven, Ludwig van (1770–1827)");

        // SubpieceHierarchy parses "Rondo" then "Allegro…" as nested subpieces;
        // the canon's Rondo movement is a leaf, so this would create structure.
        Assert.False(ItunesImporter.ResolvesWithoutCreating(
            track, composer, resolver,
            ItunesImportInference.DotSeparatorInterpretation.SubpieceHierarchy));
    }

    [Fact]
    public void UnknownTopPiece_DoesNotResolve()
    {
        var (resolver, composer) = BuildCanon();
        var track = Track(
            "Piano Sonata #99 in Q - 1. Allegro. Vivace",
            "Beethoven, Ludwig van (1770–1827)");

        Assert.False(ItunesImporter.ResolvesWithoutCreating(
            track, composer, resolver,
            ItunesImportInference.DotSeparatorInterpretation.FormAndTempo));
        Assert.False(ItunesImporter.ResolvesWithoutCreating(
            track, composer, resolver,
            ItunesImportInference.DotSeparatorInterpretation.SubpieceHierarchy));
    }

    [Fact]
    public void BareTopLevelReference_NoSubpath_ResolvesWhenPieceExists()
    {
        var (resolver, composer) = BuildCanon();
        var track = Track(
            "Piano Sonata #15 in D, Op. 28 \"Pastoral\"",
            "Beethoven, Ludwig van (1770–1827)");

        Assert.True(ItunesImporter.ResolvesWithoutCreating(
            track, composer, resolver,
            ItunesImportInference.DotSeparatorInterpretation.FormAndTempo));
    }
}
