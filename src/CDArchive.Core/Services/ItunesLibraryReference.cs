using System.Text.RegularExpressions;
using System.Xml;
using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

/// <summary>
/// Mines the user's iTunes Music Library XML for composer info and work details.
/// This is the highest-priority reference source since it reflects the user's own conventions.
///
/// <para>
/// Threading / caching: the iTunes XML is potentially hundreds of MB. The
/// previous design read it twice — once eagerly via <see cref="LoadAllTracksAsync"/>
/// on every iTunes-Import view load, and once lazily into the
/// composer/works index on first lookup. The all-tracks path bypassed the
/// cache entirely. This class now does a single XML walk that populates
/// every projection (composers, works, AllTracks) into a shared
/// <c>LibraryCache</c>; <see cref="LoadAllTracksAsync"/> reads from the
/// cache too. See Rework H5.
/// </para>
///
/// <para>
/// Filtering: the composer / works index only considers tracks whose iTunes
/// <c>Location</c> URL contains the URL-encoded leaf segment of the user's
/// <c>ArchiveRootPath</c> — was hardcoded to <c>"CD%20archive"</c>, which
/// silently produced an empty cache for any user whose archive sat outside
/// a folder of that exact name. The leaf segment is computed from
/// <see cref="IArchiveSettings.ArchiveRootPath"/> at cache-build time, so a
/// settings change followed by <see cref="Refresh"/> picks up the new value.
/// See Rework H6.
/// </para>
/// </summary>
public class ItunesLibraryReference : ICatalogueReference
{
    public string SourceName => "iTunes Library";

    private static readonly Regex ComposerWithDatesRegex = new(
        @"^(.+?),\s*(.+?)\s*\((\d{3,4})\s*[–\-]\s*(\d{3,4})?\)$",
        RegexOptions.Compiled);

    private readonly string _libraryPath;
    private readonly IArchiveSettings? _archiveSettings;
    private Lazy<Task<LibraryCache>> _cache;

    /// <summary>
    /// Standard DI-friendly ctor. <paramref name="archiveSettings"/> drives
    /// the URL-encoded archive-folder filter used when indexing composers
    /// and works; pass null in test fixtures that don't care about that
    /// projection.
    /// </summary>
    public ItunesLibraryReference(IArchiveSettings? archiveSettings = null, string? libraryPath = null)
    {
        _archiveSettings = archiveSettings;
        _libraryPath = libraryPath
            ?? FindDefaultLibraryPath()
            ?? "";
        _cache = NewLazy();
    }

    private Lazy<Task<LibraryCache>> NewLazy() =>
        new(() => Task.Run(BuildCache));

    /// <summary>
    /// Invalidates the cached parse of the iTunes XML. The next access to
    /// any of <see cref="LoadAllTracksAsync"/>, <see cref="LookupComposerAsync"/>,
    /// or <see cref="LookupWorkAsync"/> re-reads the XML and rebuilds the
    /// projections. Use this when the user has made changes in iTunes that
    /// the app should pick up without restarting (or when
    /// <see cref="IArchiveSettings.ArchiveRootPath"/> has changed).
    /// </summary>
    public void Refresh()
    {
        _cache = NewLazy();
    }

    /// <summary>
    /// Returns every Music track in the iTunes XML (Podcasts, Movies, TV
    /// Shows, Audiobooks, Music Videos, and Books are filtered out). Reads
    /// from the in-memory cache after the first call; call
    /// <see cref="Refresh"/> to force a re-read.
    /// </summary>
    public async Task<IReadOnlyList<ItunesTrack>> LoadAllTracksAsync()
    {
        var cache = await _cache.Value.ConfigureAwait(false);
        return cache.AllTracks;
    }

    public async Task<ComposerInfo?> LookupComposerAsync(string lastName, string? firstName = null)
    {
        var cache = await _cache.Value;

        if (cache.Composers.TryGetValue(lastName.ToLowerInvariant(), out var matches))
        {
            if (firstName != null)
            {
                var exact = matches.FirstOrDefault(c =>
                    c.FirstName.StartsWith(firstName, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
            }
            return matches.First();
        }
        return null;
    }

    public async Task<WorkInfo?> LookupWorkAsync(string composerLastName, string workSearchTerm)
    {
        var cache = await _cache.Value;
        var key = composerLastName.ToLowerInvariant();

        if (!cache.Works.TryGetValue(key, out var works))
            return null;

        // Try exact match first, then substring
        var searchLower = workSearchTerm.ToLowerInvariant();
        var match = works.FirstOrDefault(w =>
            w.Title.Contains(searchLower, StringComparison.OrdinalIgnoreCase));

        return match;
    }

    /// <summary>
    /// Computes the URL-encoded archive-folder substring that
    /// <see cref="BuildCache"/> uses to filter location URLs. The default
    /// <c>D:\CD archive</c> produces <c>CD%20archive</c>, matching the
    /// pre-fix hardcoded value. A custom <c>ArchiveRootPath</c> produces
    /// the URL-encoded leaf of that path. Internal so the new
    /// <c>ItunesLibraryReferenceTests</c> can drive the contract directly.
    /// </summary>
    internal static string ComputeArchiveFolderFilter(string archiveRootPath)
    {
        if (string.IsNullOrWhiteSpace(archiveRootPath)) return "";
        // GetFileName on a path with a trailing separator returns "" — strip
        // any trailing slash/backslash first so e.g. "D:\CD archive\" still
        // resolves to "CD archive".
        var trimmed = archiveRootPath.TrimEnd('\\', '/');
        var leaf = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(leaf)) return "";
        // iTunes Location URLs encode spaces as %20 and use forward slashes.
        // Uri.EscapeDataString matches that convention for ASCII identifiers.
        return Uri.EscapeDataString(leaf);
    }

    private LibraryCache BuildCache()
    {
        var cache = new LibraryCache();
        if (string.IsNullOrEmpty(_libraryPath) || !File.Exists(_libraryPath))
            return cache;

        // Derive the substring filter from settings at build time. Defaults
        // to "CD%20archive" — the pre-fix hardcoded value — when the
        // settings come back with the default ArchiveRootPath.
        var archiveFolderFilter = ComputeArchiveFolderFilter(_archiveSettings?.ArchiveRootPath ?? "");

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
        using var reader = XmlReader.Create(_libraryPath, settings);

        if (!AdvanceToTracksDict(reader))
            return cache;

        var tracks = new List<ItunesTrack>();

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "dict")
                break; // End of Tracks dict

            if (reader.NodeType != XmlNodeType.Element || reader.Name != "key")
                continue;

            var trackIdStr = reader.ReadElementContentAsString();
            if (!int.TryParse(trackIdStr, out var trackId))
                continue;

            if (!AdvanceToElement(reader, "dict"))
                continue;

            var props = ReadDictProperties(reader);

            // Only include Music. iTunes uses boolean flags per non-Music media
            // kind (Podcast, Movie, TV Show, Audiobook, Music Video, Book);
            // Music itself is the absence of any such flag. The "Has Video"
            // flag catches video tracks that aren't explicitly tagged as
            // movies but still aren't music.
            if (IsTaggedTrue(props, "Podcast")     ||
                IsTaggedTrue(props, "Movie")       ||
                IsTaggedTrue(props, "TV Show")     ||
                IsTaggedTrue(props, "Audiobook")   ||
                IsTaggedTrue(props, "Music Video") ||
                IsTaggedTrue(props, "Has Video")   ||
                IsTaggedTrue(props, "Book"))
                continue;

            var name = props.GetValueOrDefault("Name", "");
            if (string.IsNullOrEmpty(name))
                continue;

            // MBID shortcut path: Picard writes "MusicBrainz Album Id" /
            // "MusicBrainz Track Id" / "MusicBrainz Work Id" tags to the
            // file, which iTunes surfaces via the Comments field (one
            // per line, "Key: value" or similar). Parse them out so the
            // planner can skip the MB search step entirely when MBIDs
            // are present.
            var comments = props.GetValueOrDefault("Comments", "");
            var (mbRelease, mbRecording, mbWork) = ParseMbIdsFromComments(comments);

            // Build the ItunesTrack projection used by the import view.
            tracks.Add(new ItunesTrack(
                TrackId:      trackId,
                PersistentId: NullIfEmpty(props.GetValueOrDefault("Persistent ID")),
                DiscNumber:   ParseIntOrNull(props.GetValueOrDefault("Disc Number")),
                TrackNumber:  ParseIntOrNull(props.GetValueOrDefault("Track Number")),
                Name:         name,
                DurationMs:   ParseIntOrNull(props.GetValueOrDefault("Total Time")),
                Genre:        NullIfEmpty(props.GetValueOrDefault("Genre")),
                Composer:     NullIfEmpty(props.GetValueOrDefault("Composer")),
                Album:        NullIfEmpty(props.GetValueOrDefault("Album")),
                AlbumArtist:  NullIfEmpty(props.GetValueOrDefault("Album Artist")),
                Artist:       NullIfEmpty(props.GetValueOrDefault("Artist")),
                DateAdded:    ParseDateOrNull(props.GetValueOrDefault("Date Added")),
                Location:     NullIfEmpty(props.GetValueOrDefault("Location")),
                MbReleaseId:   mbRelease,
                MbRecordingId: mbRecording,
                MbWorkId:      mbWork));

            // Composer / works indexing only considers tracks under the
            // user's archive folder. An empty filter (no settings injected
            // or empty ArchiveRootPath) indexes everything — useful for
            // headless tests.
            var location = props.GetValueOrDefault("Location", "");
            var passesArchiveFilter = archiveFolderFilter.Length == 0
                || location.Contains(archiveFolderFilter, StringComparison.OrdinalIgnoreCase);
            if (!passesArchiveFilter)
                continue;

            var composerRaw = props.GetValueOrDefault("Composer", "");
            if (!string.IsNullOrEmpty(composerRaw))
                IndexComposer(cache, composerRaw);

            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(composerRaw))
                IndexWork(cache, composerRaw, name);
        }

        cache.AllTracks = tracks;
        return cache;
    }

    private static int? ParseIntOrNull(string? s) =>
        int.TryParse(s, out var v) ? v : null;

    private static DateTime? ParseDateOrNull(string? s) =>
        DateTime.TryParse(s, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : null;

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrEmpty(s) ? null : s;

    // MusicBrainz UUID — 36 chars, 8-4-4-4-12 hex with dashes. We don't
    // need a strict validator; a length / hex-ish check keeps obviously-
    // malformed values out without rejecting real MBIDs.
    private static readonly System.Text.RegularExpressions.Regex MbidRegex = new(
        @"\b([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\b",
        System.Text.RegularExpressions.RegexOptions.Compiled
        | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Best-effort parse of MusicBrainz IDs out of an iTunes Comments
    /// field. MusicBrainz Picard writes lines like:
    /// <code>
    ///   MusicBrainz Album Id: 3e0c2f88-9bf9-4f4a-bd95-b6f9b87d3a8e
    ///   MusicBrainz Track Id: ...
    ///   MusicBrainz Work Id: ...
    /// </code>
    /// Returns (release, recording, work) — any combination of nulls.
    /// Tolerant to label variations ("MusicBrainz Release Id" / Picard's
    /// older "MusicBrainz Album Id"; "Track Id" / "Recording Id"; etc.).
    /// </summary>
    internal static (string? MbRelease, string? MbRecording, string? MbWork)
        ParseMbIdsFromComments(string? comments)
    {
        if (string.IsNullOrWhiteSpace(comments)) return (null, null, null);

        string? release = null, recording = null, work = null;
        foreach (var rawLine in comments.Split(new[] { '\n', '\r' },
                                                StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            // Find a UUID anywhere on the line first; if none, skip.
            var idMatch = MbidRegex.Match(line);
            if (!idMatch.Success) continue;
            var id = idMatch.Value.ToLowerInvariant();

            // Determine which slot it belongs to by the label substring.
            // Picard's "Album" maps to MB's "release" entity.
            var lower = line.ToLowerInvariant();
            if (release is null && (lower.Contains("album id") || lower.Contains("release id")))
                release = id;
            else if (recording is null && (lower.Contains("track id") || lower.Contains("recording id")))
                recording = id;
            else if (work is null && lower.Contains("work id"))
                work = id;
        }

        return (release, recording, work);
    }

    private static bool IsTaggedTrue(Dictionary<string, string> p, string key) =>
        string.Equals(p.GetValueOrDefault(key), "true", StringComparison.OrdinalIgnoreCase);

    private static void IndexComposer(LibraryCache cache, string composerRaw)
    {
        var match = ComposerWithDatesRegex.Match(composerRaw);
        if (!match.Success)
            return;

        var lastName = match.Groups[1].Value.Trim();
        var firstName = match.Groups[2].Value.Trim();
        var key = lastName.ToLowerInvariant();

        if (!cache.Composers.ContainsKey(key))
            cache.Composers[key] = new List<ComposerInfo>();

        // Don't duplicate
        if (cache.Composers[key].Any(c =>
            c.FirstName.Equals(firstName, StringComparison.OrdinalIgnoreCase)))
            return;

        var info = new ComposerInfo
        {
            LastName = lastName,
            FirstName = firstName,
            BirthYear = int.TryParse(match.Groups[3].Value, out var b) ? b : null,
            DeathYear = match.Groups[4].Success && int.TryParse(match.Groups[4].Value, out var d) ? d : null
        };

        cache.Composers[key].Add(info);
    }

    private static void IndexWork(LibraryCache cache, string composerRaw, string nameRaw)
    {
        var composerMatch = ComposerWithDatesRegex.Match(composerRaw);
        var composerKey = composerMatch.Success
            ? composerMatch.Groups[1].Value.Trim().ToLowerInvariant()
            : composerRaw.Split(',')[0].Trim().ToLowerInvariant();

        if (!cache.Works.ContainsKey(composerKey))
            cache.Works[composerKey] = new List<WorkInfo>();

        // Extract work title (before " - " movement separator)
        var dashIdx = nameRaw.IndexOf(" - ", StringComparison.Ordinal);
        var workTitle = dashIdx >= 0 ? nameRaw[..dashIdx].Trim() : nameRaw.Trim();
        var movementPart = dashIdx >= 0 ? nameRaw[(dashIdx + 3)..].Trim() : null;

        // Find or create the work entry
        var existing = cache.Works[composerKey]
            .FirstOrDefault(w => w.Title.Equals(workTitle, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            existing = new WorkInfo
            {
                Title = workTitle,
                ComposerLastName = composerKey
            };
            cache.Works[composerKey].Add(existing);
        }

        // Add movement if present
        if (movementPart != null)
        {
            var movNumMatch = Regex.Match(movementPart, @"^(\d+)\.\s*(.*)$");
            if (movNumMatch.Success)
            {
                var movNum = int.Parse(movNumMatch.Groups[1].Value);
                if (!existing.Movements.Any(m => m.Number == movNum))
                {
                    existing.Movements.Add(new MovementInfo
                    {
                        Number = movNum,
                        Title = movementPart
                    });
                }
            }
        }
    }

    private static bool AdvanceToTracksDict(XmlReader reader)
    {
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "key")
            {
                var text = reader.ReadElementContentAsString();
                if (text == "Tracks")
                {
                    // Next element should be the dict
                    return AdvanceToElement(reader, "dict");
                }
            }
        }
        return false;
    }

    private static bool AdvanceToElement(XmlReader reader, string elementName)
    {
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == elementName)
                return true;
        }
        return false;
    }

    private static Dictionary<string, string> ReadDictProperties(XmlReader reader)
    {
        var props = new Dictionary<string, string>();
        int depth = reader.Depth;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "dict" && reader.Depth == depth)
                break;

            if (reader.NodeType != XmlNodeType.Element || reader.Name != "key")
                continue;

            var key = reader.ReadElementContentAsString();

            // After ReadElementContentAsString the reader is positioned on the node
            // *following* </key> — which is the value element itself when iTunes
            // writes `<key>X</key><type>v</type>` with no whitespace between them.
            // An explicit reader.Read() here would skip *past* a self-closing value
            // element (<true/>, <false/>) and read the wrong sibling. MoveToContent
            // is a no-op when already on an Element and skips whitespace otherwise.
            if (reader.MoveToContent() != XmlNodeType.Element)
                continue;

            string value;
            if (reader.Name == "true")
            {
                value = "true";
                if (!reader.IsEmptyElement) reader.Read();
            }
            else if (reader.Name == "false")
            {
                value = "false";
                if (!reader.IsEmptyElement) reader.Read();
            }
            else
            {
                value = reader.ReadElementContentAsString();
            }

            props[key] = value;
        }

        return props;
    }

    private static string? FindDefaultLibraryPath()
    {
        var musicFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var candidate = Path.Combine(musicFolder, "iTunes", "iTunes Music Library.xml");
        return File.Exists(candidate) ? candidate : null;
    }

    private class LibraryCache
    {
        public IReadOnlyList<ItunesTrack> AllTracks { get; set; } = Array.Empty<ItunesTrack>();
        public Dictionary<string, List<ComposerInfo>> Composers { get; } = new();
        public Dictionary<string, List<WorkInfo>> Works { get; } = new();
    }
}
