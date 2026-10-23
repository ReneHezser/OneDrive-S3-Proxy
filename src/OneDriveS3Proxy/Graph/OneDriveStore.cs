using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OneDriveS3Proxy.Graph;

public sealed class OneDriveStore(
    HttpClient httpClient,
    IHttpClientFactory httpClientFactory,
    IGraphTokenProvider tokenProvider,
    IOptions<ProxyOptions> options) : IOneDriveStore
{
    private const string StateFolder = ".onedrive-s3-proxy-state";
    private const int SimpleUploadLimit = 4 * 1024 * 1024;
    private const int UploadChunkSize = 10 * 1024 * 1024;
    private const long MinimumMultipartPartSize = 5 * 1024 * 1024;
    private const int StateWriteAttempts = 3;
    private const int ListConcurrency = 4;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ProxyOptions _options = options.Value;
    private readonly HttpClient _monitorClient = httpClientFactory.CreateClient("GraphMonitor");
    private readonly SemaphoreSlim _rootLock = new(1, 1);
    private readonly SemaphoreSlim _readinessLock = new(1, 1);
    // A data file name is the SHA-256 of its key, so a hash-to-key mapping never goes stale.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _keysByHash =
        new(StringComparer.Ordinal);
    private volatile bool _rootReady;
    private DateTimeOffset _readyUntil;

    public async Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken)
    {
        await EnsureRootAsync(cancellationToken);
        var items = await GetChildrenAsync(_options.OneDrive.RootFolder, cancellationToken);
        return items
            .Where(item => item.Folder is not null && item.Name != StateFolder)
            .Select(item => new BucketInfo(item.Name, item.CreatedDateTime))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task CreateBucketAsync(string bucket, CancellationToken cancellationToken)
    {
        await EnsureRootAsync(cancellationToken);
        await CreateFolderAsync(_options.OneDrive.RootFolder, bucket, allowExisting: false, cancellationToken);
    }

    public async Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken)
    {
        await EnsureRootAsync(cancellationToken);
        var item = await TryGetItemAsync(BucketPath(bucket), cancellationToken);
        return item?.Folder is not null;
    }

    public async Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken)
    {
        await EnsureRootAsync(cancellationToken);
        if ((await ListObjectsAsync(bucket, cancellationToken)).Count != 0)
        {
            throw new GraphStoreException(409, "The bucket is not empty.");
        }

        await DeletePathAsync(BucketPath(bucket), ignoreMissing: false, cancellationToken);
        await DeletePathAsync(StatePath("objects", bucket), ignoreMissing: true, cancellationToken);
        await DeletePathAsync(MultipartBucketPath(bucket), ignoreMissing: true, cancellationToken);
        _keysByHash.TryRemove(bucket, out _);
    }

    public async Task<IReadOnlyList<ObjectInfo>> ListObjectsAsync(string bucket, CancellationToken cancellationToken)
    {
        if (!await BucketExistsAsync(bucket, cancellationToken))
        {
            throw new GraphStoreException(404, "The specified bucket does not exist.", "NoSuchBucket");
        }

        var results = new Dictionary<string, ObjectInfo>(StringComparer.Ordinal);
        var legacyFolders = new Queue<(string Path, string Prefix)>();
        legacyFolders.Enqueue((BucketPath(bucket), string.Empty));
        while (legacyFolders.TryDequeue(out var folder))
        {
            foreach (var item in await GetChildrenAsync(folder.Path, cancellationToken))
            {
                if (folder.Prefix.Length == 0 &&
                    item.Folder is not null &&
                    item.Name == OneDrivePathMapper.DataFolder)
                {
                    continue;
                }

                var key = string.IsNullOrEmpty(folder.Prefix) ? item.Name : $"{folder.Prefix}/{item.Name}";
                if (item.Folder is not null)
                {
                    legacyFolders.Enqueue(($"{folder.Path}/{item.Name}", key));
                }
                else
                {
                    results[key] = ToObjectInfo(key, item);
                }
            }
        }

        await ListDataObjectsAsync(bucket, results, cancellationToken);
        return results.Values.OrderBy(item => item.Key, StringComparer.Ordinal).ToList();
    }

    private async Task ListDataObjectsAsync(
        string bucket,
        Dictionary<string, ObjectInfo> results,
        CancellationToken cancellationToken)
    {
        var dataRoot = $"{BucketPath(bucket)}/{OneDrivePathMapper.DataFolder}";
        var shards = await TryGetChildrenAsync(dataRoot, cancellationToken);
        var dataItems = new ConcurrentDictionary<string, DriveItem>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            shards.Where(shard => shard.Folder is not null),
            new ParallelOptions { MaxDegreeOfParallelism = ListConcurrency, CancellationToken = cancellationToken },
            async (shard, token) =>
            {
                foreach (var item in await GetChildrenAsync($"{dataRoot}/{shard.Name}", token))
                {
                    if (item.Folder is null && item.Name.EndsWith(".data", StringComparison.Ordinal))
                    {
                        dataItems[item.Name[..^".data".Length]] = item;
                    }
                }
            });

        var knownKeys = _keysByHash.GetOrAdd(bucket, _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal));
        var currentKeys = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            dataItems.Keys,
            new ParallelOptions { MaxDegreeOfParallelism = ListConcurrency, CancellationToken = cancellationToken },
            async (hash, token) =>
            {
                if (!knownKeys.TryGetValue(hash, out var key))
                {
                    try
                    {
                        var state = await GetJsonAsync<ObjectState>(
                            StatePath("objects", bucket, hash[..2], $"{hash}.json"),
                            token);
                        key = state.Key;
                    }
                    catch (GraphStoreException exception) when (exception.StatusCode == 404)
                    {
                        return;
                    }
                }

                if (key is not null && OneDrivePathMapper.Hash(key) == hash)
                {
                    currentKeys[hash] = key;
                }
            });

        // Dropping hashes that no longer have data keeps the cache sized to the live objects.
        _keysByHash[bucket] = currentKeys;
        foreach (var (hash, key) in currentKeys)
        {
            results[key] = ToObjectInfo(key, dataItems[hash]);
        }
    }

    public async Task<ObjectInfo> PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        long? length,
        CancellationToken cancellationToken)
    {
        await EnsureBucketAndLengthAsync(bucket, length, cancellationToken);
        var item = await UploadFileAsync(
            ObjectDataPath(bucket, key),
            content,
            length!.Value,
            requireExistingBucket: true,
            cancellationToken);
        if (await TryResolveLegacyObjectAsync(bucket, key, cancellationToken) is { } legacy)
        {
            await DeletePathAsync(legacy.Path, ignoreMissing: true, cancellationToken);
        }

        return ToObjectInfo(key, item);
    }

    public async Task<GraphDownload> GetObjectAsync(
        string bucket,
        string key,
        string? range,
        CancellationToken cancellationToken)
    {
        var (path, item) = await ResolveObjectAsync(bucket, key, cancellationToken);
        var info = ToObjectInfo(key, item);
        var request = await CreateRequestAsync(
            HttpMethod.Get,
            $"{ItemPath(path)}:/content",
            null,
            cancellationToken);
        if (range is not null)
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await EnsureSuccessAsync(response, cancellationToken);
        }

        return new GraphDownload(response, info);
    }

    public async Task<ObjectInfo> GetObjectInfoAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        var (_, item) = await ResolveObjectAsync(bucket, key, cancellationToken);
        return ToObjectInfo(key, item);
    }

    public async Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        await DeletePathAsync(ObjectDataPath(bucket, key), ignoreMissing: true, cancellationToken);
        if (await TryResolveLegacyObjectAsync(bucket, key, cancellationToken) is { } legacy)
        {
            await DeletePathAsync(legacy.Path, ignoreMissing: true, cancellationToken);
        }

        await DeleteObjectStateAsync(bucket, key, cancellationToken);
    }

    public async Task<ObjectInfo> CopyObjectAsync(
        string sourceBucket,
        string sourceKey,
        string destinationBucket,
        string destinationKey,
        CancellationToken cancellationToken)
    {
        var (sourcePath, _) = await ResolveObjectAsync(sourceBucket, sourceKey, cancellationToken);
        if (!await BucketExistsAsync(destinationBucket, cancellationToken))
        {
            throw new GraphStoreException(404, "The destination bucket does not exist.", "NoSuchBucket");
        }

        var destinationPath = ObjectDataPath(destinationBucket, destinationKey);
        await EnsureParentFoldersForPathAsync(
            destinationPath,
            requireExistingBucket: true,
            cancellationToken);

        var parentPath = destinationPath[..destinationPath.LastIndexOf('/')];
        var parent = await GetItemAsync(parentPath, cancellationToken);
        var name = destinationPath.Split('/').Last();
        using var body = JsonContent.Create(new
        {
            parentReference = new { id = parent.Id },
            name
        });
        using var response = await SendAsync(
            HttpMethod.Post,
            $"{ItemPath(sourcePath)}:/copy?@microsoft.graph.conflictBehavior=replace",
            body,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var monitorUrl = response.Headers.Location
            ?? throw new GraphStoreException(502, "Microsoft Graph did not return a copy monitor URL.");
        await MonitorCopyAsync(monitorUrl, cancellationToken);

        var copied = await TryGetItemAsync(destinationPath, cancellationToken)
            ?? throw new GraphStoreException(502, "Microsoft Graph completed the copy without creating the destination item.");
        if (await TryResolveLegacyObjectAsync(destinationBucket, destinationKey, cancellationToken) is { } legacy)
        {
            await DeletePathAsync(legacy.Path, ignoreMissing: true, cancellationToken);
        }

        return ToObjectInfo(destinationKey, copied);
    }

    private async Task MonitorCopyAsync(Uri monitorUrl, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, monitorUrl);
            using var response = await _monitorClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.SeeOther)
            {
                return;
            }

            await EnsureSuccessAsync(response, cancellationToken);
            var status = await response.Content.ReadFromJsonAsync<AsyncJobStatus>(cancellationToken)
                ?? throw new GraphStoreException(502, "Microsoft Graph returned an empty copy status.");
            if (status.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (status.Status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                status.Status.Equals("deleteFailed", StringComparison.OrdinalIgnoreCase))
            {
                throw new GraphStoreException(
                    502,
                    $"Microsoft Graph copy failed: {status.Error?.ToString() ?? "no error detail"}");
            }

            var retryAfter = response.Headers.RetryAfter?.Delta;
            await Task.Delay(retryAfter ?? delay, cancellationToken);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 5));
        }
    }

    public async Task<ObjectState> GetObjectStateAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        var path = ObjectStatePath(bucket, key);
        using var response = await SendAsync(HttpMethod.Get, $"{ItemPath(path)}:/content", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ObjectState();
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ObjectState>(JsonOptions, cancellationToken)
            ?? new ObjectState();
    }

    public async Task PutObjectStateAsync(
        string bucket,
        string key,
        ObjectState state,
        CancellationToken cancellationToken)
    {
        state = state with { Key = key };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        var path = ObjectStatePath(bucket, key);

        // Objects are only listed once their state exists, so retry transient failures
        // instead of leaving an uploaded object invisible to listings.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var content = new MemoryStream(bytes, writable: false);
                await UploadFileAsync(path, content, bytes.Length, requireExistingBucket: false, cancellationToken);
                return;
            }
            catch (GraphStoreException exception) when (attempt < StateWriteAttempts && IsTransient(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt * attempt), cancellationToken);
            }
        }
    }

    private static bool IsTransient(GraphStoreException exception) =>
        exception.StatusCode is 429 or >= 500;

    public Task DeleteObjectStateAsync(string bucket, string key, CancellationToken cancellationToken) =>
        DeletePathAsync(ObjectStatePath(bucket, key), ignoreMissing: true, cancellationToken);

    public async Task<MultipartUpload> CreateMultipartUploadAsync(
        string bucket,
        string key,
        ObjectState state,
        CancellationToken cancellationToken)
    {
        if (!await BucketExistsAsync(bucket, cancellationToken))
        {
            throw new GraphStoreException(404, "The specified bucket does not exist.", "NoSuchBucket");
        }

        var upload = new MultipartUpload(Guid.NewGuid().ToString("N"), bucket, key, state);
        await PutJsonAsync(MultipartManifestPath(bucket, upload.UploadId), upload, cancellationToken);
        return upload;
    }

    public async Task<UploadedPart> PutPartAsync(
        string bucket,
        string key,
        string uploadId,
        int partNumber,
        Stream content,
        long? length,
        CancellationToken cancellationToken)
    {
        await ValidateUploadAsync(bucket, key, uploadId, cancellationToken);
        if (length is null or <= 0)
        {
            throw new GraphStoreException(400, "Multipart parts must include a positive Content-Length.");
        }

        var storageName = $"{partNumber:D5}-{Guid.NewGuid():N}.bin";
        var hashingStream = new HashingReadStream(content);
        await UploadFileAsync(
            MultipartPartPath(bucket, uploadId, storageName),
            hashingStream,
            length.Value,
            requireExistingBucket: false,
            cancellationToken);
        var part = new UploadedPart(
            partNumber,
            length.Value,
            hashingStream.GetHash(),
            DateTimeOffset.UtcNow,
            storageName);
        await PutJsonAsync(MultipartPartStatePath(bucket, uploadId, partNumber), part, cancellationToken);
        if (!await BucketExistsAsync(bucket, cancellationToken))
        {
            await DeletePathAsync(MultipartUploadPath(bucket, uploadId), ignoreMissing: true, cancellationToken);
            throw new GraphStoreException(404, "The specified bucket does not exist.", "NoSuchBucket");
        }

        return part;
    }

    public async Task<IReadOnlyList<UploadedPart>> ListPartsAsync(
        string bucket,
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        await ValidateUploadAsync(bucket, key, uploadId, cancellationToken);
        var path = MultipartPartsPath(bucket, uploadId);
        var folder = await TryGetItemAsync(path, cancellationToken);
        if (folder is null)
        {
            return [];
        }

        var parts = new List<UploadedPart>();
        foreach (var item in await GetChildrenAsync(path, cancellationToken))
        {
            if (item.Folder is null && item.Name.EndsWith(".json", StringComparison.Ordinal))
            {
                var part = await GetJsonAsync<UploadedPart>($"{path}/{item.Name}", cancellationToken);
                parts.Add(part);
            }
        }

        return parts.OrderBy(part => part.PartNumber).ToList();
    }

    public async Task<ObjectInfo> CompleteMultipartUploadAsync(
        string bucket,
        string key,
        string uploadId,
        IReadOnlyList<UploadedPart> requestedParts,
        CancellationToken cancellationToken)
    {
        var upload = await ValidateUploadAsync(bucket, key, uploadId, cancellationToken);
        var available = (await ListPartsAsync(bucket, key, uploadId, cancellationToken))
            .ToDictionary(part => part.PartNumber);
        if (requestedParts.Count == 0)
        {
            throw new GraphStoreException(400, "At least one multipart part is required.");
        }

        var selected = new List<UploadedPart>(requestedParts.Count);
        for (var index = 0; index < requestedParts.Count; index++)
        {
            var requested = requestedParts[index];
            if (index > 0 && requested.PartNumber <= requestedParts[index - 1].PartNumber)
            {
                throw new GraphStoreException(
                    400,
                    "Multipart parts must be in strictly ascending order.",
                    "InvalidPartOrder");
            }

            if (!available.TryGetValue(requested.PartNumber, out var actual) ||
                !NormalizeEtag(actual.ETag).Equals(NormalizeEtag(requested.ETag), StringComparison.OrdinalIgnoreCase))
            {
                throw new GraphStoreException(
                    400,
                    $"Part {requested.PartNumber} is missing or its ETag does not match.",
                    "InvalidPart");
            }

            if (index < requestedParts.Count - 1 && actual.Size < MinimumMultipartPartSize)
            {
                throw new GraphStoreException(
                    400,
                    $"Part {requested.PartNumber} is smaller than 5 MiB.",
                    "EntityTooSmall");
            }

            selected.Add(actual);
        }

        var totalLength = selected.Sum(part => part.Size);
        if (totalLength > MaximumObjectSize)
        {
            throw new GraphStoreException(413, $"The completed object exceeds {_options.S3.MaxUploadSizeMiB} MiB.");
        }

        await using var combined = new MultipartReadStream(
            selected.Select(part => MultipartPartPath(
                bucket,
                uploadId,
                part.StorageName ?? throw new GraphStoreException(
                    500,
                    $"Stored state for part {part.PartNumber} has no data reference."))).ToList(),
            OpenPathDownloadAsync);
        var item = await UploadFileAsync(
            ObjectDataPath(bucket, key),
            combined,
            totalLength,
            requireExistingBucket: true,
            cancellationToken);
        await PutObjectStateAsync(bucket, key, upload.State, cancellationToken);
        if (await TryResolveLegacyObjectAsync(bucket, key, cancellationToken) is { } legacy)
        {
            await DeletePathAsync(legacy.Path, ignoreMissing: true, cancellationToken);
        }

        await DeletePathAsync(MultipartUploadPath(bucket, uploadId), ignoreMissing: true, cancellationToken);
        return ToObjectInfo(key, item);
    }

    public async Task AbortMultipartUploadAsync(
        string bucket,
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        await ValidateUploadAsync(bucket, key, uploadId, cancellationToken);
        await DeletePathAsync(MultipartUploadPath(bucket, uploadId), ignoreMissing: true, cancellationToken);
    }

    public async Task CheckReadyAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _readyUntil)
        {
            return;
        }

        await _readinessLock.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow < _readyUntil)
            {
                return;
            }

            using var driveResponse = await SendAsync(
                HttpMethod.Get,
                $"users/{Escape(_options.OneDrive.UserId)}/drive?$select=id",
                null,
                cancellationToken);
            await EnsureSuccessAsync(driveResponse, cancellationToken);

            var root = await TryGetItemAsync(_options.OneDrive.RootFolder, cancellationToken);
            if (root is null)
            {
                await CreateFolderAsync(
                    null,
                    _options.OneDrive.RootFolder,
                    allowExisting: true,
                    cancellationToken);
            }
            else if (root.Folder is null)
            {
                throw new GraphStoreException(500, "The configured OneDrive root is not a folder.");
            }

            _rootReady = true;
            _readyUntil = DateTimeOffset.UtcNow.AddSeconds(30);
        }
        finally
        {
            _readinessLock.Release();
        }
    }

    private long MaximumObjectSize => _options.S3.MaxUploadSizeMiB * 1024L * 1024L;

    private async Task EnsureBucketAndLengthAsync(
        string bucket,
        long? length,
        CancellationToken cancellationToken)
    {
        if (!await BucketExistsAsync(bucket, cancellationToken))
        {
            throw new GraphStoreException(404, "The specified bucket does not exist.", "NoSuchBucket");
        }

        if (length is null || length < 0 || length > MaximumObjectSize)
        {
            throw new GraphStoreException(
                413,
                $"Uploads must include Content-Length and be at most {_options.S3.MaxUploadSizeMiB} MiB.");
        }
    }

    private async Task<DriveItem> UploadFileAsync(
        string path,
        Stream content,
        long length,
        bool requireExistingBucket,
        CancellationToken cancellationToken)
    {
        if (length > MaximumObjectSize)
        {
            throw new GraphStoreException(413, $"The object exceeds {_options.S3.MaxUploadSizeMiB} MiB.");
        }

        await EnsureParentFoldersForPathAsync(path, requireExistingBucket, cancellationToken);
        if (length <= SimpleUploadLimit)
        {
            using var body = new StreamContent(content);
            body.Headers.ContentLength = length;
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await SendAsync(
                HttpMethod.Put,
                $"{ItemPath(path)}:/content",
                body,
                cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            return await response.Content.ReadFromJsonAsync<DriveItem>(cancellationToken)
                ?? throw new GraphStoreException(502, "Microsoft Graph returned an empty upload response.");
        }

        var fileName = path.Split('/').Last();
        using var sessionBody = JsonContent.Create(new Dictionary<string, object>
        {
            ["item"] = new Dictionary<string, object>
            {
                ["name"] = fileName,
                ["@microsoft.graph.conflictBehavior"] = "replace"
            }
        });
        using var sessionResponse = await SendAsync(
            HttpMethod.Post,
            $"{ItemPath(path)}:/createUploadSession",
            sessionBody,
            cancellationToken);
        await EnsureSuccessAsync(sessionResponse, cancellationToken);
        var session = await sessionResponse.Content.ReadFromJsonAsync<UploadSession>(cancellationToken)
            ?? throw new GraphStoreException(502, "Microsoft Graph returned an empty upload-session response.");
        if (string.IsNullOrWhiteSpace(session.UploadUrl))
        {
            throw new GraphStoreException(502, "Microsoft Graph did not return an upload URL.");
        }

        return await UploadChunksAsync(session.UploadUrl, content, length, cancellationToken);
    }

    private async Task<DriveItem> UploadChunksAsync(
        string uploadUrl,
        Stream content,
        long length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[UploadChunkSize];
        long offset = 0;
        DriveItem? completedItem = null;
        while (offset < length)
        {
            var required = (int)Math.Min(buffer.Length, length - offset);
            var count = await ReadExactlyUpToAsync(content, buffer, required, cancellationToken);
            if (count == 0)
            {
                throw new GraphStoreException(400, "The request body ended before Content-Length bytes were received.");
            }

            using var body = new ByteArrayContent(buffer, 0, count);
            body.Headers.ContentLength = count;
            body.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, length);
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = body };
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            offset += count;
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                completedItem = await response.Content.ReadFromJsonAsync<DriveItem>(cancellationToken);
            }
        }

        return completedItem
            ?? throw new GraphStoreException(502, "Microsoft Graph did not return the completed uploaded item.");
    }

    private async Task EnsureRootAsync(CancellationToken cancellationToken)
    {
        if (_rootReady)
        {
            return;
        }

        await _rootLock.WaitAsync(cancellationToken);
        try
        {
            if (_rootReady)
            {
                return;
            }

            var root = await TryGetItemAsync(_options.OneDrive.RootFolder, cancellationToken);
            if (root is null)
            {
                await CreateFolderAsync(null, _options.OneDrive.RootFolder, allowExisting: true, cancellationToken);
            }

            _rootReady = true;
        }
        finally
        {
            _rootLock.Release();
        }
    }

    private async Task EnsureParentFoldersForPathAsync(
        string path,
        bool requireExistingBucket,
        CancellationToken cancellationToken)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parent = segments[0];
        var startIndex = 1;
        if (requireExistingBucket)
        {
            if (segments.Length < 3)
            {
                throw new GraphStoreException(500, "An object path must include a bucket and object name.");
            }

            var bucketPath = $"{parent}/{segments[1]}";
            var bucket = await TryGetItemAsync(bucketPath, cancellationToken);
            if (bucket?.Folder is null)
            {
                throw new GraphStoreException(404, "The specified bucket does not exist.", "NoSuchBucket");
            }

            parent = bucketPath;
            startIndex = 2;
        }

        for (var index = startIndex; index < segments.Length - 1; index++)
        {
            var candidate = $"{parent}/{segments[index]}";
            if (await TryGetItemAsync(candidate, cancellationToken) is null)
            {
                await CreateFolderAsync(parent, segments[index], allowExisting: true, cancellationToken);
            }

            parent = candidate;
        }
    }

    private async Task CreateFolderAsync(
        string? parentPath,
        string name,
        bool allowExisting,
        CancellationToken cancellationToken)
    {
        var path = parentPath is null
            ? $"users/{Escape(_options.OneDrive.UserId)}/drive/root/children"
            : $"{ItemPath(parentPath)}:/children";
        using var body = JsonContent.Create(new Dictionary<string, object>
        {
            ["name"] = name,
            ["folder"] = new { },
            ["@microsoft.graph.conflictBehavior"] = "fail"
        });
        using var response = await SendAsync(HttpMethod.Post, path, body, cancellationToken);
        if (allowExisting && response.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<List<DriveItem>> GetChildrenAsync(string path, CancellationToken cancellationToken)
    {
        var items = new List<DriveItem>();
        string? next = $"{ItemPath(path)}:/children?$select=id,name,size,eTag,createdDateTime,lastModifiedDateTime,folder&$top=200";
        while (next is not null)
        {
            using var response = await SendAsync(HttpMethod.Get, next, null, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var page = await response.Content.ReadFromJsonAsync<DriveItemCollection>(cancellationToken)
                ?? throw new GraphStoreException(502, "Microsoft Graph returned an empty collection response.");
            items.AddRange(page.Value);
            next = page.NextLink;
        }

        return items;
    }

    private async Task<DriveItem> GetItemAsync(string path, CancellationToken cancellationToken) =>
        await TryGetItemAsync(path, cancellationToken)
        ?? throw new GraphStoreException(404, "The specified item does not exist.");

    private async Task<(string Path, DriveItem Item)> ResolveObjectAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        var dataPath = ObjectDataPath(bucket, key);
        var item = await TryGetItemAsync(dataPath, cancellationToken);
        if (item is not null && item.Folder is null)
        {
            return (dataPath, item);
        }

        if (await TryResolveLegacyObjectAsync(bucket, key, cancellationToken) is { } legacy)
        {
            return legacy;
        }

        throw new GraphStoreException(404, "The specified object does not exist.", "NoSuchKey");
    }

    private async Task<(string Path, DriveItem Item)?> TryResolveLegacyObjectAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        if (!IsLegacyPathCompatible(key))
        {
            return null;
        }

        var path = BucketPath(bucket);
        DriveItem? item = null;
        var segments = key.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            path = $"{path}/{segment}";
            item = await TryGetItemAsync(path, cancellationToken);
            if (item is null ||
                !item.Name.Equals(segment, StringComparison.Ordinal) ||
                index < segments.Length - 1 && item.Folder is null)
            {
                return null;
            }
        }

        return item is not null && item.Folder is null ? (path, item) : null;
    }

    private static bool IsLegacyPathCompatible(string key)
    {
        var segments = key.Split('/');
        return segments.All(segment =>
            segment.Length > 0 &&
            segment.Length <= 255 &&
            segment is not "." and not ".." &&
            !segment.EndsWith(' ') &&
            !segment.EndsWith('.') &&
            segment.IndexOfAny(['"', '*', ':', '<', '>', '?', '\\', '|']) < 0 &&
            segment.All(character => !char.IsControl(character)));
    }

    private async Task<List<DriveItem>> TryGetChildrenAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await GetChildrenAsync(path, cancellationToken);
        }
        catch (GraphStoreException exception) when (exception.StatusCode == 404)
        {
            return [];
        }
    }

    private async Task<DriveItem?> TryGetItemAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, ItemPath(path), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<DriveItem>(cancellationToken)
            ?? throw new GraphStoreException(502, "Microsoft Graph returned an empty item response.");
    }

    private async Task<HttpResponseMessage> OpenPathDownloadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Get,
            $"{ItemPath(path)}:/content",
            null,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await EnsureSuccessAsync(response, cancellationToken);
        }

        return response;
    }

    private async Task DeletePathAsync(
        string path,
        bool ignoreMissing,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, ItemPath(path), null, cancellationToken);
        if (ignoreMissing && response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task PutJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using var content = new MemoryStream(bytes, writable: false);
        await UploadFileAsync(
            path,
            content,
            bytes.Length,
            requireExistingBucket: false,
            cancellationToken);
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await OpenPathDownloadAsync(path, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            ?? throw new GraphStoreException(502, "Stored proxy state is empty.");
    }

    private async Task<MultipartUpload> ValidateUploadAsync(
        string bucket,
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        if (!await BucketExistsAsync(bucket, cancellationToken))
        {
            throw new GraphStoreException(404, "The specified bucket does not exist.", "NoSuchBucket");
        }

        MultipartUpload upload;
        try
        {
            upload = await GetJsonAsync<MultipartUpload>(
                MultipartManifestPath(bucket, uploadId),
                cancellationToken);
        }
        catch (GraphStoreException exception) when (exception.StatusCode == 404)
        {
            throw new GraphStoreException(
                404,
                "The specified multipart upload does not exist.",
                "NoSuchUpload");
        }

        if (upload.Bucket != bucket || upload.Key != key)
        {
            throw new GraphStoreException(
                404,
                "The specified multipart upload does not exist.",
                "NoSuchUpload");
        }

        return upload;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await tokenProvider.GetTokenAsync(cancellationToken));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var request = await CreateRequestAsync(method, path, content, cancellationToken);
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = string.IsNullOrWhiteSpace(detail)
            ? $"Microsoft Graph returned {(int)response.StatusCode}."
            : $"Microsoft Graph returned {(int)response.StatusCode}: {detail}";
        throw new GraphStoreException((int)response.StatusCode, message);
    }

    private string BucketPath(string bucket) =>
        OneDrivePathMapper.BucketPath(_options.OneDrive.RootFolder, bucket);

    private string ObjectDataPath(string bucket, string key) =>
        OneDrivePathMapper.ObjectDataPath(_options.OneDrive.RootFolder, bucket, key);

    private string StatePath(params string[] segments) =>
        $"{_options.OneDrive.RootFolder}/{StateFolder}/{string.Join('/', segments)}";

    private string ObjectStatePath(string bucket, string key)
    {
        var hash = OneDrivePathMapper.Hash(key);
        return StatePath("objects", bucket, hash[..2], $"{hash}.json");
    }

    private string MultipartBucketPath(string bucket) =>
        StatePath("multipart", bucket);

    private string MultipartUploadPath(string bucket, string uploadId) =>
        $"{MultipartBucketPath(bucket)}/{uploadId}";

    private string MultipartManifestPath(string bucket, string uploadId) =>
        $"{MultipartUploadPath(bucket, uploadId)}/manifest.json";

    private string MultipartPartsPath(string bucket, string uploadId) =>
        $"{MultipartUploadPath(bucket, uploadId)}/parts";

    private string MultipartPartPath(string bucket, string uploadId, string storageName) =>
        $"{MultipartPartsPath(bucket, uploadId)}/{storageName}";

    private string MultipartPartStatePath(string bucket, string uploadId, int partNumber) =>
        $"{MultipartPartsPath(bucket, uploadId)}/{partNumber:D5}.json";

    private string ItemPath(string path) =>
        $"users/{Escape(_options.OneDrive.UserId)}/drive/root:/{EncodePath(path)}";

    private static string EncodePath(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Escape));

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string NormalizeEtag(string etag) => etag.Trim().Trim('"');

    private static ObjectInfo ToObjectInfo(string key, DriveItem item) =>
        new(key, item.Size, item.LastModifiedDateTime, item.ETag);

    private static async Task<int> ReadExactlyUpToAsync(
        Stream stream,
        byte[] buffer,
        int count,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private sealed class HashingReadStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        private byte[]? _completedHash;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public string GetHash()
        {
            if (_completedHash is null)
            {
                _completedHash = _hash.GetHashAndReset();
                _hash.Dispose();
            }

            return Convert.ToHexString(_completedHash).ToLowerInvariant();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            _hash.AppendData(buffer, offset, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            _hash.AppendData(buffer.Span[..read]);
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing) => base.Dispose(disposing);
    }

    private sealed class MultipartReadStream(
        IReadOnlyList<string> paths,
        Func<string, CancellationToken, Task<HttpResponseMessage>> open) : Stream
    {
        private int _index;
        private HttpResponseMessage? _response;
        private Stream? _stream;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_stream is null)
                {
                    if (_index >= paths.Count)
                    {
                        return 0;
                    }

                    _response = await open(paths[_index++], cancellationToken);
                    _stream = await _response.Content.ReadAsStreamAsync(cancellationToken);
                }

                var read = await _stream.ReadAsync(buffer, cancellationToken);
                if (read != 0)
                {
                    return read;
                }

                await DisposeCurrentAsync();
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            await DisposeCurrentAsync();
            GC.SuppressFinalize(this);
        }

        private async ValueTask DisposeCurrentAsync()
        {
            if (_stream is not null)
            {
                await _stream.DisposeAsync();
                _stream = null;
            }

            _response?.Dispose();
            _response = null;
        }
    }
}
