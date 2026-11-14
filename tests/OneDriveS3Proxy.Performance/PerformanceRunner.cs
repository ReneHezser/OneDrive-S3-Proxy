using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace OneDriveS3Proxy.Performance;

internal static class PerformanceRunner
{
    public static async Task<IReadOnlyList<PhaseResult>> RunAsync(
        PerformanceOptions options,
        TextWriter output,
        CancellationToken cancellationToken,
        Action<SimulatedOneDriveStore>? configureStore = null)
    {
        var digests = new Dictionary<long, PayloadDigest>();
        await output.WriteLineAsync("Preparing streaming payload hashes (outside measured time)...");
        foreach (var size in options.SizesMiB.Append(1).Distinct())
        {
            var length = size * 1024L * 1024;
            digests.Add(length, await PayloadDigest.CreateAsync(length, cancellationToken));
        }

        await using var factory = new PerformanceProxyFactory(digests);
        configureStore?.Invoke(factory.Store);
        factory.UseKestrel(0);
        // The factory's redirect handler eagerly buffers request bodies for replay.
        factory.ClientOptions.AllowAutoRedirect = false;
        factory.ClientOptions.HandleCookies = false;
        using var client = factory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using (var ready = await client.GetAsync("/health/ready", cancellationToken))
        {
            ready.EnsureSuccessStatusCode();
        }
        await output.WriteLineAsync($"Local Kestrel: {client.BaseAddress} (simulated backend; no disk or Graph I/O)");
        await output.WriteLineAsync("Warming up upload/download paths and connections...");
        var warmup = await RunCaseAsync(1, options.Concurrency.Max(), Math.Min(options.Objects, options.Concurrency.Max()), "warmup");
        if (warmup.Any(phase => phase.Failed != 0))
        {
            throw new InvalidOperationException($"Warmup failed: {string.Join("; ", warmup.SelectMany(phase => phase.Errors))}");
        }

        await output.WriteLineAsync(" MiB   C  Op    OK Fail Skip     MiB/s  Files/s   p50 ms   p95 ms   p99 ms  Peak(C/S)");
        var results = new List<PhaseResult>();
        foreach (var size in options.SizesMiB)
        {
            foreach (var concurrency in options.Concurrency)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var result in await RunCaseAsync(size, concurrency, options.Objects, $"{size}-{concurrency}"))
                {
                    results.Add(result);
                    await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"{size,4} {concurrency,3} {result.Operation,3} {result.Succeeded,5} {result.Failed,4} {result.Skipped,4} {result.MiBPerSecond,9:F1} {result.FilesPerSecond,8:F2} {result.P50Milliseconds,8:F1} {result.P95Milliseconds,8:F1} {result.P99Milliseconds,8:F1} {result.PeakClientInFlight,4}/{result.PeakServerInFlight}"));
                    foreach (var error in result.Errors)
                    {
                        await output.WriteLineAsync($"  ERROR: {error}");
                    }
                }
            }
        }
        return results;

        async Task<PhaseResult[]> RunCaseAsync(int size, int concurrency, int count, string group)
        {
            var indices = Enumerable.Range(0, count).ToArray();
            var upload = await RunPhaseAsync(HttpMethod.Put, indices);
            var download = await RunPhaseAsync(HttpMethod.Get, upload.SuccessfulIndices);
            // Cleanup is intentionally outside measured phases and never touches an external store.
            var store = factory.Services.GetRequiredService<OneDriveS3Proxy.Graph.IOneDriveStore>();
            foreach (var index in indices)
            {
                await store.DeleteObjectAsync(SimulatedOneDriveStore.Bucket, $"{group}/{index}", cancellationToken);
            }
            return [upload.Result, download.Result];

            async Task<(PhaseResult Result, int[] SuccessfulIndices)> RunPhaseAsync(HttpMethod method, int[] work)
            {
                var successes = new ConcurrentBag<(int Index, double Milliseconds)>();
                var failures = new ConcurrentBag<string>();
                var inFlight = new ConcurrencyCounter();
                var length = size * 1024L * 1024;
                var digest = digests[length];
                using var process = Process.GetCurrentProcess();
                var cpuStart = process.TotalProcessorTime;
                var clock = Stopwatch.StartNew();
                await Parallel.ForEachAsync(work, new ParallelOptions
                {
                    MaxDegreeOfParallelism = concurrency,
                    CancellationToken = cancellationToken
                }, async (index, token) =>
                {
                    var started = Stopwatch.GetTimestamp();
                    inFlight.Enter();
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                        var path = $"/{SimulatedOneDriveStore.Bucket}/{group}/{index}";
                        using var request = new HttpRequestMessage(method, new Uri(client.BaseAddress!, path))
                        {
                            Version = HttpVersion.Version11,
                            VersionPolicy = HttpVersionPolicy.RequestVersionExact
                        };
                        if (method == HttpMethod.Put)
                        {
                            request.Content = new StreamContent(new PatternStream(length), 64 * 1024);
                            request.Content.Headers.ContentLength = length;
                        }
                        Sign(request, method == HttpMethod.Put ? digest.Sha256 : Convert.ToHexStringLower(SHA256.HashData([])));
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                        response.EnsureSuccessStatusCode();
                        if (response.Headers.ETag?.Tag != $"\"{digest.Md5}\"")
                        {
                            throw new InvalidDataException("Response ETag does not match the payload MD5.");
                        }
                        if (method == HttpMethod.Get)
                        {
                            await VerifyDownloadAsync(response.Content, length, digest.Sha256, timeout.Token);
                        }
                        else
                        {
                            await response.Content.CopyToAsync(Stream.Null, timeout.Token);
                        }
                        successes.Add((index, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        failures.Add($"Object {index}: request exceeded {options.TimeoutSeconds} seconds.");
                    }
                    catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
                    {
                        failures.Add($"Object {index}: {exception.Message}");
                    }
                    finally
                    {
                        inFlight.Exit();
                    }
                });
                clock.Stop();
                var latencies = successes.Select(sample => sample.Milliseconds).Order().ToArray();
                var key = PerformanceProxyFactory.CounterKey(method.Method, $"/{SimulatedOneDriveStore.Bucket}/{group}/0");
                var serverPeak = factory.ServerCounters.TryGetValue(key, out var counter) ? counter.Peak : 0;
                var result = new PhaseResult(
                    size, concurrency, method.Method, work.Length, successes.Count, failures.Count,
                    count - work.Length, successes.Count * length, clock.Elapsed.TotalSeconds,
                    successes.Count * (double)size / clock.Elapsed.TotalSeconds,
                    successes.Count / clock.Elapsed.TotalSeconds,
                    Percentile(latencies, 0.50), Percentile(latencies, 0.95), Percentile(latencies, 0.99),
                    inFlight.Peak, serverPeak,
                    (process.TotalProcessorTime - cpuStart).TotalSeconds / clock.Elapsed.TotalSeconds / Environment.ProcessorCount * 100,
                    failures.Order(StringComparer.Ordinal).ToArray());
                return (result, successes.Select(sample => sample.Index).Order().ToArray());
            }
        }
    }

    internal static double? Percentile(double[] sorted, double percentile) =>
        sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];

    private static async Task VerifyDownloadAsync(HttpContent content, long length, string sha256, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength != length)
        {
            throw new InvalidDataException("Download Content-Length does not match the uploaded object.");
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long received = 0;
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, 64 * 1024), cancellationToken)) != 0)
            {
                hash.AppendData(buffer, 0, read);
                received += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (received != length || Convert.ToHexStringLower(hash.GetHashAndReset()) != sha256)
        {
            throw new InvalidDataException("Downloaded payload length or SHA-256 does not match the uploaded object.");
        }
    }

    private static void Sign(HttpRequestMessage request, string payloadHash)
    {
        var uri = request.RequestUri!;
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = timestamp[..8];
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        request.Headers.Host = uri.Authority;
        request.Headers.Add("x-amz-date", timestamp);
        request.Headers.Add("x-amz-content-sha256", payloadHash);
        var canonical = string.Join('\n', request.Method.Method, uri.AbsolutePath, "",
            $"host:{uri.Authority}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{timestamp}\n",
            signedHeaders, payloadHash);
        var scope = $"{date}/{PerformanceProxyFactory.Region}/s3/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{timestamp}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var dateKey = Hmac(Encoding.UTF8.GetBytes($"AWS4{PerformanceProxyFactory.SecretKey}"), date);
        var regionKey = Hmac(dateKey, PerformanceProxyFactory.Region);
        var serviceKey = Hmac(regionKey, "s3");
        var signature = Convert.ToHexStringLower(Hmac(Hmac(serviceKey, "aws4_request"), toSign));
        request.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={PerformanceProxyFactory.AccessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
}

internal sealed record PhaseResult(
    int SizeMiB, int Concurrency, string Operation, int Attempted, int Succeeded, int Failed, int Skipped,
    long SuccessfulBytes, double Seconds, double MiBPerSecond, double FilesPerSecond,
    double? P50Milliseconds, double? P95Milliseconds, double? P99Milliseconds,
    int PeakClientInFlight, int PeakServerInFlight, double CombinedProcessCpuPercent, string[] Errors);
