using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OneDriveS3Proxy.Graph;

namespace OneDriveS3Proxy.Tests;

public sealed class GraphStoreContractTests
{
    [Fact]
    public void ObjectPaths_DoNotAliasValidS3Keys()
    {
        var keys = new[] { "a/b", "a//b", "A/b", "a/B", "a?/b", "a./b", "/a/b" };

        var paths = keys
            .Select(key => OneDrivePathMapper.ObjectDataPath("S3", "bucket", key))
            .ToList();

        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(paths, path => Assert.DoesNotContain("?", path));
    }

    [Fact]
    public async Task CopyObject_MonitorsGraphAndDoesNotDeleteDestinationFirst()
    {
        var handler = new CopyHandler();
        var store = CreateStore(handler, handler);

        var copied = await store.CopyObjectAsync(
            "source-bucket",
            "source/key",
            "destination-bucket",
            "destination/key",
            CancellationToken.None);

        Assert.Equal("destination/key", copied.Key);
        Assert.Equal(2, handler.MonitorRequests);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Delete.Method);
        var copyRequest = Assert.Single(handler.Requests.Where(request => request.Method == HttpMethod.Post.Method));
        Assert.Contains("conflictBehavior=replace", copyRequest.Uri.Query);
    }

    [Fact]
    public async Task UploadPart_UsesImmutableDataNamesAndAtomicDescriptors()
    {
        var handler = new MultipartHandler("bucket", "key", "upload");
        var store = CreateStore(handler, handler);

        var first = await store.PutPartAsync(
            "bucket",
            "key",
            "upload",
            1,
            new MemoryStream(Encoding.UTF8.GetBytes("first")),
            5,
            CancellationToken.None);
        var second = await store.PutPartAsync(
            "bucket",
            "key",
            "upload",
            1,
            new MemoryStream(Encoding.UTF8.GetBytes("second")),
            6,
            CancellationToken.None);

        Assert.NotEqual(first.StorageName, second.StorageName);
        Assert.Equal(2, handler.DataPaths.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, handler.Descriptors.Count);
        Assert.Contains(handler.Descriptors, part => part.StorageName == first.StorageName);
        Assert.Contains(handler.Descriptors, part => part.StorageName == second.StorageName);
    }

    [Fact]
    public async Task CompleteMultipartUpload_DoesNotRecreateMissingBucket()
    {
        var handler = new MissingBucketHandler();
        var store = CreateStore(handler, handler);

        var exception = await Assert.ThrowsAsync<GraphStoreException>(() =>
            store.CompleteMultipartUploadAsync(
                "missing",
                "key",
                "upload",
                [new UploadedPart(1, 5, "etag", DateTimeOffset.UtcNow, "part.bin")],
                CancellationToken.None));

        Assert.Equal("NoSuchBucket", exception.S3Code);
        Assert.DoesNotContain(handler.Requests, request => request.Method is "POST" or "PUT");
    }

    [Fact]
    public async Task ListObjects_UsesParallelFriendlyTreeListingAndCachesKeys()
    {
        var handler = new ListingHandler();
        handler.AddObject("bucket", "logs/2026/a.txt", 3);
        handler.AddObject("bucket", "logs/2026/b.txt", 5);
        handler.AddObject("bucket", "plain.txt", 7);
        handler.AddOrphanData("bucket", "orphan-without-state");
        var store = CreateStore(handler, handler);

        var first = await store.ListObjectsAsync("bucket", CancellationToken.None);

        Assert.Equal(["logs/2026/a.txt", "logs/2026/b.txt", "plain.txt"], first.Select(item => item.Key));
        Assert.Equal([3L, 5L, 7L], first.Select(item => item.Size));
        Assert.Equal(3, handler.StateDownloads);
        Assert.DoesNotContain(handler.Requests, request =>
            request.Method == "GET" &&
            request.Uri.AbsolutePath.EndsWith(".data", StringComparison.Ordinal));

        handler.AddObject("bucket", "added-later.txt", 1);
        var second = await store.ListObjectsAsync("bucket", CancellationToken.None);

        Assert.Equal(4, second.Count);
        Assert.Equal(4, handler.StateDownloads);

        handler.RemoveObject("bucket", "plain.txt");
        var third = await store.ListObjectsAsync("bucket", CancellationToken.None);

        Assert.DoesNotContain(third, item => item.Key == "plain.txt");
        Assert.Equal(4, handler.StateDownloads);
    }

    [Fact]
    public async Task PutObjectState_RetriesTransientGraphFailures()
    {
        var handler = new FlakyStateHandler(failures: 2);
        var store = CreateStore(handler, handler);

        await store.PutObjectStateAsync("bucket", "key", new ObjectState(), CancellationToken.None);

        Assert.Equal(3, handler.StatePutAttempts);
    }

    [Fact]
    public async Task Readiness_ValidatesGraphAndUsesBoundedCache()
    {
        var handler = new ReadinessHandler();
        var store = CreateStore(handler, handler);

        await store.CheckReadyAsync(CancellationToken.None);
        await store.CheckReadyAsync(CancellationToken.None);

        Assert.Equal(1, handler.DriveRequests);
    }

    private static OneDriveStore CreateStore(
        HttpMessageHandler graphHandler,
        HttpMessageHandler monitorHandler)
    {
        var graphClient = new HttpClient(graphHandler)
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
        };
        var monitorClient = new HttpClient(monitorHandler);
        return new OneDriveStore(
            graphClient,
            new TestHttpClientFactory(monitorClient),
            new TestTokenProvider(),
            Options.Create(new ProxyOptions
            {
                Entra = new EntraOptions
                {
                    TenantId = "tenant",
                    ClientId = "client",
                    ClientSecret = "secret"
                },
                OneDrive = new OneDriveOptions
                {
                    UserId = "user@example.com",
                    RootFolder = "S3"
                },
                S3 = new S3Options
                {
                    AccessKey = "access",
                    SecretKey = "secret",
                    Region = "us-east-1"
                }
            }));
    }

    private sealed class TestTokenProvider : IGraphTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult("token");
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private abstract class RecordingHandler : HttpMessageHandler
    {
        public ConcurrentBag<RequestRecord> Requests { get; } = [];

        protected sealed override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RequestRecord(request.Method.Method, request.RequestUri!));
            return await RespondAsync(request, cancellationToken);
        }

        protected abstract Task<HttpResponseMessage> RespondAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken);

        protected static HttpResponseMessage Json(HttpStatusCode status, object value)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = JsonContent.Create(value)
            };
            return response;
        }

        protected static object Folder(string id = "folder") => new
        {
            id,
            name = id,
            size = 0,
            eTag = "\"folder\"",
            createdDateTime = DateTimeOffset.UtcNow,
            lastModifiedDateTime = DateTimeOffset.UtcNow,
            folder = new { childCount = 0 }
        };

        protected static object File(string name = "object.data", long size = 10) => new
        {
            id = Guid.NewGuid().ToString("N"),
            name,
            size,
            eTag = "\"etag\"",
            createdDateTime = DateTimeOffset.UtcNow,
            lastModifiedDateTime = DateTimeOffset.UtcNow
        };
    }

    private sealed class CopyHandler : RecordingHandler
    {
        public int MonitorRequests { get; private set; }

        protected override Task<HttpResponseMessage> RespondAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "monitor.test")
            {
                MonitorRequests++;
                return Task.FromResult(Json(
                    HttpStatusCode.Accepted,
                    new { status = MonitorRequests == 1 ? "inProgress" : "completed" }));
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri.AbsolutePath.EndsWith("/copy", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                response.Headers.Location = new Uri("https://monitor.test/copy/1");
                return Task.FromResult(response);
            }

            var isDataFile = request.RequestUri.AbsolutePath.EndsWith(".data", StringComparison.Ordinal);
            return Task.FromResult(Json(
                HttpStatusCode.OK,
                isDataFile ? File() : Folder()));
        }
    }

    private sealed class MultipartHandler(
        string bucket,
        string key,
        string uploadId) : RecordingHandler
    {
        public ConcurrentBag<string> DataPaths { get; } = [];
        public ConcurrentBag<UploadedPart> Descriptors { get; } = [];

        protected override async Task<HttpResponseMessage> RespondAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get &&
                path.EndsWith("manifest.json:/content", StringComparison.Ordinal))
            {
                return Json(
                    HttpStatusCode.OK,
                    new MultipartUpload(uploadId, bucket, key, new ObjectState()));
            }

            if (request.Method == HttpMethod.Put && path.EndsWith(".bin:/content", StringComparison.Ordinal))
            {
                DataPaths.Add(path);
                var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                return Json(HttpStatusCode.OK, File(size: bytes.Length));
            }

            if (request.Method == HttpMethod.Put && path.EndsWith(".json:/content", StringComparison.Ordinal))
            {
                var descriptor = JsonSerializer.Deserialize<UploadedPart>(
                    await request.Content!.ReadAsStringAsync(cancellationToken),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Descriptors.Add(descriptor);
                return Json(HttpStatusCode.OK, File(size: request.Content.Headers.ContentLength ?? 0));
            }

            return Json(HttpStatusCode.OK, Folder());
        }
    }

    private sealed class MissingBucketHandler : RecordingHandler
    {
        protected override Task<HttpResponseMessage> RespondAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get &&
                path.EndsWith("/drive/root:/S3/missing", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(Json(HttpStatusCode.OK, Folder()));
        }
    }

    private sealed class ReadinessHandler : RecordingHandler
    {
        public int DriveRequests { get; private set; }

        protected override Task<HttpResponseMessage> RespondAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/drive", StringComparison.Ordinal))
            {
                DriveRequests++;
                return Task.FromResult(Json(HttpStatusCode.OK, new { id = "drive" }));
            }

            return Task.FromResult(Json(HttpStatusCode.OK, Folder("S3")));
        }
    }

    private sealed class FlakyStateHandler(int failures) : RecordingHandler
    {
        public int StatePutAttempts { get; private set; }

        protected override async Task<HttpResponseMessage> RespondAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put &&
                request.RequestUri!.AbsolutePath.EndsWith(".json:/content", StringComparison.Ordinal))
            {
                await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                StatePutAttempts++;
                return StatePutAttempts <= failures
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : Json(HttpStatusCode.OK, File("state.json", 2));
            }

            return Json(HttpStatusCode.OK, Folder());
        }
    }

    private sealed class ListingHandler : RecordingHandler
    {
        private const string Marker = "/drive/root:/";
        private readonly ConcurrentDictionary<string, (bool IsFolder, string? Content, long Size)> _items = new();
        private int _stateDownloads;

        public ListingHandler()
        {
            _items["S3"] = (true, null, 0);
            _items["S3/bucket"] = (true, null, 0);
        }

        public int StateDownloads => _stateDownloads;

        public void AddObject(string bucket, string key, long size)
        {
            var hash = OneDrivePathMapper.Hash(key);
            AddFile($"S3/{bucket}/{OneDrivePathMapper.DataFolder}/{hash[..2]}/{hash}.data", null, size);
            AddFile(
                $"S3/.onedrive-s3-proxy-state/objects/{bucket}/{hash[..2]}/{hash}.json",
                JsonSerializer.Serialize(new { key }),
                0);
        }

        public void AddOrphanData(string bucket, string key)
        {
            var hash = OneDrivePathMapper.Hash(key);
            AddFile($"S3/{bucket}/{OneDrivePathMapper.DataFolder}/{hash[..2]}/{hash}.data", null, 1);
        }

        public void RemoveObject(string bucket, string key)
        {
            var hash = OneDrivePathMapper.Hash(key);
            _items.TryRemove($"S3/{bucket}/{OneDrivePathMapper.DataFolder}/{hash[..2]}/{hash}.data", out _);
            _items.TryRemove($"S3/.onedrive-s3-proxy-state/objects/{bucket}/{hash[..2]}/{hash}.json", out _);
        }

        protected override Task<HttpResponseMessage> RespondAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var absolute = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var rest = absolute[(absolute.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..];
            if (rest.EndsWith(":/children", StringComparison.Ordinal))
            {
                return Task.FromResult(Children(rest[..^":/children".Length]));
            }

            if (rest.EndsWith(":/content", StringComparison.Ordinal))
            {
                var path = rest[..^":/content".Length];
                if (!_items.TryGetValue(path, out var file) || file.IsFolder)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                Interlocked.Increment(ref _stateDownloads);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(file.Content ?? string.Empty, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(_items.TryGetValue(rest, out var item)
                ? Json(HttpStatusCode.OK, Describe(rest, item))
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private void AddFile(string path, string? content, long size)
        {
            var segments = path.Split('/');
            for (var length = 1; length < segments.Length; length++)
            {
                _items.TryAdd(string.Join('/', segments[..length]), (true, null, 0));
            }

            _items[path] = (false, content, size);
        }

        private HttpResponseMessage Children(string folder)
        {
            if (!_items.TryGetValue(folder, out var parent) || !parent.IsFolder)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var prefix = $"{folder}/";
            var children = _items
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                               !pair.Key[prefix.Length..].Contains('/'))
                .Select(pair => Describe(pair.Key, pair.Value))
                .ToList();
            return Json(HttpStatusCode.OK, new { value = children });
        }

        private static object Describe(string path, (bool IsFolder, string? Content, long Size) item)
        {
            var name = path[(path.LastIndexOf('/') + 1)..];
            return item.IsFolder
                ? new
                {
                    id = name,
                    name,
                    size = 0L,
                    eTag = "\"folder\"",
                    createdDateTime = DateTimeOffset.UtcNow,
                    lastModifiedDateTime = DateTimeOffset.UtcNow,
                    folder = (object?)new { childCount = 0 }
                }
                : new
                {
                    id = name,
                    name,
                    size = item.Size,
                    eTag = "\"etag\"",
                    createdDateTime = DateTimeOffset.UtcNow,
                    lastModifiedDateTime = DateTimeOffset.UtcNow,
                    folder = (object?)null
                };
        }
    }

    private sealed record RequestRecord(string Method, Uri Uri);
}
