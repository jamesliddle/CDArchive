using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

public interface IArchiveScannerService
{
    Task<List<AlbumInfo>> ScanArchiveAsync(CancellationToken ct = default);
    Task<List<ValidationResult>> ValidateArchiveAsync(IProgress<int>? progress = null, CancellationToken ct = default);
}
