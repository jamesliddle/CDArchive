using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// Locks in the stable-ID foundation for the <c>feature/variants</c> work:
/// <see cref="VariantInfo.Id"/> and <see cref="CanonPieceVersion.Id"/> must
/// survive saves so album-track refs can FK to a specific variant / version.
///
/// <para>The headline regression is variant id churn: the pre-fix
/// <c>ReplaceVariantsPiece</c> / <c>ReplaceVariantsVersion</c> deleted and
/// re-inserted every variant row on each piece save, so <c>piece_variants.Id</c>
/// changed on every save and nothing could reference it. The fix reconciles by
/// id (mirroring <c>ReconcileMarkers</c>), preserving ids across saves.</para>
/// </summary>
public class PieceVariantVersionStableIdTests
{
    private static SqliteCanonDataService NewService()
    {
        var dbPath = Path.Combine(Path.GetTempPath(),
            $"cdarchive_variantid_{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={dbPath};Foreign Keys=True")
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var json    = new CanonDataService(Path.GetTempPath());
        return new SqliteCanonDataService(factory, json);
    }

    private static async Task SeedComposerAsync(SqliteCanonDataService svc) =>
        await svc.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven" },
        });

    [Fact]
    public async Task FreshVariants_GetRealIds_AndRoundTrip()
    {
        var svc = NewService();
        await SeedComposerAsync(svc);

        var piece = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Violin Concerto",
            Variants = new List<VariantInfo>
            {
                // Both Id == 0 — newly created in memory, never persisted.
                new() { Description = "Kreisler cadenza" },
                new() { Description = "Joachim cadenza" },
            },
        };
        await svc.SavePiecesAsync(new List<CanonPiece> { piece });

        var loaded = (await svc.LoadPiecesAsync()).Single();
        Assert.NotNull(loaded.Variants);
        Assert.Equal(2, loaded.Variants!.Count);
        Assert.Equal("Kreisler cadenza", loaded.Variants[0].Description);
        Assert.Equal("Joachim cadenza",  loaded.Variants[1].Description);
        Assert.All(loaded.Variants, v => Assert.NotEqual(0L, v.Id));
        Assert.NotEqual(loaded.Variants[0].Id, loaded.Variants[1].Id);
    }

    [Fact]
    public async Task NoOpResave_PreservesVariantIds()
    {
        // The core regression: re-saving an unchanged piece must NOT churn the
        // variant ids. Pre-fix the delete-and-reinsert reassigned them.
        var svc = NewService();
        await SeedComposerAsync(svc);

        var piece = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Violin Concerto",
            Variants = new List<VariantInfo>
            {
                new() { Description = "Kreisler cadenza" },
                new() { Description = "Joachim cadenza" },
            },
        };
        await svc.SavePiecesAsync(new List<CanonPiece> { piece });

        var afterFirst = (await svc.LoadPiecesAsync()).Single();
        var idA = afterFirst.Variants![0].Id;
        var idB = afterFirst.Variants![1].Id;

        // Re-save the loaded instance unchanged (ids carried back in).
        await svc.SavePiecesAsync(new List<CanonPiece> { afterFirst });

        var afterSecond = (await svc.LoadPiecesAsync()).Single();
        Assert.Equal(2, afterSecond.Variants!.Count);
        Assert.Equal(idA, afterSecond.Variants[0].Id);
        Assert.Equal(idB, afterSecond.Variants[1].Id);
    }

    [Fact]
    public async Task MixedExistingAndFreshVariants_KeepsExistingIdsAndAddsNewOnes()
    {
        var svc = NewService();
        await SeedComposerAsync(svc);

        var piece = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Violin Concerto",
            Variants = new List<VariantInfo>
            {
                new() { Description = "Kreisler cadenza" },
                new() { Description = "Joachim cadenza" },
            },
        };
        await svc.SavePiecesAsync(new List<CanonPiece> { piece });

        var afterFirst = (await svc.LoadPiecesAsync()).Single();
        var keptId = afterFirst.Variants![0].Id;
        Assert.NotEqual(0L, keptId);

        // Drop "Joachim", keep "Kreisler" (with its real id), add "Auer" (Id=0).
        afterFirst.Variants = new List<VariantInfo>
        {
            new() { Id = keptId, Description = "Kreisler cadenza" },
            new() {              Description = "Auer cadenza"     },
        };
        await svc.SavePiecesAsync(new List<CanonPiece> { afterFirst });

        var afterSecond = (await svc.LoadPiecesAsync()).Single();
        Assert.Equal(2, afterSecond.Variants!.Count);
        Assert.Equal("Kreisler cadenza", afterSecond.Variants[0].Description);
        Assert.Equal(keptId,             afterSecond.Variants[0].Id);
        Assert.Equal("Auer cadenza",     afterSecond.Variants[1].Description);
        Assert.NotEqual(0L,              afterSecond.Variants[1].Id);
        Assert.NotEqual(keptId,          afterSecond.Variants[1].Id);
    }

    [Fact]
    public async Task VariantsOnAVersion_PreserveIdsAcrossResave()
    {
        var svc = NewService();
        await SeedComposerAsync(svc);

        var piece = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Violin Concerto",
            Versions = new List<CanonPieceVersion>
            {
                new()
                {
                    Description = "arr. for piano",
                    Variants    = new List<VariantInfo>
                    {
                        new() { Description = "with extended coda" },
                    },
                },
            },
        };
        await svc.SavePiecesAsync(new List<CanonPiece> { piece });

        var afterFirst = (await svc.LoadPiecesAsync()).Single();
        var ver = afterFirst.Versions!.Single();
        var variantId = ver.Variants!.Single().Id;
        Assert.NotEqual(0L, variantId);

        await svc.SavePiecesAsync(new List<CanonPiece> { afterFirst });

        var afterSecond = (await svc.LoadPiecesAsync()).Single();
        var verAfter = afterSecond.Versions!.Single();
        Assert.Equal(variantId, verAfter.Variants!.Single().Id);
    }

    [Fact]
    public async Task VersionId_RoundTrips_AndIsStableAcrossResave()
    {
        var svc = NewService();
        await SeedComposerAsync(svc);

        var piece = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Violin Concerto",
            Versions = new List<CanonPieceVersion>
            {
                new() { Description = "original" },
                new() { Description = "arr. for piano" },
            },
        };
        await svc.SavePiecesAsync(new List<CanonPiece> { piece });

        var afterFirst = (await svc.LoadPiecesAsync()).Single();
        Assert.Equal(2, afterFirst.Versions!.Count);
        Assert.All(afterFirst.Versions, v => Assert.NotEqual(0L, v.Id));
        var idOriginal = afterFirst.Versions[0].Id;
        var idArr      = afterFirst.Versions[1].Id;
        Assert.NotEqual(idOriginal, idArr);

        await svc.SavePiecesAsync(new List<CanonPiece> { afterFirst });

        var afterSecond = (await svc.LoadPiecesAsync()).Single();
        Assert.Equal(idOriginal, afterSecond.Versions![0].Id);
        Assert.Equal(idArr,      afterSecond.Versions![1].Id);
    }
}
