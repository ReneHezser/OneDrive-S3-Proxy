using System.Security.Cryptography;
using OneDriveS3Proxy.Graph;
using OneDriveS3Proxy.Performance;

namespace OneDriveS3Proxy.Tests;

public sealed class PerformanceRunnerTests
{
    [Fact]
    public void DefaultsMatchRequestedWorkload()
    {
        var options = PerformanceOptions.Parse([]);
        Assert.Equal([1, 16, 1024], options.SizesMiB);
        Assert.Equal([1, 2, 4, 8, 16], options.Concurrency);
        Assert.Equal(32, options.Objects);
    }

    [Theory]
    [InlineData("--objects", "0")]
    [InlineData("--sizes-mib", "238001")]
    [InlineData("--sizes-mib", "1,")]
    [InlineData("--concurrency", "-1")]
    [InlineData("--timeout-seconds", "invalid")]
    [InlineData("--unknown", "1")]
    public void InvalidOptionsFailExplicitly(string name, string value) =>
        Assert.Throws<ArgumentException>(() => PerformanceOptions.Parse([name, value]));

    [Fact]
    public void MissingOptionValueFailsExplicitly() =>
        Assert.Throws<ArgumentException>(() => PerformanceOptions.Parse(["--objects"]));

    [Fact]
    public async Task PatternStreamsAreIndependentAndHandleUnalignedReads()
    {
        const int length = 131089;
        using var first = new PatternStream(length);
        using var second = new PatternStream(length);
        var expected = new byte[length];
        await first.ReadExactlyAsync(expected);
        var actual = new byte[length];
        var offset = 0;
        while (offset != length)
        {
            offset += await second.ReadAsync(actual.AsMemory(offset, Math.Min(7919, length - offset)));
        }
        Assert.Equal(expected, actual);
        Assert.Equal(0, await second.ReadAsync(new byte[1]));
        Assert.Equal(length, second.Position);
        var digest = await PayloadDigest.CreateAsync(length, TestContextToken);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(actual)), digest.Sha256);
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(actual)), digest.Md5);
        using var large = new PatternStream(3L * 1024 * 1024 * 1024);
        Assert.Equal(3L * 1024 * 1024 * 1024, large.Length);
    }

    [Fact]
    public void PercentilesUseNearestRankAndDoNotInventSuccessfulLatencies()
    {
        Assert.Null(PerformanceRunner.Percentile([], 0.95));
        Assert.Equal(1d, PerformanceRunner.Percentile([1], 0.99));
        Assert.Equal(16d, PerformanceRunner.Percentile(Enumerable.Range(1, 32).Select(value => (double)value).ToArray(), 0.5));
        Assert.Equal(31d, PerformanceRunner.Percentile(Enumerable.Range(1, 32).Select(value => (double)value).ToArray(), 0.95));
    }

    [Fact]
    public async Task SimulatedStoreConsumesButDoesNotOwnUploadAndRecreatesVerifiedDownload()
    {
        const long length = 131089;
        var digest = await PayloadDigest.CreateAsync(length, TestContextToken);
        var store = new SimulatedOneDriveStore(new Dictionary<long, PayloadDigest> { [length] = digest });
        using var body = new PatternStream(length);
        var info = await store.PutObjectAsync(SimulatedOneDriveStore.Bucket, "test", body, length, TestContextToken);
        Assert.Equal(length, body.Position);
        Assert.True(body.CanRead);
        Assert.Equal(digest.Md5, info.ETag);
        await using (var download = await store.GetObjectAsync(SimulatedOneDriveStore.Bucket, "test", null, TestContextToken))
        {
            await using var content = await download.Response.Content.ReadAsStreamAsync();
            Assert.Equal(digest.Sha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(content)));
        }
        await store.DeleteObjectAsync(SimulatedOneDriveStore.Bucket, "test", TestContextToken);
        await Assert.ThrowsAsync<GraphStoreException>(() =>
            store.GetObjectInfoAsync(SimulatedOneDriveStore.Bucket, "test", TestContextToken));
    }

    [Fact]
    public async Task CorruptUploadIsNotAcknowledgedOrStored()
    {
        const int length = 1024;
        var digest = await PayloadDigest.CreateAsync(length, TestContextToken);
        var store = new SimulatedOneDriveStore(new Dictionary<long, PayloadDigest> { [length] = digest });
        using var body = new MemoryStream(new byte[length]);
        await Assert.ThrowsAsync<GraphStoreException>(() =>
            store.PutObjectAsync(SimulatedOneDriveStore.Bucket, "test", body, length, TestContextToken));
        await Assert.ThrowsAsync<GraphStoreException>(() =>
            store.GetObjectInfoAsync(SimulatedOneDriveStore.Bucket, "test", TestContextToken));
    }

    [Fact]
    public async Task LocalRunnerExercisesRealHttpWithVerifiedBodiesAndBoundedConcurrency()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var options = PerformanceOptions.Parse(["--sizes-mib", "1", "--concurrency", "1,4", "--objects", "8"]);
        var results = await PerformanceRunner.RunAsync(options, TextWriter.Null, timeout.Token);
        Assert.Equal(4, results.Count);
        foreach (var result in results)
        {
            Assert.Equal(8, result.Attempted);
            Assert.Equal(8, result.Succeeded);
            Assert.Equal(0, result.Failed);
            Assert.Equal(0, result.Skipped);
            Assert.Equal(8L * 1024 * 1024, result.SuccessfulBytes);
            Assert.InRange(result.PeakClientInFlight, 1, result.Concurrency);
            Assert.True(result.PeakServerInFlight > 0);
            Assert.True(result.MiBPerSecond > 0);
            Assert.True(result.P95Milliseconds > 0);
            Assert.Empty(result.Errors);
        }
        Assert.All(results.Where(result => result.Concurrency == 4), result => Assert.InRange(result.PeakClientInFlight, 2, 4));
    }

    [Fact]
    public async Task IntegrityFailuresAreReportedAsFailedRequestsInsteadOfCrashingTheRun()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var options = PerformanceOptions.Parse(["--sizes-mib", "1", "--concurrency", "2", "--objects", "2"]);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PerformanceRunner.RunAsync(options, TextWriter.Null, timeout.Token, store => store.CorruptDownloadETags = true));
        Assert.Contains("ETag", exception.Message);
    }

    private static CancellationToken TestContextToken => CancellationToken.None;
}
