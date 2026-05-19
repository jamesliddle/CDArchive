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
    /// Writes the catalogue entry metadata to audio file tags (MP3 + sibling
    /// FLAC where present). Writes are atomic (copy-to-temp + atomic rename)
    /// so a process kill mid-batch can't corrupt the user's source audio.
    /// Per-file failures are captured in the returned list and do not abort
    /// the batch — a single locked or corrupt file leaves the remaining
    /// entries unaffected.
    /// </summary>
    /// <returns>
    /// One <see cref="WriteResult"/> per file actually attempted (one for
    /// each MP3, plus one for each matched FLAC sibling). Caller can sum
    /// successes / failures to build a status message.
    /// </returns>
    Task<IReadOnlyList<WriteResult>> WriteTagsAsync(IEnumerable<CatalogueEntry> entries);
}
