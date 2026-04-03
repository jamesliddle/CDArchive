using CDArchive.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CDArchive.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IArchiveSettings, ArchiveSettings>();
        services.AddTransient<IFileSystemService, FileSystemService>();
        services.AddTransient<IAlbumScaffoldingService, AlbumScaffoldingService>();
        services.AddTransient<IDuplicateDetectionService, DuplicateDetectionService>();
        services.AddTransient<IArchiveScannerService, ArchiveScannerService>();
        services.AddTransient<IConversionService, FfmpegConversionService>();
        services.AddTransient<IConversionStatusService, ConversionStatusService>();
        services.AddSingleton<LocalCatalogueReference>();
        services.AddSingleton<ItunesLibraryReference>();
        services.AddSingleton<MusicBrainzReference>();
        services.AddSingleton<CompositeCatalogueReference>();
        services.AddTransient<ICataloguingService, CataloguingService>();
        services.AddSingleton<ICanonDataService, CanonDataService>();

        return services;
    }
}
