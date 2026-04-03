using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

public interface IConversionService
{
    Task<ConversionBatch> ConvertAlbumAsync(AlbumInfo album, IProgress<ConversionJob>? progress = null, CancellationToken ct = default);
    Task<ConversionJob> ConvertFileAsync(string flacPath, string mp3Path, CancellationToken ct = default);
}
