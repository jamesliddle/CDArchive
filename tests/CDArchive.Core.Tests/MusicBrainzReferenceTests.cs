using System.Net;
using System.Text;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Tests for <see cref="MusicBrainzReference"/>'s rate-limit gate (C10),
/// HTTP-factory plumbing (M16), and retry policy (M18). The delay is mocked
/// out via the internal constructor so we can assert on the exact requested
/// durations without actually sleeping.
/// </summary>
public class MusicBrainzReferenceTests
{
    // ------------------------------------------------------------------
    // Test fixtures
    // ------------------------------------------------------------------

    /// <summary>
    /// Scripted <see cref="HttpMessageHandler"/>: hands out responses from a
    /// queue, records each request URL. If the queue empties, returns 200 OK
    /// with an empty body.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        public List<string> Requests { get; } = new();

        public void Enqueue(HttpStatusCode status, string body = "{}")
        {
            _responses.Enqueue(() => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        public void EnqueueThrow(Exception ex) =>
            _responses.Enqueue(() => throw ex);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            cancellationToken.ThrowIfCancellationRequested();
            var make = _responses.Count > 0
                ? _responses.Dequeue()
                : (() => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });
            return Task.FromResult(make());
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;
        // Don't dispose the handler on client dispose; the factory owns it.
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    /// <summary>
    /// Records every requested delay without actually waiting.
    /// </summary>
    private sealed class FakeDelayer
    {
        public List<TimeSpan> Recorded { get; } = new();
        public Task Delay(TimeSpan ts, CancellationToken ct)
        {
            Recorded.Add(ts);
            return Task.CompletedTask;
        }
    }

    private sealed class DummyResponse { public string Status { get; set; } = ""; }

    private static MusicBrainzReference Build(ScriptedHandler handler, FakeDelayer delayer) =>
        new(new SingleClientFactory(handler), logger: null, delayer.Delay);

    // ------------------------------------------------------------------
    // C10 — thread-safe rate-limit gate
    // ------------------------------------------------------------------

    [Fact]
    public async Task FirstCall_FiresImmediately_NoDelay()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK);
        var delayer = new FakeDelayer();
        var mb = Build(handler, delayer);

        await mb.RateLimitedGetAsync<DummyResponse>("https://test/first");

        Assert.Single(handler.Requests);
        // No delay should have been requested for the very first call — the
        // gate's stored timestamp starts deep in the past.
        Assert.Empty(delayer.Recorded);
    }

    [Fact]
    public async Task ConcurrentCallers_SerializeThroughGate_ObserveMinimumGap()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK);
        handler.Enqueue(HttpStatusCode.OK);
        handler.Enqueue(HttpStatusCode.OK);
        var delayer = new FakeDelayer();
        var mb = Build(handler, delayer);

        // Three concurrent callers. The first wins the gate and fires; the
        // second and third queue and each pay their ~1100ms gate wait.
        var t1 = mb.RateLimitedGetAsync<DummyResponse>("https://test/a");
        var t2 = mb.RateLimitedGetAsync<DummyResponse>("https://test/b");
        var t3 = mb.RateLimitedGetAsync<DummyResponse>("https://test/c");
        await Task.WhenAll(t1, t2, t3);

        Assert.Equal(3, handler.Requests.Count);
        // First call: no delay; subsequent two: each waits ~RateLimit.
        Assert.Equal(2, delayer.Recorded.Count);
        // Each requested delay should be close to 1100ms — well, at most
        // RateLimit (1100ms). Both should be > 0 and ≤ 1100.
        foreach (var d in delayer.Recorded)
        {
            Assert.InRange(d.TotalMilliseconds, 1, 1100);
        }
    }

    // ------------------------------------------------------------------
    // M18 — retry on transient failure
    // ------------------------------------------------------------------

    [Fact]
    public async Task Retries_On503_ThenSucceeds()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.OK, "{ \"status\": \"ok\" }");
        var delayer = new FakeDelayer();
        var mb = Build(handler, delayer);

        var result = await mb.RateLimitedGetAsync<DummyResponse>("https://test/r");

        Assert.NotNull(result);
        Assert.Equal("ok", result!.Status);
        Assert.Equal(2, handler.Requests.Count);
        // Two delays expected: the 500ms retry backoff between attempts 1 and 2,
        // and the rate-limit gate wait before attempt 2's actual GET.
        Assert.Equal(2, delayer.Recorded.Count);
        Assert.Contains(delayer.Recorded, d => d.TotalMilliseconds == 500);
    }

    [Fact]
    public async Task PersistentTransientFailure_ReturnsNull_AfterMaxAttempts()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        var delayer = new FakeDelayer();
        var mb = Build(handler, delayer);

        var result = await mb.RateLimitedGetAsync<DummyResponse>("https://test/p");

        Assert.Null(result);
        Assert.Equal(3, handler.Requests.Count); // MaxAttempts = 3
    }

    [Fact]
    public async Task Retries_OnHttpRequestException_ThenSucceeds()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueThrow(new HttpRequestException("dns failure"));
        handler.Enqueue(HttpStatusCode.OK);
        var delayer = new FakeDelayer();
        var mb = Build(handler, delayer);

        var result = await mb.RateLimitedGetAsync<DummyResponse>("https://test/h");

        Assert.NotNull(result);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Retries_On429_TreatsAsTransient()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue((HttpStatusCode)429); // Too Many Requests
        handler.Enqueue(HttpStatusCode.OK);
        var delayer = new FakeDelayer();
        var mb = Build(handler, delayer);

        var result = await mb.RateLimitedGetAsync<DummyResponse>("https://test/2");

        Assert.NotNull(result);
        Assert.Equal(2, handler.Requests.Count);
    }

    // ------------------------------------------------------------------
    // Non-retry paths
    // ------------------------------------------------------------------

    [Fact]
    public async Task NotFound_ReturnsNull_WithoutRetry()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.NotFound);
        var delayer = new FakeDelayer();
        var mb = Build(handler, delayer);

        var result = await mb.RateLimitedGetAsync<DummyResponse>("https://test/404");

        Assert.Null(result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task BadRequest_4xx_ReturnsNull_WithoutRetry()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.BadRequest);
        var delayer = new FakeDelayer();
        var mb = Build(handler, delayer);

        var result = await mb.RateLimitedGetAsync<DummyResponse>("https://test/400");

        Assert.Null(result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MalformedJson_ReturnsNull_WithoutRetry()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, "not actually json");
        var delayer = new FakeDelayer();
        var mb = Build(handler, delayer);

        var result = await mb.RateLimitedGetAsync<DummyResponse>("https://test/junk");

        Assert.Null(result);
        Assert.Single(handler.Requests);
    }

    // ------------------------------------------------------------------
    // Cancellation
    // ------------------------------------------------------------------

    [Fact]
    public async Task Cancellation_PropagatesAsOperationCanceled()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK);
        var delayer = new FakeDelayer();
        var mb = Build(handler, delayer);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => mb.RateLimitedGetAsync<DummyResponse>("https://test/c", cts.Token));
    }

    // ------------------------------------------------------------------
    // M17 — User-Agent build
    // ------------------------------------------------------------------

    [Fact]
    public void BuildUserAgent_IncludesVersionAndContactUrl()
    {
        var ua = MusicBrainzReference.BuildUserAgent();
        Assert.StartsWith("CDArchive/", ua);
        Assert.Contains("github.com/jamesliddle/CDArchive", ua);
        // The hardcoded "1.0" the previous build used is gone — the actual
        // assembly version should be substituted (currently "1.0.0" for an
        // unversioned assembly, but never the literal "1.0").
        Assert.DoesNotContain("CDArchive/1.0 ", ua);
    }
}
