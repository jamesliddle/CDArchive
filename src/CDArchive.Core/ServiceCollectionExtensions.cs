using CDArchive.Core.Data;
using CDArchive.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CDArchive.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        // Make ILogger<T> + ILoggerFactory resolvable for every consumer.
        // A host that wants real log output (the App project does, via Serilog)
        // then calls services.AddLogging(b => b.AddSerilog(...)) — the resulting
        // ILoggerProvider is appended to the same LoggerFactory registered here.
        // Tests that don't call AddLogging again still get a working pipeline
        // with no providers attached, which is effectively a no-op.
        services.AddLogging();

        services.AddSingleton<IArchiveSettings, ArchiveSettings>();
        services.AddSingleton<IArchiveAudioLocator, ArchiveAudioLocator>();
        services.AddSingleton<IAudioPlayerService, NAudioPlayerService>();
        services.AddTransient<IFileSystemService, FileSystemService>();
        services.AddTransient<IAlbumScaffoldingService, AlbumScaffoldingService>();
        services.AddTransient<IDuplicateDetectionService, DuplicateDetectionService>();
        services.AddTransient<IArchiveScannerService, ArchiveScannerService>();
        services.AddTransient<IConversionService, FfmpegConversionService>();
        services.AddTransient<IConversionStatusService, ConversionStatusService>();
        services.AddSingleton<LocalCatalogueReference>();
        services.AddSingleton<ItunesLibraryReference>();
        // MusicBrainzReference is a singleton (it owns the per-process rate-limit
        // gate state — semaphore + monotonic clock — that the C10 fix relies on
        // to serialise callers). Because the typed-client lifetime would be
        // transient, we register a named HttpClient via IHttpClientFactory and
        // the singleton calls factory.CreateClient(HttpClientName) per request
        // — that's the MS-recommended pattern for "long-lived consumer of an
        // HttpClient with handler rotation". User-Agent is set on the named
        // client so every request to MB carries a real version + contact URL.
        services.AddHttpClient(MusicBrainzReference.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(MusicBrainzReference.BuildUserAgent());
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });
        services.AddSingleton<MusicBrainzReference>();
        services.AddSingleton<CompositeCatalogueReference>();
        services.AddTransient<ICataloguingService, CataloguingService>();

        // Canon data path — SQLite is the sole source of truth at runtime:
        //   • SqliteCanonDataService is the only ICanonDataService implementation.
        //     Every Load reads from SQLite; every Save writes to SQLite. JSON files
        //     are never touched during normal Load/Save — they exist purely for
        //     ad hoc import/export via the Import/Export screen or the SeedDb tool.
        //   • CanonDbContext is created on-demand via IDbContextFactory; the data
        //     service constructs short-lived contexts per Load/Save call. Schema
        //     upgrades (PRAGMA-guarded ALTER TABLEs) run inside EnsureInitializedAsync
        //     so the database evolves with the model without manual migrations.
        //   • CanonDataService is retained as a JSON helper (path resolution and
        //     ad-hoc serialize/deserialize for the Import/Export screen). It is
        //     intentionally NOT registered as ICanonDataService; nothing in the
        //     runtime data flow uses it. SqliteCanonDataService takes a reference
        //     only to reuse its data-directory probe for locating the .db file.
        services.AddSingleton<CanonDataService>();
        services.AddDbContextFactory<CanonDbContext>((sp, options) =>
        {
            var json    = sp.GetRequiredService<CanonDataService>();
            var dataDir = Path.GetDirectoryName(json.ComposersFilePath)!;
            var dbPath  = Path.Combine(dataDir, "ClassicalCanon.db");
            options.UseSqlite($"Data Source={dbPath}");
        });
        services.AddSingleton<ICanonDataService, SqliteCanonDataService>();
        services.AddSingleton<PieceReferenceIndex>();

        return services;
    }
}
