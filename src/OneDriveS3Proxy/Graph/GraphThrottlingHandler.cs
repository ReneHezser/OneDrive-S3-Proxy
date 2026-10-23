using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OneDriveS3Proxy.Graph;

public sealed class GraphRequestCoordinator(IOptions<ProxyOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(options.Value.OneDrive.MaxConcurrency);
    private readonly object _lock = new();
    private DateTimeOffset _resumeAt;

    public async Task EnterAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                TimeSpan remaining;
                lock (_lock)
                {
                    remaining = _resumeAt - DateTimeOffset.UtcNow;
                }

                if (remaining <= TimeSpan.Zero)
                {
                    return;
                }

                await Task.Delay(remaining, cancellationToken);
            }
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    public void Defer(TimeSpan delay)
    {
        lock (_lock)
        {
            var resumeAt = DateTimeOffset.UtcNow + delay;
            if (resumeAt > _resumeAt)
            {
                _resumeAt = resumeAt;
            }
        }
    }

    public void Release() => _slots.Release();

    public void Dispose() => _slots.Dispose();
}

public sealed class GraphThrottlingHandler(
    GraphRequestCoordinator coordinator,
    IOptions<ProxyOptions> options,
    ILogger<GraphThrottlingHandler> logger) : DelegatingHandler
{
    // Graph upload chunks are at most 10 MiB; never buffer a whole S3 object.
    private const long MaximumBufferedRequestBytes = 10 * 1024 * 1024;
    private readonly int _maxRetries = options.Value.OneDrive.MaxRetries;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await coordinator.EnterAsync(cancellationToken);
            try
            {
                if (attempt == 0 && request.Content is not null)
                {
                    await request.Content.LoadIntoBufferAsync(MaximumBufferedRequestBytes, cancellationToken);
                }

                var response = await base.SendAsync(request, cancellationToken);
                if (response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    return response;
                }

                try
                {
                    var delay = await GetRetryDelayAsync(response, attempt, DateTimeOffset.UtcNow, cancellationToken);
                    coordinator.Defer(delay);
                    if (attempt >= _maxRetries)
                    {
                        logger.LogWarning(
                            "Graph throttling persisted after {Retries} retries; returning the throttled response. Shared cooldown: {Delay}.",
                            _maxRetries, delay);
                        return response;
                    }

                    logger.LogWarning(
                        "Graph throttled a request; retry {Attempt}/{MaxRetries} after a shared cooldown of {Delay}.",
                        attempt + 1, _maxRetries, delay);
                }
                catch
                {
                    response.Dispose();
                    throw;
                }

                response.Dispose();
            }
            finally
            {
                coordinator.Release();
            }
        }
    }

    internal async Task<TimeSpan> GetRetryDelayAsync(
        HttpResponseMessage response,
        int attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            return date > now ? date - now : TimeSpan.Zero;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("retryAfterSeconds", out var seconds) &&
                    seconds.ValueKind == JsonValueKind.Number &&
                    seconds.TryGetInt32(out var value) && value >= 0)
                {
                    return TimeSpan.FromSeconds(value);
                }
            }
            catch (JsonException exception)
            {
                logger.LogDebug(exception, "Graph throttle response was not JSON; using exponential backoff.");
            }
        }

        return TimeSpan.FromSeconds(Math.Pow(2, Math.Min(attempt, 5)) + Random.Shared.NextDouble());
    }
}
