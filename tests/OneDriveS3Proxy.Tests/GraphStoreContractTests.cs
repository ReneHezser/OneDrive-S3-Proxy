using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OneDriveS3Proxy.Graph;

namespace OneDriveS3Proxy.Tests;

public sealed class GraphStoreContractTests : IDisposable
{
    private readonly List<HttpClient> _clients = [];

    public static TheoryData<int, bool, bool> UploadStreamCases
    {
        get
        {
            var cases = new TheoryData<int, bool, bool>();
            foreach (var size in new[] { 1, 4 * 1024 * 1024, 4 * 1024 * 1024 + 1 })
            {
                foreach (var seekable in new[] { false, true })
                {
                    foreach (var multipart in new[] { false, true })
                    {
                        cases.Add(size, seekable, multipart);
                    }
                }
            }

            return cases;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_DisposesClientsAndHandlersOnce(bool sharedHandler)
    {
        var graphHandler = new DisposalTrackingHandler();
        var monitorHandler = sharedHandler ? graphHandler : new DisposalTrackingHandler();
        CreateStore(graphHandler, monitorHandler);
        var clients = _clients.ToArray();

        Dispose();
        Dispose();

        Assert.Equal(2, clients.Length);
        Assert.Empty(_clients);
        Assert.Equal(1, graphHandler.DisposeCount);
        Assert.Equal(1, monitorHandler.DisposeCount);
        foreach (var client in clients)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendAsync(request));
        }
    }

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
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(4 * 1024 * 1024 + 1, false)]
    [InlineData(10 * 1024 * 1024 + 37, false)]
    [InlineData(3, true)]
    [InlineData(4 * 1024 * 1024 + 1, true)]
    public async Task S3Uploads_PersistMd5ETagsAcrossReadsListingsAndRestart(int size, bool sigV4)
    {
        var handler = new ListingHandler();
        var store = CreateStore(handler, handler);
        await using var factory = new LonghornProxyFactory();
        await using var proxy = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOneDriveStore>();
            services.AddSingleton<IOneDriveStore>(store);
        }));
        using var client = proxy.CreateClient();
        var payload = new byte[size];
        if (size > 0)
        {
            payload[^1] = 7;
        }

        var etag = Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant();
        using var put = await SendS3Async(client, HttpMethod.Put, "/bucket/backup.bin", payload, sigV4: sigV4);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal($"\"{etag}\"", put.Headers.ETag?.Tag);

        foreach (var method in new[] { HttpMethod.Head, HttpMethod.Get })
        {
            using var read = await SendS3Async(client, method, "/bucket/backup.bin", sigV4: sigV4);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(put.Headers.ETag, read.Headers.ETag);
            if (method == HttpMethod.Get)
            {
                Assert.Equal(payload, await read.Content.ReadAsByteArrayAsync());
            }
        }

        foreach (var query in new[] { "", "?list-type=2" })
        {
            using var list = await SendS3Async(client, HttpMethod.Get, "/bucket" + query, sigV4: sigV4);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Equal($"\"{etag}\"", XDocument.Parse(await list.Content.ReadAsStringAsync())
                .Descendants().Single(element => element.Name.LocalName == "ETag").Value);
        }

        var restarted = CreateStore(handler, handler);
        Assert.Equal(etag, (await restarted.GetObjectInfoAsync("bucket", "backup.bin", CancellationToken.None)).ETag);
        Assert.Equal(etag, Assert.Single(await restarted.ListObjectsAsync("bucket", CancellationToken.None)).ETag);
        var state = await restarted.GetObjectStateAsync("bucket", "backup.bin", CancellationToken.None);
        Assert.Equal(etag, state.ETag);
        Assert.NotEqual(etag, state.StorageETag);
        Assert.Equal("application/test", state.ContentType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S3CopiesAndMetadataChanges_PreserveETags(bool replaceMetadata)
    {
        var handler = new ListingHandler();
        var store = CreateStore(handler, handler);
        await using var factory = new LonghornProxyFactory();
        await using var proxy = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOneDriveStore>();
            services.AddSingleton<IOneDriveStore>(store);
        }));
        using var client = proxy.CreateClient();
        var payload = "backup content"u8.ToArray();
        var etag = $"\"{Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant()}\"";
        using var put = await SendS3Async(client, HttpMethod.Put, "/bucket/source.bin", payload);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var tags = await SendS3Async(client, HttpMethod.Put, "/bucket/source.bin?tagging",
            "<Tagging><TagSet><Tag><Key>client</Key><Value>qnap</Value></Tag></TagSet></Tagging>"u8.ToArray());
        Assert.Equal(HttpStatusCode.OK, tags.StatusCode);

        using var copy = await SendS3Async(client, HttpMethod.Put, "/bucket/copied.bin", headers: new()
        {
            ["x-amz-copy-source"] = "/bucket/source.bin",
            ["x-amz-metadata-directive"] = replaceMetadata ? "REPLACE" : "COPY"
        });
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);
        Assert.Equal(etag, XDocument.Parse(await copy.Content.ReadAsStringAsync())
            .Descendants().Single(element => element.Name.LocalName == "ETag").Value);
        using var copiedHead = await SendS3Async(client, HttpMethod.Head, "/bucket/copied.bin");
        Assert.Equal(etag, copiedHead.Headers.ETag?.Tag);
        var copiedState = await store.GetObjectStateAsync("bucket", "copied.bin", CancellationToken.None);
        Assert.Equal(replaceMetadata ? "application/octet-stream" : "application/test", copiedState.ContentType);
        Assert.Equal("qnap", copiedState.Tags["client"]);

        using var selfCopy = await SendS3Async(client, HttpMethod.Put, "/bucket/copied.bin", headers: new()
        {
            ["x-amz-copy-source"] = "/bucket/copied.bin",
            ["x-amz-metadata-directive"] = "REPLACE",
            ["x-amz-meta-owner"] = "archive"
        });
        Assert.Equal(HttpStatusCode.OK, selfCopy.StatusCode);
        using var deleteTags = await SendS3Async(client, HttpMethod.Delete, "/bucket/copied.bin?tagging");
        Assert.Equal(HttpStatusCode.NoContent, deleteTags.StatusCode);
        using var head = await SendS3Async(client, HttpMethod.Head, "/bucket/copied.bin");
        Assert.Equal(etag, head.Headers.ETag?.Tag);
        Assert.Equal("archive", Assert.Single(head.Headers.GetValues("x-amz-meta-owner")));

        using var overwrite = await SendS3Async(client, HttpMethod.Put, "/bucket/source.bin", "new content"u8.ToArray());
        Assert.Equal(HttpStatusCode.OK, overwrite.StatusCode);
        Assert.NotEqual(etag, overwrite.Headers.ETag?.Tag);
        using var overwrittenHead = await SendS3Async(client, HttpMethod.Head, "/bucket/source.bin");
        Assert.Equal(overwrite.Headers.ETag, overwrittenHead.Headers.ETag);
        Assert.NotEqual(overwrite.Headers.ETag, head.Headers.ETag);
    }

    [Fact]
    public async Task S3Upload_DoesNotAcknowledgeFailedETagPersistence()
    {
        var handler = new ListingHandler { StateWriteFailures = 3 };
        var store = CreateStore(handler, handler);
        await using var factory = new LonghornProxyFactory();
        await using var proxy = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOneDriveStore>();
            services.AddSingleton<IOneDriveStore>(store);
        }));
        using var client = proxy.CreateClient();

        using var put = await SendS3Async(client, HttpMethod.Put, "/bucket/backup.bin", [1, 2, 3]);

        Assert.Equal(HttpStatusCode.BadGateway, put.StatusCode);
        Assert.Contains("<Code>InternalError</Code>", await put.Content.ReadAsStringAsync());
        Assert.Null(put.Headers.ETag);
        Assert.Equal(0, handler.StateWriteFailures);
    }

    [Fact]
    public async Task ObjectETags_KeepLegacyTagsAndRejectStaleChecksums()
    {
        var handler = new ListingHandler();
        handler.AddDirectObject("bucket", "backup.bin", [1, 2, 3]);
        var store = CreateStore(handler, handler);
        var original = await store.GetObjectInfoAsync("bucket", "backup.bin", CancellationToken.None);
        await store.PutObjectStateAsync("bucket", "backup.bin", new ObjectState
        {
            ETag = Convert.ToHexString(MD5.HashData([1, 2, 3])).ToLowerInvariant(),
            StorageETag = original.StorageETag
        }, CancellationToken.None);
        handler.AddDirectObject("bucket", "backup.bin", [4, 5, 6]);

        var current = await store.GetObjectInfoAsync("bucket", "backup.bin", CancellationToken.None);
        Assert.NotEqual(original.ETag, current.ETag);
        Assert.Equal(current.StorageETag, current.ETag);
        Assert.Equal(current.ETag, Assert.Single(await store.ListObjectsAsync("bucket", CancellationToken.None)).ETag);
        await using var download = await store.GetObjectAsync("bucket", "backup.bin", null, CancellationToken.None);
        Assert.Equal(current.ETag, download.Info.ETag);
    }

    [Theory]
    [MemberData(nameof(UploadStreamCases))]
    public async Task Uploads_LeaveCallerStreamsOpenAfterSuccess(int size, bool seekable, bool multipart)
    {
        var handler = new ListingHandler();
        var store = CreateStore(handler, handler);
        var payload = new byte[size + 1];
        payload[0] = 42;
        payload[^1] = 7;
        using var content = new UploadTestStream(payload, seekable);
        Assert.Equal(42, content.ReadByte());

        var uploaded = await UploadAsync(store, content, size, multipart, CancellationToken.None);

        var md5 = MD5.HashData(payload[1..]);
        var etag = multipart
            ? $"{Convert.ToHexString(MD5.HashData(md5)).ToLowerInvariant()}-1"
            : Convert.ToHexString(md5).ToLowerInvariant();
        Assert.Equal(etag, uploaded.ETag);
        Assert.True(content.CanRead);
        Assert.Equal(payload.LongLength, content.BytesRead);
        Assert.Equal(-1, content.ReadByte());
        if (seekable)
        {
            Assert.Equal(payload.LongLength, content.Position);
        }

        Assert.Equal(size > 4 * 1024 * 1024 ? (multipart ? 2 : 1) : 0, handler.UploadSessions);
        await using var download = await store.GetObjectAsync("bucket", "stream.bin", null, CancellationToken.None);
        Assert.Equal(payload[1..], await download.Response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [MemberData(nameof(UploadStreamCases))]
    public async Task Uploads_LeaveCallerStreamsOpenAfterGraphFailure(int size, bool seekable, bool multipart)
    {
        var handler = new FailingUploadHandler(new ListingHandler());
        var store = CreateStore(handler, handler);
        using var content = new UploadTestStream(new byte[size], seekable);

        var exception = await Assert.ThrowsAsync<GraphStoreException>(() =>
            UploadAsync(store, content, size, multipart, CancellationToken.None));

        Assert.Equal(503, exception.StatusCode);
        Assert.True(content.BytesRead > 0);
        Assert.True(content.CanRead);
        Assert.Equal(-1, content.ReadByte());
    }

    [Theory]
    [MemberData(nameof(UploadStreamCases))]
    public async Task Uploads_LeaveCallerStreamsOpenAfterCancellation(int size, bool seekable, bool multipart)
    {
        var handler = new ListingHandler();
        var store = CreateStore(handler, handler);
        using var cancellation = new CancellationTokenSource();
        using var content = new UploadTestStream(new byte[size], seekable)
        {
            BeforeRead = cancellation.Cancel
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UploadAsync(store, content, size, multipart, cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(content.ReadAttempts > 0);
        Assert.True(content.CanRead);
        content.BeforeRead = null;
        Assert.Equal(0, content.ReadByte());
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

    [Theory]
    [InlineData("")]
    [InlineData("file.txt")]
    [InlineData("/")]
    [InlineData("S3")]
    [InlineData("S3/bucket")]
    [InlineData("S3/bucket/file.txt")]
    public async Task PruneEmptyParents_StopsWithoutGraphRequestsWhenThereIsNoParentBelowTheBoundary(string deletedPath)
    {
        var handler = new ListingHandler();
        var store = CreateStore(handler, handler);

        await store.PruneEmptyParentFoldersAsync(deletedPath, "S3/bucket", CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.True(handler.HasPath("S3"));
        Assert.True(handler.HasPath("S3/bucket"));
    }

    [Theory]
    [InlineData("file.txt")]
    [InlineData("backups/file.txt")]
    [InlineData("backups/daily/nested/file.txt")]
    [InlineData("backups # %25/daily/file.txt")]
    public async Task DeleteObject_PrunesEmptyAncestorsWithoutRemovingBucket(string key)
    {
        var handler = new ListingHandler();
        handler.AddDirectObject("bucket", key, [1]);
        handler.AddDirectObject("other-bucket", "keep.txt", [2]);
        var store = CreateStore(handler, handler);

        await store.DeleteObjectAsync("bucket", key, CancellationToken.None);

        var segments = key.Split('/');
        for (var length = 1; length <= segments.Length; length++)
        {
            Assert.False(handler.HasPath($"S3/bucket/{string.Join('/', segments[..length])}"));
        }

        Assert.True(handler.HasPath("S3/bucket"));
        Assert.True(handler.HasPath("S3"));
        Assert.True(handler.HasPath("S3/other-bucket/keep.txt"));
        Assert.Equal(segments.Length - 1, handler.Requests.Count(request =>
            request.Method == "DELETE" && request.IfMatch == "\"folder\""));
    }

    [Theory]
    [InlineData("backups/other.txt", false)]
    [InlineData("backups/other/keep.txt", false)]
    [InlineData("backups/empty", true)]
    public async Task DeleteObject_StopsAtParentWithRemainingChildren(string sibling, bool folder)
    {
        var handler = new ListingHandler();
        handler.AddDirectObject("bucket", "backups/deep/file.txt", [1]);
        if (folder)
        {
            handler.AddEmptyFolder("bucket", sibling);
        }
        else
        {
            handler.AddDirectObject("bucket", sibling, [2]);
        }
        var store = CreateStore(handler, handler);

        await store.DeleteObjectAsync("bucket", "backups/deep/file.txt", CancellationToken.None);

        Assert.False(handler.HasPath("S3/bucket/backups/deep"));
        Assert.True(handler.HasPath("S3/bucket/backups"));
        Assert.True(handler.HasPath($"S3/bucket/{sibling}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteObject_PrunesMetadataWithoutRemovingSharedStateOrActiveUploads(bool sameShard)
    {
        const string key = "backups/file.txt";
        const string stateRoot = "S3/.onedrive-s3-proxy-state";
        var shard = OneDrivePathMapper.Hash(key)[..2];
        var otherKey = Enumerable.Range(0, 10000)
            .Select(index => $"other-{index}.txt")
            .First(candidate => OneDrivePathMapper.Hash(candidate).StartsWith(shard, StringComparison.Ordinal) == sameShard);
        var handler = new ListingHandler();
        handler.AddDirectObject("bucket", key, [1]);
        var store = CreateStore(handler, handler);
        await store.PutObjectStateAsync("bucket", key, new ObjectState(), CancellationToken.None);
        await store.PutObjectStateAsync("bucket", otherKey, new ObjectState { ContentType = "text/plain" }, CancellationToken.None);
        var upload = await store.CreateMultipartUploadAsync("bucket", "pending/file.bin", new ObjectState(), CancellationToken.None);
        var manifestPath = $"{stateRoot}/multipart/bucket/{upload.UploadId}/manifest.json";

        await store.DeleteObjectAsync("bucket", key, CancellationToken.None);

        Assert.False(handler.HasPath($"{stateRoot}/objects/bucket/{shard}/{OneDrivePathMapper.Hash(key)}.json"));
        Assert.Equal(sameShard, handler.HasPath($"{stateRoot}/objects/bucket/{shard}"));
        Assert.Equal("text/plain", (await store.GetObjectStateAsync("bucket", otherKey, CancellationToken.None)).ContentType);
        Assert.True(handler.HasPath(manifestPath));

        await store.DeleteObjectStateAsync("bucket", otherKey, CancellationToken.None);

        Assert.False(handler.HasPath($"{stateRoot}/objects/bucket"));
        Assert.True(handler.HasPath($"{stateRoot}/objects"));
        Assert.True(handler.HasPath(stateRoot));
        Assert.True(handler.HasPath(manifestPath));
        Assert.True(handler.HasPath("S3/bucket"));
    }

    [Theory]
    [InlineData("backups/missing.txt")]
    [InlineData("BACKUPS/file.txt")]
    [InlineData("backups")]
    public async Task DeleteObject_DoesNotPrunePayloadFoldersForMissingOrFolderKeys(string key)
    {
        var handler = new ListingHandler();
        handler.AddDirectObject("bucket", "backups/file.txt", [1]);
        var store = CreateStore(handler, handler);

        await store.DeleteObjectAsync("bucket", key, CancellationToken.None);

        Assert.True(handler.HasPath("S3/bucket/backups/file.txt"));
        Assert.DoesNotContain(handler.Requests, request => request.Method == "DELETE" && request.IfMatch is not null);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    public async Task DeleteObject_LeavesFoldersOnCleanupConflict(HttpStatusCode status)
    {
        var handler = new ListingHandler();
        handler.AddDirectObject("bucket", "backups/deep/file.txt", [1]);
        handler.OverrideResponse = request =>
            request.Method == HttpMethod.Delete &&
            Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).EndsWith("/drive/items/S3/bucket/backups/deep", StringComparison.Ordinal)
                ? new HttpResponseMessage(status)
                : null;
        var store = CreateStore(handler, handler);

        await store.DeleteObjectAsync("bucket", "backups/deep/file.txt", CancellationToken.None);

        Assert.False(handler.HasPath("S3/bucket/backups/deep/file.txt"));
        Assert.True(handler.HasPath("S3/bucket/backups/deep"));
        Assert.True(handler.HasPath("S3/bucket/backups"));
        Assert.Single(handler.Requests, request => request.Method == "DELETE" && request.IfMatch == "\"folder\"");
    }

    [Theory]
    [InlineData("lookup")]
    [InlineData("children")]
    [InlineData("delete")]
    public async Task DeleteObject_ContinuesWhenAnEmptyParentDisappears(string phase)
    {
        var handler = new ListingHandler();
        handler.AddDirectObject("bucket", "backups/deep/file.txt", [1]);
        handler.OverrideResponse = request =>
        {
            if (handler.HasPath("S3/bucket/backups/deep/file.txt"))
            {
                return null;
            }

            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var matches = phase switch
            {
                "lookup" => request.Method == HttpMethod.Get && path.EndsWith("/drive/root:/S3/bucket/backups/deep", StringComparison.Ordinal),
                "children" => request.Method == HttpMethod.Get && path.EndsWith("/drive/items/S3/bucket/backups/deep/children", StringComparison.Ordinal),
                _ => request.Method == HttpMethod.Delete && path.EndsWith("/drive/items/S3/bucket/backups/deep", StringComparison.Ordinal)
            };
            if (!matches)
            {
                return null;
            }

            handler.RemovePath("S3/bucket/backups/deep");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var store = CreateStore(handler, handler);

        await store.DeleteObjectAsync("bucket", "backups/deep/file.txt", CancellationToken.None);

        Assert.False(handler.HasPath("S3/bucket/backups"));
        Assert.True(handler.HasPath("S3/bucket"));
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.InternalServerError)]
    public async Task DeleteObject_PropagatesUnexpectedCleanupErrors(bool delete, HttpStatusCode status)
    {
        var handler = new ListingHandler();
        handler.AddDirectObject("bucket", "backups/file.txt", [1]);
        handler.OverrideResponse = request =>
            request.Method == (delete ? HttpMethod.Delete : HttpMethod.Get) &&
            Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).EndsWith(
                "/drive/items/S3/bucket/backups" + (delete ? string.Empty : "/children"), StringComparison.Ordinal)
                ? new HttpResponseMessage(status)
                : null;
        var store = CreateStore(handler, handler);

        var exception = await Assert.ThrowsAsync<GraphStoreException>(() =>
            store.DeleteObjectAsync("bucket", "backups/file.txt", CancellationToken.None));

        Assert.Equal((int)status, exception.StatusCode);
        Assert.False(handler.HasPath("S3/bucket/backups/file.txt"));
        Assert.True(handler.HasPath("S3/bucket/backups"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteObject_ChecksChildrenInsteadOfTrustingChildCount(bool newChild)
    {
        var handler = new ListingHandler();
        handler.AddDirectObject("bucket", "backups/file.txt", [1]);
        handler.OverrideResponse = request =>
        {
            if (request.Method != HttpMethod.Get)
            {
                return null;
            }

            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            if (path.EndsWith("/drive/root:/S3/bucket/backups", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "S3/bucket/backups",
                        name = "backups",
                        eTag = "\"folder\"",
                        folder = new { childCount = newChild ? 0 : 1 }
                    })
                };
            }

            if (newChild && path.EndsWith("/drive/items/S3/bucket/backups/children", StringComparison.Ordinal))
            {
                handler.AddDirectObject("bucket", "backups/new.txt", [2]);
            }

            return null;
        };
        var store = CreateStore(handler, handler);

        await store.DeleteObjectAsync("bucket", "backups/file.txt", CancellationToken.None);

        Assert.Equal(newChild, handler.HasPath("S3/bucket/backups"));
        Assert.Equal(newChild, handler.HasPath("S3/bucket/backups/new.txt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("invalid")]
    public async Task DeleteObject_RequiresAValidFolderEntityTagForCleanup(string etag)
    {
        var handler = new ListingHandler();
        handler.AddDirectObject("bucket", "backups/file.txt", [1]);
        handler.OverrideResponse = request =>
            request.Method == HttpMethod.Get &&
            Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).EndsWith("/drive/root:/S3/bucket/backups", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "S3/bucket/backups",
                        name = "backups",
                        eTag = etag,
                        folder = new { childCount = 0 }
                    })
                }
                : null;
        var store = CreateStore(handler, handler);

        var exception = await Assert.ThrowsAsync<GraphStoreException>(() =>
            store.DeleteObjectAsync("bucket", "backups/file.txt", CancellationToken.None));

        Assert.Equal(502, exception.StatusCode);
        Assert.True(handler.HasPath("S3/bucket/backups"));
        Assert.DoesNotContain(handler.Requests, request => request.Method == "DELETE" && request.IfMatch is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S3Deletes_PruneEmptyPayloadAndMetadataFolders(bool bulk)
    {
        var handler = new ListingHandler();
        var store = CreateStore(handler, handler);
        foreach (var key in new[] { "backups/first.txt", "backups/second.txt" })
        {
            handler.AddDirectObject("bucket", key, [1]);
            await store.PutObjectStateAsync("bucket", key, new ObjectState(), CancellationToken.None);
        }
        await using var factory = new LonghornProxyFactory();
        await using var proxy = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOneDriveStore>();
            services.AddSingleton<IOneDriveStore>(store);
        }));
        using var client = proxy.CreateClient();

        using var first = await SendS3Async(client, HttpMethod.Delete, "/bucket/backups/first.txt");
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.True(handler.HasPath("S3/bucket/backups"));

        using var last = bulk
            ? await SendS3Async(client, HttpMethod.Post, "/bucket?delete",
                "<Delete><Object><Key>backups/first.txt</Key></Object><Object><Key>backups/second.txt</Key></Object></Delete>"u8.ToArray())
            : await SendS3Async(client, HttpMethod.Delete, "/bucket/backups/second.txt");
        Assert.Equal(bulk ? HttpStatusCode.OK : HttpStatusCode.NoContent, last.StatusCode);
        Assert.False(handler.HasPath("S3/bucket/backups"));
        Assert.False(handler.HasPath("S3/.onedrive-s3-proxy-state/objects/bucket"));
        Assert.True(handler.HasPath("S3/bucket"));
        Assert.True(handler.HasPath("S3/.onedrive-s3-proxy-state"));

        using var list = await SendS3Async(client, HttpMethod.Get, "/bucket?prefix=backups%2F&delimiter=%2F");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain(XDocument.Parse(await list.Content.ReadAsStringAsync()).Descendants(),
            element => element.Name.LocalName is "Contents" or "CommonPrefixes");
    }

    [Theory]
    [InlineData(4 * 1024 * 1024 + 1, false)]
    [InlineData(5 * 1024 * 1024, false)]
    [InlineData(10 * 1024 * 1024 + 37, false)]
    [InlineData(5 * 1024 * 1024, true)]
    public async Task LargeUploads_UseSessionsAndCompleteAtTheDirectKeyPath(int size, bool overwrite)
    {
        var handler = new ListingHandler();
        if (overwrite)
        {
            handler.AddDirectObject("bucket", "large/block.blk", [1]);
        }

        var store = CreateStore(handler, handler);
        var payload = new byte[size];
        payload[^1] = 7;

        using (var payloadStream = new MemoryStream(payload))
        {
            await store.PutObjectAsync("bucket", "large/block.blk", payloadStream, payload.Length, CancellationToken.None);
        }

        Assert.Equal(1, handler.UploadSessions);
        Assert.True(handler.HasPath("S3/bucket/large/block.blk"));
        await using var download = await store.GetObjectAsync("bucket", "large/block.blk", null, CancellationToken.None);
        Assert.Equal(payload, await download.Response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5 * 1024 * 1024)]
    [InlineData(10 * 1024 * 1024 + 37)]
    public async Task MultipartCompletion_WritesTheOriginalKeyAndPreservesMetadata(int size)
    {
        var handler = new ListingHandler();
        var store = CreateStore(handler, handler);
        var state = new ObjectState { ContentType = "application/test", Metadata = new() { ["source"] = "backup" } };
        var payload = new byte[size];
        payload[^1] = 7;
        using var content = new MemoryStream(payload);
        var upload = await store.CreateMultipartUploadAsync("bucket", "nested/backup.blk", state, CancellationToken.None);
        var part = await store.PutPartAsync(
            "bucket", "nested/backup.blk", upload.UploadId, 1, content, payload.Length, CancellationToken.None);

        Assert.Equal(Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant(), part.ETag);
        Assert.True(content.CanRead);

        var completed = await store.CompleteMultipartUploadAsync(
            "bucket", "nested/backup.blk", upload.UploadId, [part], CancellationToken.None);
        var etag = $"{Convert.ToHexString(MD5.HashData(MD5.HashData(payload))).ToLowerInvariant()}-1";
        Assert.Equal(etag, completed.ETag);

        Assert.True(handler.HasPath("S3/bucket/nested/backup.blk"));
        Assert.False(handler.HasPath($"S3/.onedrive-s3-proxy-state/multipart/bucket/{upload.UploadId}"));
        Assert.Equal(size > 4 * 1024 * 1024 ? 2 : 0, handler.UploadSessions);
        var savedState = await store.GetObjectStateAsync("bucket", "nested/backup.blk", CancellationToken.None);
        Assert.Equal(etag, savedState.ETag);
        Assert.Equal("application/test", savedState.ContentType);
        Assert.Equal("backup", savedState.Metadata["source"]);
        await using var download = await store.GetObjectAsync("bucket", "nested/backup.blk", null, CancellationToken.None);
        Assert.Equal(etag, download.Info.ETag);
        Assert.Equal(payload, await download.Response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task MultipartCompletion_ETagUsesOnlySelectedPartsInOrder()
    {
        var handler = new ListingHandler();
        var store = CreateStore(handler, handler);
        var firstBody = new byte[5 * 1024 * 1024];
        var secondBody = "tail"u8.ToArray();
        var upload = await store.CreateMultipartUploadAsync("bucket", "backup.bin", new ObjectState(), CancellationToken.None);
        using var firstContent = new MemoryStream(firstBody);
        using var secondContent = new MemoryStream(secondBody);
        using var unusedContent = new MemoryStream([42]);
        var first = await store.PutPartAsync(
            "bucket", "backup.bin", upload.UploadId, 1, firstContent, firstBody.Length, CancellationToken.None);
        var second = await store.PutPartAsync(
            "bucket", "backup.bin", upload.UploadId, 2, secondContent, secondBody.Length, CancellationToken.None);
        await store.PutPartAsync("bucket", "backup.bin", upload.UploadId, 3, unusedContent, 1, CancellationToken.None);

        var completed = await store.CompleteMultipartUploadAsync(
            "bucket", "backup.bin", upload.UploadId, [first, second], CancellationToken.None);

        var etag = $"{Convert.ToHexString(MD5.HashData(
            [.. MD5.HashData(firstBody), .. MD5.HashData(secondBody)])).ToLowerInvariant()}-2";
        Assert.Equal(etag, completed.ETag);
        var restarted = CreateStore(handler, handler);
        Assert.Equal(etag, (await restarted.GetObjectInfoAsync("bucket", "backup.bin", CancellationToken.None)).ETag);
        Assert.Equal(etag, Assert.Single(await restarted.ListObjectsAsync("bucket", CancellationToken.None)).ETag);
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
    public async Task ListObjects_TraversesDirectPathsWithoutDownloadingPayloads()
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
            request.Uri.AbsolutePath.EndsWith("/content", StringComparison.Ordinal) &&
            !request.Uri.AbsolutePath.Contains(".onedrive-s3-proxy-state", StringComparison.Ordinal));

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
    public async Task ListObjects_SystemBackupPrefixDoesNotVisitVolumeBlocks()
    {
        var handler = new ListingHandler();
        handler.AddDirectObject("bucket", "backupstore/system-backups/backup-a/config.json", [1, 2, 3]);
        for (var index = 0; index < 256; index++)
        {
            handler.AddDirectObject("bucket", $"backupstore/volumes/{index:x4}/blocks/aa/bb/data.blk", [1]);
        }

        var store = CreateStore(handler, handler);
        var listing = await store.ListObjectsAsync("bucket", "backupstore/system-backups/", null, CancellationToken.None);

        Assert.Equal("backupstore/system-backups/backup-a/config.json", Assert.Single(listing.Objects).Key);
        Assert.Empty(listing.CommonPrefixes);
        Assert.Equal(2, handler.Requests.Count(request => request.Uri.AbsolutePath.EndsWith(":/children", StringComparison.Ordinal)));
        Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath.Contains("/volumes", StringComparison.Ordinal));
        Assert.Equal(0, handler.ContentDownloads);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("", "/")]
    [InlineData("logs/", null)]
    [InlineData("logs/", "/")]
    [InlineData("logs/20", "/")]
    [InlineData("logs/2026/", "/")]
    [InlineData("logs/2026/a", null)]
    [InlineData("logs/2026/a.txt/", "/")]
    [InlineData("logs/v1", "--")]
    [InlineData("logs/", "26/")]
    [InlineData("logs/", "2026/a")]
    [InlineData("logs/", ".")]
    [InlineData("log", "/")]
    [InlineData("LOGS/", "/")]
    [InlineData("missing/", "/")]
    [InlineData("logs//", "/")]
    public async Task ListObjects_PrefixAndDelimiterMatchObjectKeySemantics(string prefix, string? delimiter)
    {
        string[] keys =
        [
            "logs/2026/a.txt", "logs/2026/b.txt", "logs/2027/a.txt",
            "logs/v1--alpha/a.txt", "logs/v1--beta/b.txt", "logs-extra/root.txt", "plain.txt"
        ];
        var handler = new ListingHandler();
        foreach (var key in keys)
        {
            handler.AddDirectObject("bucket", key, [1]);
        }

        handler.AddEmptyFolder("bucket", "empty/nested");
        handler.AddEmptyFolder("bucket", "logs/2025/empty");
        var expected = ObjectListing.FromObjects(
            keys.Select(key => new ObjectInfo(key, 1, DateTimeOffset.UtcNow, "\"etag\"")),
            prefix,
            delimiter);
        var store = CreateStore(handler, handler);

        var actual = await store.ListObjectsAsync("bucket", prefix, delimiter, CancellationToken.None);

        Assert.Equal(expected.Objects.Select(item => item.Key), actual.Objects.Select(item => item.Key));
        Assert.Equal(expected.CommonPrefixes, actual.CommonPrefixes);
        Assert.Equal(0, handler.ContentDownloads);
    }

    [Fact]
    public async Task ListObjects_FollowsGraphPages()
    {
        var handler = new ListingHandler { PageSize = 1 };
        foreach (var key in new[] { "a.txt", "b.txt", "c.txt" })
        {
            handler.AddDirectObject("bucket", key, [1]);
        }

        var store = CreateStore(handler, handler);
        var listing = await store.ListObjectsAsync("bucket", string.Empty, null, CancellationToken.None);

        Assert.Equal(["a.txt", "b.txt", "c.txt"], listing.Objects.Select(item => item.Key));
        Assert.Equal(3, handler.Requests.Count(request => request.Uri.AbsolutePath.EndsWith(":/children", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ListObjects_DelimiterStopsAfterFindingOneDescendantFile()
    {
        var handler = new ListingHandler { PageSize = 1 };
        for (var index = 0; index < 256; index++)
        {
            handler.AddDirectObject("bucket", $"logs/{index:x4}/blocks/data.blk", [1]);
        }

        var store = CreateStore(handler, handler);
        var listing = await store.ListObjectsAsync("bucket", string.Empty, "/", CancellationToken.None);

        Assert.Empty(listing.Objects);
        Assert.Equal(["logs/"], listing.CommonPrefixes);
        Assert.Equal(4, handler.Requests.Count(request => request.Uri.AbsolutePath.EndsWith(":/children", StringComparison.Ordinal)));
        Assert.DoesNotContain(handler.Requests, request => request.Uri.Query.Contains("$skiptoken", StringComparison.Ordinal));
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

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _clients.Clear();
    }

    private static async Task<ObjectInfo> UploadAsync(
        OneDriveStore store,
        Stream content,
        long length,
        bool multipart,
        CancellationToken cancellationToken)
    {
        if (!multipart)
        {
            return await store.PutObjectAsync("bucket", "stream.bin", content, length, cancellationToken);
        }

        var upload = await store.CreateMultipartUploadAsync("bucket", "stream.bin", new ObjectState(), cancellationToken);
        var part = await store.PutPartAsync(
            "bucket", "stream.bin", upload.UploadId, 1, content, length, cancellationToken);
        return await store.CompleteMultipartUploadAsync("bucket", "stream.bin", upload.UploadId, [part], cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendS3Async(
        HttpClient client,
        HttpMethod method,
        string path,
        byte[]? body = null,
        Dictionary<string, string>? headers = null,
        bool sigV4 = true)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new("application/test");
            request.Content.Headers.ContentMD5 = MD5.HashData(body);
        }

        foreach (var header in headers ?? [])
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (sigV4)
        {
            LonghornSigV4Signer.Sign(request, body ?? []);
        }
        else
        {
            SigV2CompatibilityTests.Sign(request);
        }

        return await client.SendAsync(request);
    }

    private OneDriveStore CreateStore(
        HttpMessageHandler graphHandler,
        HttpMessageHandler monitorHandler)
    {
        var graphClient = new HttpClient(graphHandler)
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
        };
        _clients.Add(graphClient);
        var monitorClient = new HttpClient(monitorHandler, disposeHandler: !ReferenceEquals(graphHandler, monitorHandler));
        _clients.Add(monitorClient);
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

    private sealed class UploadTestStream(byte[] content, bool seekable) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public Action? BeforeRead { get; set; }
        public int ReadAttempts { get; private set; }
        public long BytesRead { get; private set; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => seekable && _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => seekable ? _inner.Length : throw new NotSupportedException();
        public override long Position
        {
            get => seekable ? _inner.Position : throw new NotSupportedException();
            set
            {
                if (!seekable)
                {
                    throw new NotSupportedException();
                }

                _inner.Position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadAttempts++;
            BeforeRead?.Invoke();
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadAttempts++;
            BeforeRead?.Invoke();
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            seekable ? _inner.Seek(offset, origin) : throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FailingUploadHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put &&
                request.Content is { } content &&
                (content.Headers.ContentRange is not null ||
                 path.EndsWith(".bin:/content", StringComparison.Ordinal) ||
                 path.EndsWith(".bin/content", StringComparison.Ordinal)))
            {
                await content.CopyToAsync(Stream.Null, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class DisposalTrackingHandler : HttpMessageHandler
    {
        public int DisposeCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A disposed client must not send requests.");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }

            base.Dispose(disposing);
        }
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
            Requests.Add(new RequestRecord(request.Method.Method, request.RequestUri!,
                request.Headers.IfMatch.Count == 0 ? null : request.Headers.IfMatch.ToString()));
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
        public int PageSize { get; init; } = int.MaxValue;
        public int UploadSessions { get; private set; }
        public int StateWriteFailures { get; set; }
        public Action? BeforeUpload { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage?>? OverrideResponse { get; set; }

        public bool HasPath(string path) => _items.ContainsKey(path);

        public void RemovePath(string path)
        {
            foreach (var candidate in _items.Keys.Where(candidate => candidate == path || candidate.StartsWith(path + "/", StringComparison.Ordinal)))
            {
                _items.TryRemove(candidate, out _);
            }
        }

        public void AddDirectObject(string bucket, string key, byte[] content) =>
            AddFile($"S3/{bucket}/{key}", content, content.Length);

        public void AddEmptyFolder(string bucket, string key)
        {
            var segments = $"S3/{bucket}/{key}".Split('/');
            for (var length = 1; length <= segments.Length; length++)
            {
                _items.TryAdd(string.Join('/', segments[..length]), (true, null, 0));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var upload in _uploads.Values)
                {
                    upload.Content.Dispose();
                }

                _uploads.Clear();
            }

            base.Dispose(disposing);
        }

        protected override async Task<HttpResponseMessage> RespondAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (OverrideResponse?.Invoke(request) is { } overridden)
            {
                return overridden;
            }

            if (request.RequestUri!.Host == "monitor.test")
            {
                return Json(HttpStatusCode.OK, new { status = "completed" });
            }

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

            if (request.Method == HttpMethod.Post && rest.EndsWith("/copy", StringComparison.Ordinal))
            {
                var source = _items[rest[..^"/copy".Length]];
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var parent = body.RootElement.GetProperty("parentReference").GetProperty("id").GetString();
                var name = body.RootElement.GetProperty("name").GetString();
                AddFile($"{parent}/{name}", source.Content, source.Size);
                var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                response.Headers.Location = new Uri("https://monitor.test/copy/1");
                return response;
            }

            if (rest.EndsWith(":/children", StringComparison.Ordinal) ||
                byId && rest.EndsWith("/children", StringComparison.Ordinal))
            {
                var suffix = rest.EndsWith(":/children", StringComparison.Ordinal) ? ":/children" : "/children";
                var parent = rest[..^suffix.Length];
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

                return Children(parent, request.RequestUri);
            }

            if (rest.EndsWith(":/createUploadSession", StringComparison.Ordinal) ||
                byId && rest.EndsWith("/createUploadSession", StringComparison.Ordinal))
            {
                var suffix = rest.EndsWith(":/createUploadSession", StringComparison.Ordinal)
                    ? ":/createUploadSession" : "/createUploadSession";
                var path = rest[..^suffix.Length];
                BeforeUpload?.Invoke();
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var uploadProperties = body.RootElement.GetProperty("item");
                if (uploadProperties.TryGetProperty("name", out _))
                {
                    return Json(HttpStatusCode.BadRequest, new
                    {
                        error = new { code = "invalidRequest", message = "Upload sessions must use the destination name from the URL." }
                    });
                }

                var behavior = uploadProperties.GetProperty("@microsoft.graph.conflictBehavior").GetString();
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
                    if (path.Contains(".onedrive-s3-proxy-state", StringComparison.Ordinal) && StateWriteFailures > 0)
                    {
                        StateWriteFailures--;
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }

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

                if (_items[actual].IsFolder && request.Headers.IfMatch.Count > 0 &&
                    request.Headers.IfMatch.ToString() != "\"folder\"")
                {
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                }

                RemovePath(actual);
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

        private HttpResponseMessage Children(string folder, Uri requestUri)
        {
            if (!_items.TryGetValue(folder, out var parent) || !parent.IsFolder)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var prefix = $"{folder}/";
            var skipToken = System.Text.RegularExpressions.Regex.Match(requestUri.Query, @"(?:\?|&)\$skiptoken=(\d+)");
            var skip = skipToken.Success ? int.Parse(skipToken.Groups[1].Value) : 0;
            var children = _items
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                               !pair.Key[prefix.Length..].Contains('/'))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => Describe(pair.Key, pair.Value))
                .ToList();
            var page = new Dictionary<string, object>
            {
                ["value"] = children.Skip(skip).Take(PageSize).ToList()
            };
            if (children.Count - skip > PageSize)
            {
                page["@odata.nextLink"] = $"{requestUri.GetLeftPart(UriPartial.Path)}?$skiptoken={skip + PageSize}";
            }

            return Json(HttpStatusCode.OK, page);
        }

        private object Describe(string path, (bool IsFolder, byte[]? Content, long Size) item)
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
                    folder = (object?)new
                    {
                        childCount = _items.Keys.Count(candidate =>
                            candidate.StartsWith(path + "/", StringComparison.Ordinal) &&
                            !candidate[(path.Length + 1)..].Contains('/'))
                    }
                }
                : new
                {
                    id = path,
                    name,
                    size = item.Size,
                    eTag = $"\"graph-{Convert.ToHexString(SHA256.HashData(item.Content ?? []))}\"",
                    createdDateTime = DateTimeOffset.UtcNow,
                    lastModifiedDateTime = DateTimeOffset.UtcNow,
                    folder = (object?)null
                };
        }
    }

    private sealed record RequestRecord(string Method, Uri Uri, string? IfMatch);
}
