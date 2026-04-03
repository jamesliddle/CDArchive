using System.Text.RegularExpressions;
using System.Xml;
using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

/// <summary>
/// Mines the user's iTunes Music Library XML for composer info and work details.
/// This is the highest-priority reference source since it reflects the user's own conventions.
/// </summary>
public class ItunesLibraryReference : ICatalogueReference
{
    public string SourceName => "iTunes Library";

    private static readonly Regex ComposerWithDatesRegex = new(
        @"^(.+?),\s*(.+?)\s*\((\d{3,4})\s*[\u2013\-]\s*(\d{3,4})?\)$",
        RegexOptions.Compiled);

    private readonly string _libraryPath;
    private readonly Lazy<Task<LibraryCache>> _cache;

    public ItunesLibraryReference(string? libraryPath = null)
    {
        _libraryPath = libraryPath
            ?? FindDefaultLibraryPath()
            ?? "";
        _cache = new Lazy<Task<LibraryCache>>(() => Task.Run(BuildCache));
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

    private LibraryCache BuildCache()
    {
        var cache = new LibraryCache();
        if (string.IsNullOrEmpty(_libraryPath) || !File.Exists(_libraryPath))
            return cache;

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
        using var reader = XmlReader.Create(_libraryPath, settings);

        // Navigate to the Tracks dict
        if (!AdvanceToTracksDict(reader))
            return cache;

        // Read each track
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "dict")
                break; // End of Tracks dict

            if (reader.NodeType != XmlNodeType.Element || reader.Name != "key")
                continue;

            reader.Read(); // track ID value
            if (reader.NodeType != XmlNodeType.Text)
                continue;

            // Now read the track's dict
            if (!AdvanceToElement(reader, "dict"))
                continue;

            var trackProps = ReadDictProperties(reader);
            var composerRaw = trackProps.GetValueOrDefault("Composer", "");
            var nameRaw = trackProps.GetValueOrDefault("Name", "");
            var location = trackProps.GetValueOrDefault("Location", "");

            // Only index tracks from the CD archive
            if (!location.Contains("CD%20archive", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(composerRaw))
                IndexComposer(cache, composerRaw);

            if (!string.IsNullOrEmpty(nameRaw) && !string.IsNullOrEmpty(composerRaw))
                IndexWork(cache, composerRaw, nameRaw);
        }

        return cache;
    }

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
            if (!reader.Read())
                break;

            string value;
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "true")
                {
                    value = "true";
                    if (reader.IsEmptyElement) continue;
                    reader.Read(); // skip end element
                }
                else if (reader.Name == "false")
                {
                    value = "false";
                    if (reader.IsEmptyElement) continue;
                    reader.Read();
                }
                else
                {
                    value = reader.ReadElementContentAsString();
                }
            }
            else
            {
                value = reader.Value;
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
        public Dictionary<string, List<ComposerInfo>> Composers { get; } = new();
        public Dictionary<string, List<WorkInfo>> Works { get; } = new();
    }
}
