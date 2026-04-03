using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

/// <summary>
/// Queries the MusicBrainz API for composer info and work details.
/// Used as a fallback when the iTunes library doesn't have the data.
/// Rate-limited to 1 request per second per MusicBrainz policy.
/// </summary>
public class MusicBrainzReference : ICatalogueReference
{
    public string SourceName => "MusicBrainz";

    private readonly HttpClient _http;
    private DateTime _lastRequest = DateTime.MinValue;
    private static readonly TimeSpan RateLimit = TimeSpan.FromMilliseconds(1100);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public MusicBrainzReference(HttpClient? httpClient = null)
    {
        _http = httpClient ?? CreateDefaultClient();
    }

    public async Task<ComposerInfo?> LookupComposerAsync(string lastName, string? firstName = null)
    {
        var query = firstName != null
            ? $"artist:\"{firstName} {lastName}\""
            : $"artist:\"{lastName}\"";

        var url = $"https://musicbrainz.org/ws/2/artist?query={HttpUtility.UrlEncode(query)}&fmt=json&limit=5";
        var response = await RateLimitedGetAsync<MbArtistSearchResult>(url);
        if (response?.Artists == null || response.Artists.Count == 0)
            return null;

        // Prefer composers (type = "Person") with matching name
        var match = response.Artists
            .Where(a => a.Type == "Person")
            .FirstOrDefault(a =>
                a.Name.Contains(lastName, StringComparison.OrdinalIgnoreCase) ||
                a.SortName.StartsWith(lastName, StringComparison.OrdinalIgnoreCase));

        if (match == null)
            return null;

        var nameParts = match.SortName.Split(',', 2);
        return new ComposerInfo
        {
            LastName = nameParts[0].Trim(),
            FirstName = nameParts.Length > 1 ? nameParts[1].Trim() : match.Name,
            BirthYear = ParseYear(match.LifeSpan?.Begin),
            DeathYear = ParseYear(match.LifeSpan?.End)
        };
    }

    public async Task<WorkInfo?> LookupWorkAsync(string composerLastName, string workSearchTerm)
    {
        var query = $"artist:\"{composerLastName}\" AND work:\"{workSearchTerm}\"";
        var url = $"https://musicbrainz.org/ws/2/work?query={HttpUtility.UrlEncode(query)}&fmt=json&limit=10";
        var response = await RateLimitedGetAsync<MbWorkSearchResult>(url);
        if (response?.Works == null || response.Works.Count == 0)
            return null;

        // Find best match
        var searchLower = workSearchTerm.ToLowerInvariant();
        var match = response.Works
            .FirstOrDefault(w => w.Title.Contains(searchLower, StringComparison.OrdinalIgnoreCase))
            ?? response.Works.First();

        var work = new WorkInfo
        {
            Title = match.Title,
            ComposerLastName = composerLastName
        };

        // Fetch the work details to get parts (movements)
        if (!string.IsNullOrEmpty(match.Id))
        {
            var detailUrl = $"https://musicbrainz.org/ws/2/work/{match.Id}?inc=work-rels&fmt=json";
            var detail = await RateLimitedGetAsync<MbWorkDetail>(detailUrl);
            if (detail?.Relations != null)
            {
                int movNum = 1;
                foreach (var rel in detail.Relations
                    .Where(r => r.Type == "parts" && r.Direction == "backward" && r.Work != null)
                    .OrderBy(r => r.OrderingKey ?? 0))
                {
                    work.Movements.Add(new MovementInfo
                    {
                        Number = movNum++,
                        Title = rel.Work!.Title
                    });
                }
            }
        }

        return work;
    }

    private async Task<T?> RateLimitedGetAsync<T>(string url) where T : class
    {
        var elapsed = DateTime.UtcNow - _lastRequest;
        if (elapsed < RateLimit)
            await Task.Delay(RateLimit - elapsed);

        _lastRequest = DateTime.UtcNow;

        try
        {
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static int? ParseYear(string? dateStr)
    {
        if (string.IsNullOrEmpty(dateStr))
            return null;
        // MusicBrainz dates can be "1855", "1855-01-20", etc.
        var yearPart = dateStr.Split('-')[0];
        return int.TryParse(yearPart, out var y) ? y : null;
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CDArchive/1.0 (https://github.com/cdarchive)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    // MusicBrainz JSON response models

    private class MbArtistSearchResult
    {
        public List<MbArtist> Artists { get; set; } = new();
    }

    private class MbArtist
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        [JsonPropertyName("sort-name")]
        public string SortName { get; set; } = "";
        public string? Type { get; set; }
        [JsonPropertyName("life-span")]
        public MbLifeSpan? LifeSpan { get; set; }
    }

    private class MbLifeSpan
    {
        public string? Begin { get; set; }
        public string? End { get; set; }
        public bool? Ended { get; set; }
    }

    private class MbWorkSearchResult
    {
        public List<MbWork> Works { get; set; } = new();
    }

    private class MbWork
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
    }

    private class MbWorkDetail
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public List<MbRelation>? Relations { get; set; }
    }

    private class MbRelation
    {
        public string Type { get; set; } = "";
        public string? Direction { get; set; }
        [JsonPropertyName("ordering-key")]
        public int? OrderingKey { get; set; }
        public MbWork? Work { get; set; }
    }
}
