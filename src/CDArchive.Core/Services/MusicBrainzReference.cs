using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using CDArchive.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CDArchive.Core.Services;

/// <summary>
/// Queries the MusicBrainz API for composer info and work details. Used as a
/// fallback when the iTunes library doesn't have the data. MB asks clients to
/// stay under 1 request per second; we serialise through a process-wide gate
/// (<see cref="SemaphoreSlim"/> + <see cref="Stopwatch"/>) so concurrent callers
/// always observe the minimum gap, and retry transient failures with
/// exponential backoff.
/// </summary>
public class MusicBrainzReference : ICatalogueReference
{
    /// <summary>
    /// Name used by <see cref="ServiceCollectionExtensions.AddCoreServices"/>
    /// when registering the named <see cref="HttpClient"/> via
    /// <see cref="IHttpClientFactory"/>. Held as a constant so tests can use
    /// the same key.
    /// </summary>
    public const string HttpClientName = "MusicBrainz";

    public string SourceName => "MusicBrainz";

    // 1.1 seconds rather than 1.0 — MB's policy is "no more than 1 request per
    // second"; the 100ms cushion absorbs clock drift and avoids 503s on the
    // boundary.
    private static readonly TimeSpan RateLimit = TimeSpan.FromMilliseconds(1100);

    // 3 attempts total = 1 initial + 2 retries. 500ms / 1000ms backoff between
    // retries; both are bounded by RateLimit anyway because we re-enter the gate.
    private const int MaxAttempts = 3;
    private static readonly TimeSpan[] BackoffDelays = { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MusicBrainzReference> _logger;

    // Process-wide serialiser for the rate-limit gate. Concurrent callers
    // queue here; one holds the gate at a time and observes the actual
    // monotonic gap from _lastRequestTickMs.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastRequestTickMs = long.MinValue / 2; // far in the past → first request fires immediately

    // Test seam: real Task.Delay in production; deterministic recorder in tests.
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public MusicBrainzReference(
        IHttpClientFactory httpFactory,
        ILogger<MusicBrainzReference>? logger = null)
        : this(httpFactory, logger, static (ts, ct) => Task.Delay(ts, ct))
    {
    }

    /// <summary>
    /// Test-only constructor — injects a fake delay so the rate-limit gate
    /// and the retry backoff can be exercised deterministically without
    /// actually sleeping.
    /// </summary>
    internal MusicBrainzReference(
        IHttpClientFactory httpFactory,
        ILogger<MusicBrainzReference>? logger,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _httpFactory = httpFactory;
        _logger = logger ?? NullLogger<MusicBrainzReference>.Instance;
        _delay = delay;
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

        var searchLower = workSearchTerm.ToLowerInvariant();
        var match = response.Works
            .FirstOrDefault(w => w.Title.Contains(searchLower, StringComparison.OrdinalIgnoreCase))
            ?? response.Works.First();

        var work = new WorkInfo
        {
            Title = match.Title,
            ComposerLastName = composerLastName
        };

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

    internal async Task<T?> RateLimitedGetAsync<T>(string url, CancellationToken ct = default) where T : class
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            HttpResponseMessage? response;
            try
            {
                response = await WaitGateAndSendAsync(url, ct);
            }
            catch (HttpRequestException ex)
            {
                if (attempt < MaxAttempts)
                {
                    _logger.LogWarning(ex,
                        "MusicBrainz {Url} network error (attempt {Attempt}/{Max}); will retry",
                        url, attempt, MaxAttempts);
                    await _delay(BackoffDelays[attempt - 1], ct);
                    continue;
                }
                _logger.LogWarning(ex,
                    "MusicBrainz request to {Url} failed (network error, {Max} attempts)",
                    url, MaxAttempts);
                return null;
            }
            catch (TaskCanceledException ex)
            {
                // Either an explicit cancel or HttpClient.Timeout. Don't retry
                // on cancellation; do retry on a timeout-style failure.
                if (ct.IsCancellationRequested)
                    throw;

                if (attempt < MaxAttempts)
                {
                    _logger.LogWarning(ex,
                        "MusicBrainz {Url} timed out (attempt {Attempt}/{Max}); will retry",
                        url, attempt, MaxAttempts);
                    await _delay(BackoffDelays[attempt - 1], ct);
                    continue;
                }
                _logger.LogWarning(ex,
                    "MusicBrainz request to {Url} timed out ({Max} attempts)", url, MaxAttempts);
                return null;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        var json = await response.Content.ReadAsStringAsync(ct);
                        return JsonSerializer.Deserialize<T>(json, JsonOptions);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex,
                            "MusicBrainz response from {Url} failed to parse as {Type}",
                            url, typeof(T).Name);
                        return null;
                    }
                }

                var status = (int)response.StatusCode;
                if (status == 404)
                {
                    _logger.LogInformation("MusicBrainz {Url} returned 404 (no match)", url);
                    return null;
                }

                // 429 (Too Many Requests) and 5xx are transient — retry. MB returns
                // 503 specifically when the rate-limit policy was violated; with
                // the gate in place that should never happen, but a shared IP
                // or proxy could still trigger it.
                if (status == 429 || status >= 500)
                {
                    if (attempt < MaxAttempts)
                    {
                        _logger.LogWarning(
                            "MusicBrainz {Url} returned {Status} (attempt {Attempt}/{Max}); will retry",
                            url, status, attempt, MaxAttempts);
                        await _delay(BackoffDelays[attempt - 1], ct);
                        continue;
                    }
                    _logger.LogWarning(
                        "MusicBrainz {Url} returned {Status} ({Max} attempts exhausted)",
                        url, status, MaxAttempts);
                    return null;
                }

                _logger.LogWarning("MusicBrainz {Url} returned {Status}", url, status);
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Acquires the rate-limit gate, waits the residual time so the call site
    /// observes at least <see cref="RateLimit"/> since the last request,
    /// dispatches the GET, and updates the timestamp on the way out (whether
    /// the request succeeded or threw — a thrown call still consumed network
    /// resources and we mustn't let a rapid retry bypass the gate).
    /// </summary>
    private async Task<HttpResponseMessage> WaitGateAndSendAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var elapsedMs = _clock.ElapsedMilliseconds - _lastRequestTickMs;
            var deficitMs = (long)RateLimit.TotalMilliseconds - elapsedMs;
            if (deficitMs > 0)
                await _delay(TimeSpan.FromMilliseconds(deficitMs), ct);

            try
            {
                var client = _httpFactory.CreateClient(HttpClientName);
                return await client.GetAsync(url, ct);
            }
            finally
            {
                _lastRequestTickMs = _clock.ElapsedMilliseconds;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static int? ParseYear(string? dateStr)
    {
        if (string.IsNullOrEmpty(dateStr))
            return null;
        var yearPart = dateStr.Split('-')[0];
        return int.TryParse(yearPart, out var y) ? y : null;
    }

    /// <summary>
    /// Builds the MusicBrainz-compliant User-Agent header from the assembly's
    /// real version + a contact URL. MB asks clients not to send stale-looking
    /// defaults like "CDArchive/1.0"; this lets MB contact the project if our
    /// traffic ever looks abusive.
    /// </summary>
    public static string BuildUserAgent()
    {
        var version = typeof(MusicBrainzReference).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(MusicBrainzReference).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        // Strip any "+commit-hash" suffix that source-link adds, since it's not
        // useful in a User-Agent header.
        var plus = version.IndexOf('+');
        if (plus >= 0) version = version.Substring(0, plus);
        return $"CDArchive/{version} (https://github.com/jamesliddle/CDArchive)";
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
