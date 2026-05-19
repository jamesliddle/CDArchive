namespace CDArchive.Core.Models;

/// <summary>
/// Outcome of a single audio-file tag-write attempt. Returned per file (one
/// per MP3 + one per matched FLAC sibling) so a batch caller can report
/// successes and failures independently rather than aborting on the first
/// throwing file.
/// </summary>
/// <param name="FilePath">Absolute path of the file the writer targeted.</param>
/// <param name="Success">True if the file's tags were updated atomically.</param>
/// <param name="ErrorMessage">
/// Brief, human-readable failure reason when <paramref name="Success"/> is
/// false; null on success. The full exception is in the application log.
/// </param>
public sealed record WriteResult(string FilePath, bool Success, string? ErrorMessage);
