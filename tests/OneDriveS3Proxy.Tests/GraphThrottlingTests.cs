using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OneDriveS3Proxy.Graph;

namespace OneDriveS3Proxy.Tests;

public sealed class GraphThrottlingTests
{
    [Fact]
    public async Task Retries429_ReplayingNonSeekableUploadAndHeaders()
    {
        var options = CreateOptions(retries: 2);
        using var coordinator = new GraphRequestCoordinator(options);
        var payload = Encoding.UTF8.GetBytes("Longhorn backup block");
        var attempts = 0;
        var throttledBodies = new List<TrackedContent>();
        using var client = CreateClient(coordinator, options, async (request, cancellationToken) =>
        {
            using var received = new MemoryStream();
            await request.Content!.CopyToAsync(received, cancellationToken);
            Assert.Equal(payload, received.ToArray());
            Assert.Equal(payload.Length, request.Content.Headers.ContentLength);
            Assert.Equal("bytes 0-20/21", request.Content.Headers.ContentRange!.ToString());
            Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
            if (++attempts == 3)
            {
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            var body = new TrackedContent("throttled");
            throttledBodies.Add(body);
            return Throttled(TimeSpan.Zero, body);
        });
        using var stream = new NonSeekableStream(payload);
        using var request = new HttpRequestMessage(HttpMethod.Put, "https://example.test/upload")
        {
            Content = new StreamContent(stream)
        };
        request.Content.Headers.ContentLength = payload.Length;
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, payload.Length - 1, payload.Length);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(3, attempts);
        Assert.All(throttledBodies, body => Assert.True(body.Disposed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ExhaustedBudget_ReturnsFinal429AndItsBody(int retries)
    {
        var options = CreateOptions(retries: retries);
        using var coordinator = new GraphRequestCoordinator(options);
        var attempts = 0;
        using var client = CreateClient(coordinator, options, (_, _) =>
        {
            attempts++;
            return Task.FromResult(Throttled(TimeSpan.Zero, new StringContent("original Graph error")));
        });

        using var response = await client.GetAsync("https://example.test/item");

        Assert.Equal(retries + 1, attempts);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("original Graph error", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task OtherStatuses_AreNotRetried(HttpStatusCode status)
    {
        var options = CreateOptions();
        using var coordinator = new GraphRequestCoordinator(options);
        var attempts = 0;
        using var client = CreateClient(coordinator, options, (_, _) =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(status));
        });

        using var response = await client.GetAsync("https://example.test/item");

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RetryDelay_HonorsHeaderSecondsAndDateBeforeJsonFallback()
    {
        var options = CreateOptions();
        using var coordinator = new GraphRequestCoordinator(options);
        using var handler = new GraphThrottlingHandler(coordinator, options, NullLogger<GraphThrottlingHandler>.Instance);
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        using var secondsResponse = Throttled(TimeSpan.FromSeconds(5), new StringContent(
            """{"error":{"retryAfterSeconds":99}}"""));
        Assert.Equal(TimeSpan.FromSeconds(5),
            await handler.GetRetryDelayAsync(secondsResponse, 0, now, CancellationToken.None));

        using var dateResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        dateResponse.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(7),
            await handler.GetRetryDelayAsync(dateResponse, 0, now, CancellationToken.None));

        dateResponse.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(-7));
        Assert.Equal(TimeSpan.Zero,
            await handler.GetRetryDelayAsync(dateResponse, 0, now, CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"error":{"retryAfterSeconds":5}}""", 5)]
    [InlineData("""{"error":{"retryAfterSeconds":0}}""", 0)]
    public async Task RetryDelay_UsesGraphJsonWhenHeaderIsAbsent(string body, int seconds)
    {
        var options = CreateOptions();
        using var coordinator = new GraphRequestCoordinator(options);
        using var handler = new GraphThrottlingHandler(coordinator, options, NullLogger<GraphThrottlingHandler>.Instance);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(body) };

        Assert.Equal(TimeSpan.FromSeconds(seconds),
            await handler.GetRetryDelayAsync(response, 0, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not JSON")]
    [InlineData("""{"error":{"retryAfterSeconds":-5}}""")]
    [InlineData("""{"error":{"retryAfterSeconds":"invalid"}}""")]
    [InlineData("""{"error":{"retryAfterSeconds":1e100}}""")]
    [InlineData("""{"error":null}""")]
    [InlineData("[]")]
    public async Task RetryDelay_MissingOrInvalidHintsUseExponentialBackoffWithJitter(string body)
    {
        var options = CreateOptions();
        using var coordinator = new GraphRequestCoordinator(options);
        using var handler = new GraphThrottlingHandler(coordinator, options, NullLogger<GraphThrottlingHandler>.Instance);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(body) };

        var delay = await handler.GetRetryDelayAsync(response, 2, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.InRange(delay.TotalSeconds, 4, 5);
    }

    [Fact]
    public async Task Cooldown_IsSharedAcrossClientsAndHonorsCancellation()
    {
        var options = CreateOptions(retries: 0);
        using var coordinator = new GraphRequestCoordinator(options);
        using var first = CreateClient(coordinator, options, (_, _) =>
            Task.FromResult(Throttled(TimeSpan.FromMinutes(1), new StringContent("throttled"))));
        var secondAttempts = 0;
        using var second = CreateClient(coordinator, options, (_, _) =>
        {
            secondAttempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var response = await first.GetAsync("https://example.test/upload");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            second.GetAsync("https://example.test/monitor", cancellation.Token));

        Assert.Equal(0, secondAttempts);
    }

    [Fact]
    public async Task CancellationDuringRetryDelay_StopsFurtherAttempts()
    {
        var options = CreateOptions();
        using var coordinator = new GraphRequestCoordinator(options);
        var attempts = 0;
        using var client = CreateClient(coordinator, options, (_, _) =>
        {
            attempts++;
            return Task.FromResult(Throttled(TimeSpan.FromMinutes(1), new StringContent("throttled")));
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetAsync("https://example.test/item", cancellation.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Retry_WaitsForCooldownAndThenSucceeds()
    {
        var options = CreateOptions();
        using var coordinator = new GraphRequestCoordinator(options);
        var attempts = 0;
        using var client = CreateClient(coordinator, options, (_, _) =>
        {
            if (++attempts == 1)
            {
                return Task.FromResult(Throttled(TimeSpan.FromSeconds(1), new StringContent("throttled")));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var started = Stopwatch.GetTimestamp();

        using var response = await client.GetAsync("https://example.test/item");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts);
        Assert.True(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public async Task RequestBuffering_IsBoundedAndFailureReleasesTheSlot()
    {
        var options = CreateOptions(concurrency: 1);
        using var coordinator = new GraphRequestCoordinator(options);
        var attempts = 0;
        using var client = CreateClient(coordinator, options, (_, _) =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var request = new HttpRequestMessage(HttpMethod.Put, "https://example.test/item")
        {
            Content = new ByteArrayContent(new byte[10 * 1024 * 1024 + 1])
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        Assert.Equal(0, attempts);
        using var response = await client.GetAsync("https://example.test/item");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ConcurrencyLimit_IsSharedAndCancelledWaitersDoNotLeakSlots()
    {
        var options = CreateOptions(concurrency: 1);
        using var coordinator = new GraphRequestCoordinator(options);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = CreateClient(coordinator, options, async (_, cancellationToken) =>
        {
            firstStarted.SetResult();
            await finishFirst.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var secondAttempts = 0;
        using var second = CreateClient(coordinator, options, (_, _) =>
        {
            secondAttempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var firstRequest = first.GetAsync("https://example.test/upload");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            second.GetAsync("https://example.test/monitor", cancellation.Token));
        Assert.Equal(0, secondAttempts);

        finishFirst.SetResult();
        using var firstResponse = await firstRequest;
        using var secondResponse = await second.GetAsync("https://example.test/monitor");
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(1, secondAttempts);
    }

    [Fact]
    public async Task TransportErrors_AreNotRetriedAndReleaseTheSlot()
    {
        var options = CreateOptions(concurrency: 1);
        using var coordinator = new GraphRequestCoordinator(options);
        var attempts = 0;
        using var client = CreateClient(coordinator, options, (_, _) =>
        {
            if (++attempts == 1)
            {
                throw new HttpRequestException("Connection lost");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://example.test/item"));
        Assert.Equal(1, attempts);
        using var response = await client.GetAsync("https://example.test/item");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static IOptions<ProxyOptions> CreateOptions(int retries = 5, int concurrency = 4) =>
        Options.Create(new ProxyOptions
        {
            OneDrive = new OneDriveOptions { MaxRetries = retries, MaxConcurrency = concurrency }
        });

    private static HttpClient CreateClient(
        GraphRequestCoordinator coordinator,
        IOptions<ProxyOptions> options,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new GraphThrottlingHandler(coordinator, options, NullLogger<GraphThrottlingHandler>.Instance)
        {
            InnerHandler = new CallbackHandler(send)
        });

    private static HttpResponseMessage Throttled(TimeSpan delay, HttpContent content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = content };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }

    private sealed class TrackedContent(string body) : StringContent(body)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
