using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

public interface ICataloguingService
{
    /// <summary>
    /// Reads existing metadata from MP3 files in an album folder and returns
    /// catalogue entries with the raw tag values populated.
    /// </summary>
    Task<List<CatalogueEntry>> ReadAlbumTagsAsync(string albumPath);

    /// <summary>
    /// Writes the catalogue entry metadata to audio file tags (MP3 and FLAC).
    /// Returns the total number of files written.
    /// </summary>
    Task<int> WriteTagsAsync(IEnumerable<CatalogueEntry> entries);
}
