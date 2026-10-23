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
    public void ObjectPaths_PreserveOriginalKeys()
    {
        var keys = new[] { "a/b", "A/b", "a/B", "backupstore/blocks/block.blk" };

        Assert.All(keys, key => Assert.Equal($"S3/bucket/{key}", OneDrivePathMapper.ObjectPath("S3", "bucket", key)));
    }

    [Theory]
    [InlineData("backupstore/volumes/blocks/block.blk")]
    [InlineData("a # %25.txt")]
    [InlineData(".onedrive-s3-proxy-objects/plain.txt")]
    public async Task PutObject_WritesOriginalKeyAndSupportsReadListAndDelete(string key)
    {
        var handler = new ListingHandler();
        var store = CreateStore(handler, handler);
        var payload = Encoding.UTF8.GetBytes("backup block");

        await store.PutObjectAsync("bucket", key, new MemoryStream(payload), payload.Length, CancellationToken.None);

        Assert.True(handler.HasPath($"S3/bucket/{key}"));
        var put = Assert.Single(handler.Requests, request => request.Method == "PUT");
        Assert.Contains("/drive/items/", put.Uri.AbsolutePath);
        var listed = Assert.Single(await store.ListObjectsAsync("bucket", CancellationToken.None));
        Assert.Equal(key, listed.Key);
        await using (var download = await store.GetObjectAsync("bucket", key, null, CancellationToken.None))
        {
            Assert.Equal(payload, await download.Response.Content.ReadAsByteArrayAsync());
        }

        await store.DeleteObjectAsync("bucket", key, CancellationToken.None);

        Assert.False(handler.HasPath($"S3/bucket/{key}"));
        Assert.Contains(handler.Requests, request => request.Method == "DELETE" &&
            request.Uri.AbsolutePath.Contains("/drive/items/", StringComparison.Ordinal));
        Assert.Empty(await store.ListObjectsAsync("bucket", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a//b")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("a?/b")]
    [InlineData("a./b")]
    [InlineData(" a/b")]
    [InlineData("a /b")]
    [InlineData("a\\b")]
    [InlineData("CON.txt")]
    [InlineData("com1")]
    [InlineData(".lock")]
    [InlineData("desktop.ini")]
    [InlineData("a_vti_b")]
    [InlineData("~$file")]
    public void DirectObjectPaths_RejectKeysThatCannotBeStoredSafely(string key)
    {
        var exception = Assert.Throws<GraphStoreException>(() =>
            OneDrivePathMapper.ObjectPath("S3", "bucket", key));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("InvalidArgument", exception.S3Code);
    }

    [Fact]
    public void DirectObjectPaths_EnforceSegmentAndTotalPathLimits()
    {
        var key = new string('a', 200) + "/" + new string('b', 189);
        Assert.Equal(400, OneDrivePathMapper.ObjectPath("S3", "bucket", key).Length);
        Assert.Throws<GraphStoreException>(() => OneDrivePathMapper.ObjectPath("S3", "bucket", key + "b"));
        Assert.Throws<GraphStoreException>(() => OneDrivePathMapper.ObjectPath("S3", "bucket", new string('a', 256)));
    }

    [Theory]
    [InlineData("A.txt", "a.txt")]
    [InlineData("Folder/existing.txt", "folder/new.txt")]
    [InlineData("a", "a/b")]
    [InlineData("a/b", "a")]
    public async Task PutObject_RejectsCaseAndFileFolderConflictsWithoutChangingData(string existingKey, string key)
    {
        var handler = new ListingHandler();
        handler.AddDirectObject("bucket", existingKey, [1, 2, 3]);
        var store = CreateStore(handler, handler);

        var exception = await Assert.ThrowsAsync<GraphStoreException>(() =>
            store.PutObjectAsync("bucket", key, new MemoryStream([4]), 1, CancellationToken.None));

        Assert.Equal("InvalidArgument", exception.S3Code);
        Assert.True(handler.HasPath($"S3/bucket/{existingKey}"));
        Assert.DoesNotContain(handler.Requests, request => request.Method == "PUT");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PutObject_UsesConflictFailToProtectAgainstConcurrentCaseCollision(bool large)
    {
        var handler = new ListingHandler();
        handler.BeforeUpload = () => handler.AddDirectObject("bucket", "RACE.txt", [1, 2, 3]);
        var store = CreateStore(handler, handler);

        var payload = new byte[large ? 4 * 1024 * 1024 + 1 : 1];
        var exception = await Assert.ThrowsAsync<GraphStoreException>(() =>
            store.PutObjectAsync("bucket", "race.txt", new MemoryStream(payload), payload.Length, CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
        Assert.Equal("OperationAborted", exception.S3Code);
        Assert.False(handler.HasPath("S3/bucket/race.txt"));
        await using var download = await store.GetObjectAsync("bucket", "RACE.txt", null, CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3 }, await download.Response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task PutObject_OverwritesAnExactMatchByItemId()
    {
        var handler = new ListingHandler();
        handler.AddDirectObject("bucket", "nested/file.txt", [1]);
        var store = CreateStore(handler, handler);

        await store.PutObjectAsync("bucket", "nested/file.txt", new MemoryStream([2, 3]), 2, CancellationToken.None);

        Assert.Equal(2, (await store.GetObjectInfoAsync("bucket", "nested/file.txt", CancellationToken.None)).Size);
        var put = Assert.Single(handler.Requests, request => request.Method == "PUT");
        Assert.Contains("/drive/items/", put.Uri.AbsolutePath);
        Assert.DoesNotContain("conflictBehavior=fail", put.Uri.Query);
    }

    [Fact]
    public async Task OldHashedPayloads_AreOnlyOrdinaryFilesAtTheirLiteralPaths()
    {
        var handler = new ListingHandler();
        const string key = "nested/file.txt";
        var hash = OneDrivePathMapper.Hash(key);
        var oldKey = $".onedrive-s3-proxy-objects/{hash[..2]}/{hash}.data";
        handler.AddDirectObject("bucket", oldKey, [9]);
        var store = CreateStore(handler, handler);
        await store.PutObjectStateAsync("bucket", key, new ObjectState(), CancellationToken.None);

        var missing = await Assert.ThrowsAsync<GraphStoreException>(() =>
            store.GetObjectInfoAsync("bucket", key, CancellationToken.None));
        Assert.Equal("NoSuchKey", missing.S3Code);
        Assert.Equal(oldKey, Assert.Single(await store.ListObjectsAsync("bucket", CancellationToken.None)).Key);
        await store.DeleteObjectAsync("bucket", key, CancellationToken.None);
        Assert.True(handler.HasPath($"S3/bucket/{oldKey}"));
        await store.PutObjectAsync("bucket", key, new MemoryStream([1, 2]), 2, CancellationToken.None);

        Assert.Equal(2, (await store.ListObjectsAsync("bucket", CancellationToken.None)).Count);
        await using var download = await store.GetObjectAsync("bucket", oldKey, null, CancellationToken.None);
        Assert.Equal(new byte[] { 9 }, await download.Response.Content.ReadAsByteArrayAsync());
        Assert.True(handler.HasPath($"S3/bucket/{oldKey}"));
    }

    [Fact]
    public async Task DeleteObject_DoesNotDeleteChildrenOrDifferentlyCasedKeys()
    {
        var handler = new ListingHandler();
        handler.AddDirectObject("bucket", "nested/file.txt", [1, 2, 3]);
        var store = CreateStore(handler, handler);

        var item = Assert.Single(await store.ListObjectsAsync("bucket", CancellationToken.None));
        Assert.Equal(3, item.Size);
        Assert.Equal(3, (await store.GetObjectInfoAsync("bucket", "nested/file.txt", CancellationToken.None)).Size);
        await store.DeleteObjectAsync("bucket", "nested", CancellationToken.None);
        await store.DeleteObjectAsync("bucket", "NESTED/file.txt", CancellationToken.None);

        Assert.True(handler.HasPath("S3/bucket/nested/file.txt"));
        await store.DeleteObjectAsync("bucket", "nested/file.txt", CancellationToken.None);
        Assert.False(handler.HasPath("S3/bucket/nested/file.txt"));
    }

    [Fact]
    public async Task LargeUploads_UseSessionsAndCompleteAtTheDirectKeyPath()
    {
        var handler = new ListingHandler();
        var store = CreateStore(handler, handler);
        var payload = new byte[4 * 1024 * 1024 + 1];
        payload[^1] = 7;

        await store.PutObjectAsync("bucket", "large/block.blk", new MemoryStream(payload), payload.Length, CancellationToken.None);

        Assert.Equal(1, handler.UploadSessions);
        Assert.True(handler.HasPath("S3/bucket/large/block.blk"));
        await using var download = await store.GetObjectAsync("bucket", "large/block.blk", null, CancellationToken.None);
        Assert.Equal(payload, await download.Response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task MultipartCompletion_WritesTheOriginalKeyAndPreservesMetadata()
    {
        var handler = new ListingHandler();
        var store = CreateStore(handler, handler);
        var state = new ObjectState { ContentType = "application/test", Metadata = new() { ["source"] = "backup" } };
        var upload = await store.CreateMultipartUploadAsync("bucket", "nested/backup.blk", state, CancellationToken.None);
        var part = await store.PutPartAsync(
            "bucket", "nested/backup.blk", upload.UploadId, 1, new MemoryStream([1, 2, 3]), 3, CancellationToken.None);

        await store.CompleteMultipartUploadAsync("bucket", "nested/backup.blk", upload.UploadId, [part], CancellationToken.None);

        Assert.True(handler.HasPath("S3/bucket/nested/backup.blk"));
        Assert.False(handler.HasPath($"S3/.onedrive-s3-proxy-state/multipart/bucket/{upload.UploadId}"));
        var savedState = await store.GetObjectStateAsync("bucket", "nested/backup.blk", CancellationToken.None);
        Assert.Equal("application/test", savedState.ContentType);
        Assert.Equal("backup", savedState.Metadata["source"]);
    }

    [Fact]
    public async Task InvalidKeys_DoNotWriteObjectsOrMultipartManifests()
    {
        var handler = new ListingHandler();
        var store = CreateStore(handler, handler);

        var put = await Assert.ThrowsAsync<GraphStoreException>(() =>
            store.PutObjectAsync("bucket", "a//b", new MemoryStream([1]), 1, CancellationToken.None));
        var multipart = await Assert.ThrowsAsync<GraphStoreException>(() =>
            store.CreateMultipartUploadAsync("bucket", "a//b", new ObjectState(), CancellationToken.None));

        Assert.Equal("InvalidArgument", put.S3Code);
        Assert.Equal("InvalidArgument", multipart.S3Code);
        Assert.DoesNotContain(handler.Requests, request => request.Method is "POST" or "PUT");
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
        var copyRequest = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post.Method);
        Assert.Contains("conflictBehavior=fail", copyRequest.Uri.Query);
    }

    [Fact]
    public async Task CopyObject_StillOverwritesAnExactDirectDestination()
    {
        var handler = new CopyHandler(destinationExists: true);
        var store = CreateStore(handler, handler);

        await store.CopyObjectAsync("source-bucket", "source/key", "destination-bucket", "destination/key", CancellationToken.None);

        var request = Assert.Single(handler.Requests, request => request.Method == "POST");
        Assert.Contains("conflictBehavior=replace", request.Uri.Query);
    }

    [Fact]
    public async Task FailedCopy_DoesNotDeleteDestinationData()
    {
        var handler = new CopyHandler(failMonitor: true);
        var store = CreateStore(handler, handler);

        await Assert.ThrowsAsync<GraphStoreException>(() =>
            store.CopyObjectAsync("source-bucket", "source/key", "destination-bucket", "destination/key", CancellationToken.None));

        Assert.DoesNotContain(handler.Requests, request => request.Method == "DELETE");
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
    public async Task ListObjects_TraversesDirectPathsWithoutDownloadingPayloadsOrState()
    {
        var handler = new ListingHandler();
        handler.AddDirectObject("bucket", "logs/2026/a.txt", new byte[3]);
        handler.AddDirectObject("bucket", "logs/2026/b.txt", new byte[5]);
        handler.AddDirectObject("bucket", "plain.txt", new byte[7]);
        var store = CreateStore(handler, handler);

        var first = await store.ListObjectsAsync("bucket", CancellationToken.None);

        Assert.Equal(["logs/2026/a.txt", "logs/2026/b.txt", "plain.txt"], first.Select(item => item.Key));
        Assert.Equal([3L, 5L, 7L], first.Select(item => item.Size));
        Assert.Equal(0, handler.ContentDownloads);
        Assert.DoesNotContain(handler.Requests, request =>
            request.Method == "GET" &&
            request.Uri.AbsolutePath.EndsWith("/content", StringComparison.Ordinal));

        handler.AddDirectObject("bucket", "added-later.txt", [1]);
        var second = await store.ListObjectsAsync("bucket", CancellationToken.None);

        Assert.Equal(4, second.Count);
        Assert.Equal(0, handler.ContentDownloads);

        await store.DeleteObjectAsync("bucket", "plain.txt", CancellationToken.None);
        var third = await store.ListObjectsAsync("bucket", CancellationToken.None);

        Assert.DoesNotContain(third, item => item.Key == "plain.txt");
        Assert.Equal(0, handler.ContentDownloads);
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

    private sealed class CopyHandler(bool destinationExists = false, bool failMonitor = false) : RecordingHandler
    {
        public int MonitorRequests { get; private set; }
        private bool _copied = destinationExists;

        protected override Task<HttpResponseMessage> RespondAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "monitor.test")
            {
                MonitorRequests++;
                return Task.FromResult(Json(
                    HttpStatusCode.Accepted,
                    new { status = failMonitor ? "failed" : MonitorRequests == 1 ? "inProgress" : "completed" }));
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri.AbsolutePath.EndsWith("/copy", StringComparison.Ordinal))
            {
                _copied = true;
                var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                response.Headers.Location = new Uri("https://monitor.test/copy/1");
                return Task.FromResult(response);
            }

            var path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath);
            var name = path.Split('/').Last();
            if (request.Method == HttpMethod.Delete)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (path.EndsWith("/destination/key", StringComparison.Ordinal) && !_copied)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(Json(HttpStatusCode.OK,
                path.EndsWith("/source/key", StringComparison.Ordinal) || path.EndsWith("/destination/key", StringComparison.Ordinal)
                    ? File(name)
                    : Folder(name)));
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
        private readonly ConcurrentDictionary<string, (bool IsFolder, byte[]? Content, long Size)> _items = new();
        private readonly ConcurrentDictionary<string, (string Path, MemoryStream Content)> _uploads = new();
        private int _contentDownloads;

        public ListingHandler()
        {
            _items["S3"] = (true, null, 0);
            _items["S3/bucket"] = (true, null, 0);
        }

        public int ContentDownloads => _contentDownloads;
        public int UploadSessions { get; private set; }
        public Action? BeforeUpload { get; set; }

        public bool HasPath(string path) => _items.ContainsKey(path);

        public void AddDirectObject(string bucket, string key, byte[] content) =>
            AddFile($"S3/{bucket}/{key}", content, content.Length);

        protected override async Task<HttpResponseMessage> RespondAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "upload.test")
            {
                var sessionId = request.RequestUri.AbsolutePath.Trim('/');
                var session = _uploads[sessionId];
                var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                await session.Content.WriteAsync(bytes, cancellationToken);
                if (session.Content.Length < request.Content.Headers.ContentRange!.Length)
                {
                    return new HttpResponseMessage(HttpStatusCode.Accepted);
                }

                var completed = session.Content.ToArray();
                session.Content.Dispose();
                _uploads.TryRemove(sessionId, out _);
                AddFile(session.Path, completed, completed.Length);
                return Json(HttpStatusCode.Created, Describe(session.Path, _items[session.Path]));
            }

            var absolute = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var rootIndex = absolute.IndexOf(Marker, StringComparison.Ordinal);
            var byId = rootIndex < 0;
            var rest = byId
                ? absolute[(absolute.IndexOf("/drive/items/", StringComparison.Ordinal) + "/drive/items/".Length)..]
                : absolute[(rootIndex + Marker.Length)..];
            if (byId)
            {
                var childIndex = rest.IndexOf(":/", StringComparison.Ordinal);
                if (childIndex >= 0)
                {
                    rest = rest[..childIndex] + "/" + rest[(childIndex + 2)..];
                }
            }

            if (rest.EndsWith(":/children", StringComparison.Ordinal))
            {
                var parent = rest[..^":/children".Length];
                if (request.Method == HttpMethod.Post)
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    var path = $"{parent}/{body.RootElement.GetProperty("name").GetString()}";
                    if (FindPath(path) is not null)
                    {
                        return new HttpResponseMessage(HttpStatusCode.Conflict);
                    }

                    _items[path] = (true, null, 0);
                    return Json(HttpStatusCode.Created, Describe(path, _items[path]));
                }

                return Children(parent);
            }

            if (rest.EndsWith(":/createUploadSession", StringComparison.Ordinal) ||
                byId && rest.EndsWith("/createUploadSession", StringComparison.Ordinal))
            {
                var suffix = rest.EndsWith(":/createUploadSession", StringComparison.Ordinal)
                    ? ":/createUploadSession" : "/createUploadSession";
                var path = rest[..^suffix.Length];
                BeforeUpload?.Invoke();
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var behavior = body.RootElement.GetProperty("item").GetProperty("@microsoft.graph.conflictBehavior").GetString();
                if (FindPath(path) is not null && behavior == "fail")
                {
                    return new HttpResponseMessage(HttpStatusCode.Conflict);
                }

                var sessionId = Guid.NewGuid().ToString("N");
                UploadSessions++;
                _uploads[sessionId] = (FindPath(path) ?? path, new MemoryStream());
                return Json(HttpStatusCode.OK, new { uploadUrl = $"https://upload.test/{sessionId}" });
            }

            if (rest.EndsWith(":/content", StringComparison.Ordinal) ||
                byId && rest.EndsWith("/content", StringComparison.Ordinal))
            {
                var suffix = rest.EndsWith(":/content", StringComparison.Ordinal) ? ":/content" : "/content";
                var path = rest[..^suffix.Length];
                if (request.Method == HttpMethod.Put)
                {
                    BeforeUpload?.Invoke();
                    var existing = FindPath(path);
                    if (existing is not null && request.RequestUri.Query.Contains("conflictBehavior=fail", StringComparison.Ordinal))
                    {
                        return new HttpResponseMessage(HttpStatusCode.Conflict);
                    }

                    path = existing ?? path;
                    var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                    AddFile(path, bytes, bytes.Length);
                    return Json(HttpStatusCode.Created, Describe(path, _items[path]));
                }

                var actualPath = FindPath(path);
                if (actualPath is null || !_items.TryGetValue(actualPath, out var file) || file.IsFolder)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                Interlocked.Increment(ref _contentDownloads);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(file.Content ?? new byte[checked((int)file.Size)])
                };
            }

            var actual = FindPath(rest);
            if (request.Method == HttpMethod.Delete)
            {
                if (actual is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                foreach (var path in _items.Keys.Where(path => path == actual || path.StartsWith(actual + "/", StringComparison.Ordinal)))
                {
                    _items.TryRemove(path, out _);
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return actual is not null && _items.TryGetValue(actual, out var item)
                ? Json(HttpStatusCode.OK, Describe(actual, item))
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private string? FindPath(string path) =>
            _items.Keys.FirstOrDefault(candidate => candidate.Equals(path, StringComparison.OrdinalIgnoreCase));

        private void AddFile(string path, byte[]? content, long size)
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

        private static object Describe(string path, (bool IsFolder, byte[]? Content, long Size) item)
        {
            var name = path[(path.LastIndexOf('/') + 1)..];
            return item.IsFolder
                ? new
                {
                    id = path,
                    name,
                    size = 0L,
                    eTag = "\"folder\"",
                    createdDateTime = DateTimeOffset.UtcNow,
                    lastModifiedDateTime = DateTimeOffset.UtcNow,
                    folder = (object?)new { childCount = 0 }
                }
                : new
                {
                    id = path,
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
