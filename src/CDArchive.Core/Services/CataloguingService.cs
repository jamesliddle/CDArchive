using CDArchive.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CDArchive.Core.Services;

public class CataloguingService : ICataloguingService
{
    private readonly CompositeCatalogueReference _reference;
    private readonly ILogger<CataloguingService> _logger;

    public CataloguingService(
        CompositeCatalogueReference reference,
        ILogger<CataloguingService>? logger = null)
    {
        _reference = reference;
        _logger    = logger ?? NullLogger<CataloguingService>.Instance;
    }

    public async Task<List<CatalogueEntry>> ReadAlbumTagsAsync(string albumPath)
    {
        var entries = new List<CatalogueEntry>();
        var mp3Folder = FindMp3Folder(albumPath);
        if (mp3Folder == null)
            return entries;

        var mp3Files = Directory.GetFiles(mp3Folder, "*.mp3")
            .OrderBy(f => f)
            .ToArray();

        var albumName = DeriveAlbumName(albumPath);
        int trackCount = mp3Files.Length;

        for (int i = 0; i < mp3Files.Length; i++)
        {
            var entry = ReadFileTag(mp3Files[i], albumName, i + 1, trackCount);
            entries.Add(entry);
        }

        // Parse and format using references
        await FormatEntriesAsync(entries, albumName);

        return entries;
    }

    public Task<IReadOnlyList<WriteResult>> WriteTagsAsync(IEnumerable<CatalogueEntry> entries)
    {
        return Task.Run<IReadOnlyList<WriteResult>>(() =>
        {
            var results = new List<WriteResult>();
            foreach (var entry in entries)
            {
                foreach (var path in EnumerateWriteTargets(entry.FilePath))
                {
                    var result = WriteFileTag(path, entry);
                    results.Add(result);
                    if (!result.Success)
                    {
                        // Per C12: each per-file failure stays a per-file
                        // failure — the loop keeps going so a corrupt MP3
                        // in the middle of a 49-track batch doesn't strand
                        // the remaining 48 files.
                        _logger.LogWarning(
                            "Tag write failed for {Path}: {Reason}",
                            path, result.ErrorMessage ?? "(no message)");
                    }
                }
            }

            var successes = results.Count(r => r.Success);
            var failures  = results.Count - successes;
            if (failures == 0)
                _logger.LogInformation("Tag write batch: {Success} file(s) written", successes);
            else
                _logger.LogWarning("Tag write batch: {Success} succeeded, {Failed} failed", successes, failures);

            return results;
        });
    }

    /// <summary>
    /// Yields the actual file paths that <see cref="WriteTagsAsync"/> will
    /// touch for a single <see cref="CatalogueEntry"/>: the MP3 itself, plus
    /// the sibling FLAC at the conventional location if one exists. Exposed
    /// as <c>internal</c> so the unit tests can exercise the file-list
    /// expansion without going through TagLib.
    /// </summary>
    internal IEnumerable<string> EnumerateWriteTargets(string mp3Path)
    {
        yield return mp3Path;

        var flacPath = GetSiblingFormatPath(mp3Path, "MP3", "FLAC", ".flac");
        if (flacPath != null)
            yield return flacPath;
    }

    /// <summary>
    /// The core transformation pipeline: filenames → parsed fields → reference lookups → formatted entries.
    /// Parses from filenames rather than TIT2 tags to avoid re-processing already-modified data.
    /// </summary>
    private async Task FormatEntriesAsync(List<CatalogueEntry> entries, string albumName)
    {
        // Step 1: Parse each filename into structured fields (filenames never change)
        var parsed = entries.Select(e => (Entry: e, Parsed: TagParser.ParseFilename(
            Path.GetFileName(e.FilePath)))).ToList();

        // Step 2: Group by work title to determine movement counts
        var workGroups = parsed
            .Where(p => p.Parsed.RawWork != null)
            .GroupBy(p => NormalizeWorkKey(p.Parsed.RawWork!))
            .ToList();

        // Step 3: Look up composers and build formatted entries
        var composerCache = new Dictionary<string, ComposerInfo?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (entry, tag) in parsed)
        {
            // Parse composer name
            string composerLast = "", composerFirst = "";
            if (tag.RawComposer != null)
            {
                (composerLast, composerFirst) = TagParser.ParseComposerName(tag.RawComposer);
            }

            // Look up composer (cached per last name)
            if (!string.IsNullOrEmpty(composerLast) && !composerCache.ContainsKey(composerLast))
            {
                composerCache[composerLast] = await _reference.LookupComposerAsync(
                    composerLast,
                    string.IsNullOrEmpty(composerFirst) ? null : composerFirst);
            }

            var composerInfo = !string.IsNullOrEmpty(composerLast)
                ? composerCache.GetValueOrDefault(composerLast)
                : null;

            // Format composer field
            if (composerInfo != null)
            {
                entry.Composer = composerInfo.Formatted;
            }
            else if (!string.IsNullOrEmpty(composerLast))
            {
                // Restore diacritics on the raw name at least
                entry.Composer = TagParser.RestoreDiacritics($"{composerLast}, {composerFirst}");
            }

            // Format work title
            var workTitle = tag.RawWork ?? "";
            workTitle = TagParser.ExpandAbbreviations(workTitle);
            workTitle = TagParser.RestoreDiacritics(workTitle);
            workTitle = CataloguingRules.HyphenateKeys(workTitle);

            // Format movement
            string? movement = null;
            if (tag.RawMovement != null)
            {
                var movText = TagParser.ExpandAbbreviations(tag.RawMovement);
                movText = TagParser.RestoreDiacritics(movText);
                movText = TagParser.NormalizeTempoSeparators(movText);

                // Determine movement number and total
                var movNum = tag.MovementNumber ?? 0;
                var workKey = NormalizeWorkKey(tag.RawWork ?? "");
                var totalMovements = workGroups
                    .FirstOrDefault(g => g.Key == workKey)?.Count() ?? 1;

                if (movNum > 0)
                {
                    movement = CataloguingRules.PadMovementNumber(
                        $"{movNum}. {movText}", totalMovements);
                }
                else
                {
                    movement = movText;
                }
            }

            // For standalone entries (no movement), check if the work title
            // contains a section word like "Overture" that should be split off
            // as an unnumbered section (spaced dash) rather than joined with em-dash.
            if (movement == null)
            {
                var (work, section) = TagParser.SplitSectionWord(workTitle);
                if (section != null)
                {
                    workTitle = work;
                    movement = section;
                }
                else
                {
                    workTitle = TagParser.InsertFormDash(workTitle);
                }
            }
            else
            {
                workTitle = TagParser.InsertFormDash(workTitle);
            }

            // Assemble final Name
            entry.Name = CataloguingRules.FormatTrackName(workTitle, movement);

            // Set album from folder name; clear fields that are unreliable
            // in raw classical CD tags and should be set manually.
            entry.Album = albumName;
            entry.Artist = "";
            entry.Genre = "";
            entry.Year = null;
            entry.DiscNumber = null;
            entry.DiscCount = null;
        }
    }

    private static string NormalizeWorkKey(string rawWork)
    {
        return rawWork.Trim().ToLowerInvariant();
    }

    private static string? FindMp3Folder(string albumPath)
    {
        var directMp3 = Path.Combine(albumPath, "MP3");
        if (Directory.Exists(directMp3))
            return directMp3;

        foreach (var disc in Directory.GetDirectories(albumPath, "Disc *").OrderBy(d => d))
        {
            var mp3InDisc = Path.Combine(disc, "MP3");
            if (Directory.Exists(mp3InDisc))
                return mp3InDisc;
        }

        if (Directory.GetFiles(albumPath, "*.mp3").Length > 0)
            return albumPath;

        return null;
    }

    private static string DeriveAlbumName(string albumPath)
    {
        var dir = new DirectoryInfo(albumPath);
        if (dir.Name.Equals("MP3", StringComparison.OrdinalIgnoreCase))
            dir = dir.Parent!;
        if (dir.Name.StartsWith("Disc ", StringComparison.OrdinalIgnoreCase))
            dir = dir.Parent!;
        return dir.Name;
    }

    private CatalogueEntry ReadFileTag(string filePath, string albumName, int trackNumber, int trackCount)
    {
        var entry = new CatalogueEntry
        {
            FilePath = filePath,
            TrackNumber = trackNumber,
            TrackCount = trackCount,
            Album = albumName,
        };

        try
        {
            using var file = TagLib.File.Create(filePath);
            var tag = file.Tag;

            entry.Name = tag.Title ?? "";
            entry.Artist = tag.JoinedPerformers ?? "";
            entry.Composer = tag.JoinedComposers ?? "";
            entry.Genre = tag.JoinedGenres ?? "";
            entry.Year = tag.Year > 0 ? (int)tag.Year : null;
            entry.DiscNumber = tag.Disc > 0 ? (int)tag.Disc : null;
            entry.DiscCount = tag.DiscCount > 0 ? (int)tag.DiscCount : null;
            entry.Album = !string.IsNullOrEmpty(tag.Album) ? tag.Album : albumName;
            entry.SortName = tag.TitleSort ?? "";
            entry.SortAlbum = tag.AlbumSort ?? "";
            entry.SortArtist = tag.PerformersSort?.FirstOrDefault() ?? "";
            entry.SortComposer = tag.ComposersSort?.FirstOrDefault() ?? "";
        }
        catch (Exception ex)
        {
            // TagLib failure: corrupt file, locked, unsupported format, IO error.
            // Falling back to the filename keeps the batch moving (the caller may
            // overwrite this entry's tags via WriteTagsAsync, so the log line is
            // important: it warns the user that we couldn't *read* the original
            // tags and they're about to be replaced by filename-derived data).
            _logger.LogWarning(ex, "Failed to read tags from {FilePath}; falling back to filename", filePath);
            entry.Name = Path.GetFileNameWithoutExtension(filePath);
        }

        return entry;
    }

    /// <summary>
    /// Given a file in one format folder (e.g., MP3), returns the corresponding
    /// file in a sibling format folder (e.g., FLAC) if it exists.
    /// </summary>
    private static string? GetSiblingFormatPath(string filePath, string sourceFolder, string targetFolder, string targetExtension)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir == null || !dir.EndsWith(sourceFolder, StringComparison.OrdinalIgnoreCase))
            return null;

        var parentDir = Path.GetDirectoryName(dir);
        if (parentDir == null)
            return null;

        var siblingDir = Path.Combine(parentDir, targetFolder);
        if (!Directory.Exists(siblingDir))
            return null;

        var stem = Path.GetFileNameWithoutExtension(filePath);
        var candidate = Path.Combine(siblingDir, stem + targetExtension);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Writes <paramref name="entry"/>'s metadata to <paramref name="filePath"/>'s
    /// audio tags atomically. Returns a per-file <see cref="WriteResult"/>;
    /// never throws.
    /// </summary>
    /// <remarks>
    /// Per C13: <c>TagLib.File.Save()</c> rewrites the underlying audio file
    /// in place. A process kill between the truncate and the complete write
    /// leaves the file corrupt — for a FLAC rip of a CD the user no longer
    /// owns, that's unrecoverable. We avoid the window by writing to a
    /// sibling temp file in the same directory (same volume → <see cref="File.Move"/>
    /// is atomic) and only then replacing the original.
    /// </remarks>
    private WriteResult WriteFileTag(string filePath, CatalogueEntry entry)
    {
        return TryAtomicWrite(filePath, tempPath =>
        {
            using var file = TagLib.File.Create(tempPath);
            var tag = file.Tag;

            tag.Title = entry.Name;
            tag.Performers = [entry.Artist];
            tag.Album = entry.Album;
            tag.Composers = [entry.Composer];
            tag.Genres = string.IsNullOrEmpty(entry.Genre) ? [] : [entry.Genre];
            tag.Track = (uint)entry.TrackNumber;
            tag.TrackCount = (uint)entry.TrackCount;
            tag.Year = entry.Year.HasValue ? (uint)entry.Year.Value : 0;

            tag.Disc = entry.DiscNumber.HasValue ? (uint)entry.DiscNumber.Value : 0;
            tag.DiscCount = entry.DiscCount.HasValue ? (uint)entry.DiscCount.Value : 0;

            if (!string.IsNullOrEmpty(entry.SortName))
                tag.TitleSort = entry.SortName;
            if (!string.IsNullOrEmpty(entry.SortAlbum))
                tag.AlbumSort = entry.SortAlbum;
            if (!string.IsNullOrEmpty(entry.SortArtist))
                tag.PerformersSort = [entry.SortArtist];
            if (!string.IsNullOrEmpty(entry.SortComposer))
                tag.ComposersSort = [entry.SortComposer];

            file.Save();
        });
    }

    /// <summary>
    /// Atomic file-mutation primitive: copy <paramref name="path"/> to a
    /// sibling temp file, hand the temp file to <paramref name="mutate"/>,
    /// then atomically replace the original with the mutated copy. On any
    /// failure the temp file is best-effort deleted and the original is
    /// left untouched.
    /// </summary>
    /// <remarks>
    /// Internal so the unit tests can exercise the primitive directly with
    /// a non-TagLib mutation — the atomic-rename behaviour is the
    /// data-safety contract that matters; the specific tag library used
    /// to mutate the temp file isn't.
    ///
    /// The temp file lives in the same directory as the target (not %TEMP%)
    /// so it lands on the same volume; <see cref="File.Move(string, string, bool)"/>
    /// is then an atomic directory-entry rename on NTFS / ext4 / APFS.
    /// </remarks>
    internal WriteResult TryAtomicWrite(string path, Action<string> mutate)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
            // No directory component — can't create a same-volume sibling.
            // Probably a malformed path; treat as failure.
            return new WriteResult(path, Success: false,
                ErrorMessage: "Cannot atomic-write a file with no directory component.");

        // Random-suffix temp name avoids collisions when two parallel batches
        // happen to target the same file (rare but possible).
        var tempPath = Path.Combine(dir,
            Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        try
        {
            File.Copy(path, tempPath, overwrite: false);
            mutate(tempPath);
            File.Move(tempPath, path, overwrite: true);
            return new WriteResult(path, Success: true, ErrorMessage: null);
        }
        catch (Exception ex)
        {
            // Best-effort cleanup. If this also fails (rare: AV mid-quarantine),
            // we leak a temp file but the original is intact — the right
            // trade. Don't let the cleanup error mask the original failure.
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx,
                    "Failed to clean up temp file {TempPath} after a failed atomic write",
                    tempPath);
            }

            return new WriteResult(path, Success: false, ErrorMessage: ex.Message);
        }
    }
}
