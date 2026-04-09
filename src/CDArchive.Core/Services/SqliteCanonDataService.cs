using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CDArchive.Core.Data;
using CDArchive.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Services;

/// <summary>
/// ICanonDataService implementation backed by SQLite via EF Core.
/// On first run the database is automatically seeded from the legacy JSON files.
/// </summary>
public class SqliteCanonDataService : ICanonDataService
{
    // ── JSON serialisation options ───────────────────────────────────────────

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ── Paths ────────────────────────────────────────────────────────────────

    private readonly string _dbPath;
    private readonly CanonDataService _jsonService;   // used for first-run seeding

    // ── Thread-safety ────────────────────────────────────────────────────────

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;

    // ── ICanonDataService path properties ───────────────────────────────────

    public string ComposersFilePath => _dbPath;
    public string PiecesFilePath    => _dbPath;
    public string DbPath            => _dbPath;

    // ── Constructor ──────────────────────────────────────────────────────────

    public SqliteCanonDataService()
    {
        _jsonService = new CanonDataService();

        // Place the database alongside the legacy JSON files so both
        // remain in the same data directory.
        var dataDir = Path.GetDirectoryName(_jsonService.ComposersFilePath)!;
        _dbPath = Path.Combine(dataDir, "ClassicalCanon.db");
    }

    public SqliteCanonDataService(string dbPath, string dataDirectory)
    {
        _jsonService = new CanonDataService(dataDirectory);
        _dbPath = dbPath;
    }

    // ── Initialisation ───────────────────────────────────────────────────────

    /// <summary>
    /// Ensures the database exists, migrations are applied, and data is seeded
    /// from JSON files if the tables are empty.
    /// </summary>
    private async Task EnsureInitialisedAsync()
    {
        if (_initialised) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialised) return;

            using var ctx = CreateContext();
            await ctx.Database.EnsureCreatedAsync();

            // Apply any schema additions that post-date the initial EnsureCreated.
            await ApplySchemaUpgradesAsync(ctx);

            if (!await ctx.Pieces.AnyAsync())
                await SeedFromJsonAsync(ctx);

            _initialised = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Adds columns that were introduced after the database was first created.
    /// Uses "ALTER TABLE … ADD COLUMN IF NOT EXISTS" semantics via a PRAGMA check
    /// so each statement is safe to run on every startup.
    /// </summary>
    private static async Task ApplySchemaUpgradesAsync(CanonDbContext ctx)
    {
        // Each entry: (table, column, column-definition)
        var additions = new[]
        {
            ("Pieces", "ComposersJson", "TEXT"),
        };

        foreach (var (table, column, definition) in additions)
        {
            // PRAGMA table_info returns one row per column; if the column is absent the result is empty.
            // All interpolated values are compile-time literals — no injection risk.
#pragma warning disable EF1002
            var exists = await ctx.Database
                .SqlQueryRaw<int>(
                    $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = '{column}'")
                .AnyAsync();

            if (!exists)
                await ctx.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}");
#pragma warning restore EF1002
        }
    }

    private async Task SeedFromJsonAsync(CanonDbContext ctx)
    {
        var composers = await _jsonService.LoadComposersAsync();
        var pieces    = await _jsonService.LoadPiecesAsync();
        var pickLists = await _jsonService.LoadPickListsAsync();

        ctx.Composers.AddRange(composers.Select(MapComposerToRow));
        ctx.Pieces.AddRange(pieces.Select(MapPieceToRow));
        ctx.Settings.Add(new SettingRow
        {
            Key   = "pick_lists",
            Value = JsonSerializer.Serialize(pickLists, WriteOptions),
        });

        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Resets the initialisation flag so the next operation re-creates and
    /// re-seeds the database. Call this immediately before deleting the DB file.
    /// </summary>
    public void ResetInitialisation() => _initialised = false;

    private CanonDbContext CreateContext() => new(_dbPath);

    // ── ICanonDataService – Composers ────────────────────────────────────────

    public async Task<List<CanonComposer>> LoadComposersAsync()
    {
        await EnsureInitialisedAsync();
        using var ctx = CreateContext();
        var rows = await ctx.Composers
            .OrderBy(c => c.SortName)
            .ToListAsync();
        return rows.Select(MapRowToComposer).ToList();
    }

    public async Task SaveComposersAsync(List<CanonComposer> composers)
    {
        await EnsureInitialisedAsync();
        using var ctx = CreateContext();
        using var tx  = await ctx.Database.BeginTransactionAsync();

        await ctx.Composers.ExecuteDeleteAsync();
        ctx.Composers.AddRange(composers.Select(MapComposerToRow));
        await ctx.SaveChangesAsync();
        await tx.CommitAsync();

        // Keep JSON files in sync so they always reflect current DB state
        await _jsonService.SaveComposersAsync(composers);
    }

    // ── ICanonDataService – Pieces ───────────────────────────────────────────

    public async Task<List<CanonPiece>> LoadPiecesAsync()
    {
        await EnsureInitialisedAsync();
        using var ctx = CreateContext();
        var rows = await ctx.Pieces.ToListAsync();
        var pieces = rows.Select(MapRowToPiece).ToList();

        // Propagate parent catalog numbers to subpieces (same logic as JSON service)
        foreach (var piece in pieces)
            PropagateCatalogNumbers(piece);

        return pieces;
    }

    public async Task SavePiecesAsync(List<CanonPiece> pieces)
    {
        await EnsureInitialisedAsync();
        using var ctx = CreateContext();
        using var tx  = await ctx.Database.BeginTransactionAsync();

        await ctx.Pieces.ExecuteDeleteAsync();
        ctx.Pieces.AddRange(pieces.Select(MapPieceToRow));
        await ctx.SaveChangesAsync();
        await tx.CommitAsync();

        // Keep JSON files in sync so they always reflect current DB state
        await _jsonService.SavePiecesAsync(pieces);
    }

    // ── ICanonDataService – Pick Lists ───────────────────────────────────────

    public async Task<CanonPickLists> LoadPickListsAsync()
    {
        await EnsureInitialisedAsync();
        using var ctx = CreateContext();
        var row = await ctx.Settings.FindAsync("pick_lists");
        if (row?.Value == null)
            return new CanonPickLists();
        return JsonSerializer.Deserialize<CanonPickLists>(row.Value, ReadOptions)
               ?? new CanonPickLists();
    }

    public async Task SavePickListsAsync(CanonPickLists pickLists)
    {
        await EnsureInitialisedAsync();

        // Sort each list before saving (mirrors CanonDataService behaviour)
        pickLists.Forms.Sort(StringComparer.OrdinalIgnoreCase);
        pickLists.Categories.Sort(StringComparer.OrdinalIgnoreCase);
        pickLists.CatalogPrefixes.Sort(StringComparer.OrdinalIgnoreCase);
        pickLists.KeyTonalities.Sort(StringComparer.OrdinalIgnoreCase);

        using var ctx = CreateContext();
        var row = await ctx.Settings.FindAsync("pick_lists");
        var json = JsonSerializer.Serialize(pickLists, WriteOptions);

        if (row == null)
            ctx.Settings.Add(new SettingRow { Key = "pick_lists", Value = json });
        else
            row.Value = json;

        await ctx.SaveChangesAsync();

        // Keep JSON files in sync so they always reflect current DB state
        await _jsonService.SavePickListsAsync(pickLists);
    }

    // ── Mapping: CanonComposer ↔ ComposerRow ─────────────────────────────────

    private static ComposerRow MapComposerToRow(CanonComposer c) => new()
    {
        Name         = c.Name,
        SortName     = c.SortName,
        BirthDate    = c.BirthDate,
        BirthPlace   = c.BirthPlace,
        BirthState   = c.BirthState,
        BirthCountry = c.BirthCountry,
        BirthNotes   = c.BirthNotes,
        DeathDate    = c.DeathDate,
        DeathPlace   = c.DeathPlace,
        DeathState   = c.DeathState,
        DeathCountry = c.DeathCountry,
    };

    private static CanonComposer MapRowToComposer(ComposerRow r) => new()
    {
        Name         = r.Name,
        SortName     = r.SortName,
        BirthDate    = r.BirthDate,
        BirthPlace   = r.BirthPlace,
        BirthState   = r.BirthState,
        BirthCountry = r.BirthCountry,
        BirthNotes   = r.BirthNotes,
        DeathDate    = r.DeathDate,
        DeathPlace   = r.DeathPlace,
        DeathState   = r.DeathState,
        DeathCountry = r.DeathCountry,
    };

    // ── Mapping: CanonPiece ↔ PieceRow ───────────────────────────────────────

    private static PieceRow MapPieceToRow(CanonPiece p) => new()
    {
        Composer              = p.Composer,
        Form                  = p.Form,
        Title                 = p.Title,
        TitleEnglish          = p.TitleEnglish,
        Nickname              = p.Nickname,
        Subtitle              = p.Subtitle,
        Number                = p.Number,
        KeyTonality           = p.KeyTonality,
        KeyMode               = p.KeyMode,
        InstrumentationCategory = p.InstrumentationCategory,
        PublicationYear       = p.PublicationYear,
        NumberedSubpieces     = p.NumberedSubpieces,
        MusicNumber           = p.MusicNumber,
        FirstLine             = p.FirstLine,

        // Sort helpers — computed from CatalogInfo
        CatalogSortPrefix     = NullIfFfff(p.CatalogSortPrefix),
        CatalogSortNumber     = p.CatalogSortNumber == int.MaxValue ? null : p.CatalogSortNumber,
        CatalogSortSuffix     = NullIfEmpty(p.CatalogSortSuffix),

        // JSON blobs
        ComposersJson         = SerializeList(p.Composers),
        CatalogInfoJson       = SerializeList(p.CatalogInfo),
        InstrumentationJson   = SerializeElement(p.Instrumentation),
        CompositionYearsJson  = SerializeElement(p.CompositionYears),
        TextAuthorJson        = SerializeElement(p.TextAuthor),
        ArrangementsJson      = SerializeElement(p.Arrangements),
        RolesJson             = SerializeElement(p.Roles),
        CadenzaJson           = SerializeElement(p.Cadenza),
        TitleNumberJson       = SerializeElement(p.TitleNumber),
        TemposJson            = SerializeList(p.Tempos),
        SubpiecesJson         = SerializeList(p.Subpieces),
        VersionsJson          = SerializeList(p.Versions),
    };

    private static CanonPiece MapRowToPiece(PieceRow r) => new()
    {
        Composer              = r.Composer,
        Form                  = r.Form,
        Title                 = r.Title,
        TitleEnglish          = r.TitleEnglish,
        Nickname              = r.Nickname,
        Subtitle              = r.Subtitle,
        Number                = r.Number,
        KeyTonality           = r.KeyTonality,
        KeyMode               = r.KeyMode,
        InstrumentationCategory = r.InstrumentationCategory,
        PublicationYear       = r.PublicationYear,
        NumberedSubpieces     = r.NumberedSubpieces,
        MusicNumber           = r.MusicNumber,
        FirstLine             = r.FirstLine,

        Composers             = DeserializeList<ComposerCredit>(r.ComposersJson),
        CatalogInfo           = DeserializeList<CatalogInfo>(r.CatalogInfoJson),
        Instrumentation       = DeserializeElement(r.InstrumentationJson),
        CompositionYears      = DeserializeElement(r.CompositionYearsJson),
        TextAuthor            = DeserializeElement(r.TextAuthorJson),
        Arrangements          = DeserializeElement(r.ArrangementsJson),
        Roles                 = DeserializeElement(r.RolesJson),
        Cadenza               = DeserializeElement(r.CadenzaJson),
        TitleNumber           = DeserializeElement(r.TitleNumberJson),
        Tempos                = DeserializeList<TempoInfo>(r.TemposJson),
        Subpieces             = DeserializeList<CanonPiece>(r.SubpiecesJson),
        Versions              = DeserializeList<CanonPieceVersion>(r.VersionsJson),
    };

    // ── JSON helpers ─────────────────────────────────────────────────────────

    private static string? SerializeElement(JsonElement? element) =>
        element.HasValue ? JsonSerializer.Serialize(element.Value, WriteOptions) : null;

    private static JsonElement? DeserializeElement(string? json)
    {
        if (json == null) return null;
        return JsonSerializer.Deserialize<JsonElement>(json, ReadOptions);
    }

    private static string? SerializeList<T>(List<T>? list) =>
        list is { Count: > 0 } ? JsonSerializer.Serialize(list, WriteOptions) : null;

    private static List<T>? DeserializeList<T>(string? json)
    {
        if (json == null) return null;
        return JsonSerializer.Deserialize<List<T>>(json, ReadOptions);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static string? NullIfFfff(string? value) =>
        value == "\uFFFF" ? null : value;

    // ── Catalog number propagation (mirrors CanonDataService) ────────────────

    private static void PropagateCatalogNumbers(CanonPiece parent)
    {
        if (parent.Subpieces == null) return;

        var parentCatNum = parent.CatalogInfo?.FirstOrDefault()?.CatalogNumber;

        foreach (var sub in parent.Subpieces)
        {
            if (parentCatNum != null && sub.CatalogInfo is { Count: > 0 })
            {
                var subCat = sub.CatalogInfo[0];
                if (subCat.CatalogNumber == null && subCat.CatalogSubnumber != null)
                    subCat.CatalogNumber = parentCatNum;
            }

            PropagateCatalogNumbers(sub);
        }
    }
}
