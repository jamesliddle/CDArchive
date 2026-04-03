using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

public interface IConversionStatusService
{
    Task<List<(AlbumInfo Album, DiscInfo Disc, List<string> MissingMp3s)>> GetMissingConversionsAsync(CancellationToken ct = default);
}
