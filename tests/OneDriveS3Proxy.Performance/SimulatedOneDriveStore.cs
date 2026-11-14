using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using OneDriveS3Proxy.Graph;

namespace OneDriveS3Proxy.Performance;

internal sealed class SimulatedOneDriveStore(IReadOnlyDictionary<long, PayloadDigest> digests) : IOneDriveStore
{
    public const string Bucket = "performance";
    private readonly ConcurrentDictionary<string, StoredObject> _objects = new(StringComparer.Ordinal);

    /// <summary>Makes downloads advertise an ETag that does not match the payload, to exercise failure reporting.</summary>
    internal bool CorruptDownloadETags { get; set; }

    public async Task<ObjectInfo> PutObjectAsync(
        string bucket, string key, Stream content, long? length, CancellationToken cancellationToken)
    {
        RequireBucket(bucket);
        if (length is null || !digests.TryGetValue(length.Value, out var expected))
        {
            throw new InvalidOperationException("The simulated store requires a configured payload size.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        long received = 0;
        try
        {
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(0, 64 * 1024), cancellationToken)) != 0)
            {
                md5.AppendData(buffer, 0, read);
                received += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var etag = Convert.ToHexStringLower(md5.GetHashAndReset());
        if (received != length || etag != expected.Md5)
        {
            throw new GraphStoreException(400, "The simulated store accepts only the generated test payload.");
        }

        var info = new ObjectInfo(key, received, DateTimeOffset.UtcNow, etag) { StorageETag = etag };
        // Retain descriptors, not payloads: even 32 concurrent 1 GiB objects stay memory-bounded.
        _objects[key] = new StoredObject(info, new ObjectState());
        return info;
    }

    public Task<GraphDownload> GetObjectAsync(
        string bucket, string key, string? range, CancellationToken cancellationToken)
    {
        if (range is not null)
        {
            throw new NotSupportedException("The performance workload uses full-object downloads.");
        }

        var stored = Get(bucket, key);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new PatternStream(stored.Info.Size), 64 * 1024)
        };
        response.Content.Headers.ContentLength = stored.Info.Size;
        var info = CorruptDownloadETags ? stored.Info with { ETag = new string('0', 32) } : stored.Info;
        return Task.FromResult(new GraphDownload(response, info));
    }

    public Task<ObjectInfo> GetObjectInfoAsync(string bucket, string key, CancellationToken cancellationToken) =>
        Task.FromResult(Get(bucket, key).Info);

    public Task<ObjectState> GetObjectStateAsync(string bucket, string key, CancellationToken cancellationToken) =>
        Task.FromResult(Get(bucket, key).State);

    public Task PutObjectStateAsync(string bucket, string key, ObjectState state, CancellationToken cancellationToken)
    {
        _objects[key] = Get(bucket, key) with { State = state };
        return Task.CompletedTask;
    }

    public Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        RequireBucket(bucket);
        _objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken) =>
        Task.FromResult(bucket == Bucket);

    public Task CheckReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken) =>
        throw Unsupported();
    public Task CreateBucketAsync(string bucket, CancellationToken cancellationToken) => throw Unsupported();
    public Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken) => throw Unsupported();
    public Task<IReadOnlyList<ObjectInfo>> ListObjectsAsync(string bucket, CancellationToken cancellationToken) =>
        throw Unsupported();
    public Task<ObjectListing> ListObjectsAsync(string bucket, string prefix, string? delimiter, CancellationToken cancellationToken) =>
        throw Unsupported();
    public Task<ObjectInfo> CopyObjectAsync(string sourceBucket, string sourceKey, string destinationBucket, string destinationKey, CancellationToken cancellationToken) =>
        throw Unsupported();
    public Task DeleteObjectStateAsync(string bucket, string key, CancellationToken cancellationToken) => throw Unsupported();
    public Task<MultipartUpload> CreateMultipartUploadAsync(string bucket, string key, ObjectState state, CancellationToken cancellationToken) =>
        throw Unsupported();
    public Task<UploadedPart> PutPartAsync(string bucket, string key, string uploadId, int partNumber, Stream content, long? length, CancellationToken cancellationToken) =>
        throw Unsupported();
    public Task<IReadOnlyList<UploadedPart>> ListPartsAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken) =>
        throw Unsupported();
    public Task<ObjectInfo> CompleteMultipartUploadAsync(string bucket, string key, string uploadId, IReadOnlyList<UploadedPart> parts, CancellationToken cancellationToken) =>
        throw Unsupported();
    public Task AbortMultipartUploadAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken) =>
        throw Unsupported();

    private StoredObject Get(string bucket, string key)
    {
        RequireBucket(bucket);
        return _objects.TryGetValue(key, out var stored)
            ? stored
            : throw new GraphStoreException(404, "Object not found.");
    }

    private static void RequireBucket(string bucket)
    {
        if (bucket != Bucket)
        {
            throw new GraphStoreException(404, "Bucket not found.");
        }
    }

    private static NotSupportedException Unsupported() =>
        new("The simulated backend supports only single-part PUT, GET, HEAD, and DELETE.");

    private sealed record StoredObject(ObjectInfo Info, ObjectState State);
}
