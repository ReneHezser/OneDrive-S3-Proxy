namespace OneDriveS3Proxy.Graph;

public interface IOneDriveStore
{
    Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken);
    Task CreateBucketAsync(string bucket, CancellationToken cancellationToken);
    Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken);
    Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken);
    Task<IReadOnlyList<ObjectInfo>> ListObjectsAsync(string bucket, CancellationToken cancellationToken);
    Task<ObjectListing> ListObjectsAsync(string bucket, string prefix, string? delimiter, CancellationToken cancellationToken);
    /// <summary>Uploads an object from the current position of <paramref name="content"/>.</summary>
    /// <remarks>
    /// The caller retains ownership of the stream and must keep it open until the task completes.
    /// The stream remains open after success, failure, or cancellation; its position may advance in all cases.
    /// The returned ETag is the content MD5. The caller must persist it and StorageETag with
    /// the request metadata using PutObjectStateAsync before acknowledging the upload.
    /// </remarks>
    Task<ObjectInfo> PutObjectAsync(string bucket, string key, Stream content, long? length, CancellationToken cancellationToken);

    /// <summary>Opens a download whose response is owned by the returned <see cref="GraphDownload"/>.</summary>
    /// <remarks>The caller must dispose the download after consuming its content.</remarks>
    Task<GraphDownload> GetObjectAsync(string bucket, string key, string? range, CancellationToken cancellationToken);
    Task<ObjectInfo> GetObjectInfoAsync(string bucket, string key, CancellationToken cancellationToken);
    /// <summary>Deletes an object and its state, then removes empty parent folders below the bucket.</summary>
    /// <remarks>Writers must not target the same payload or metadata subtree during cleanup.</remarks>
    Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken);
    Task<ObjectInfo> CopyObjectAsync(string sourceBucket, string sourceKey, string destinationBucket, string destinationKey, CancellationToken cancellationToken);
    Task<ObjectState> GetObjectStateAsync(string bucket, string key, CancellationToken cancellationToken);
    Task PutObjectStateAsync(string bucket, string key, ObjectState state, CancellationToken cancellationToken);
    /// <summary>Deletes object metadata and prunes empty shard and bucket folders below the objects state root.</summary>
    Task DeleteObjectStateAsync(string bucket, string key, CancellationToken cancellationToken);
    Task<MultipartUpload> CreateMultipartUploadAsync(string bucket, string key, ObjectState state, CancellationToken cancellationToken);
    /// <summary>Uploads a multipart part from the current position of <paramref name="content"/>.</summary>
    /// <remarks>
    /// The caller retains ownership of the stream and must keep it open until the task completes.
    /// The stream remains open after success, failure, or cancellation; its position may advance in all cases.
    /// </remarks>
    Task<UploadedPart> PutPartAsync(string bucket, string key, string uploadId, int partNumber, Stream content, long? length, CancellationToken cancellationToken);
    Task<IReadOnlyList<UploadedPart>> ListPartsAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken);
    Task<ObjectInfo> CompleteMultipartUploadAsync(string bucket, string key, string uploadId, IReadOnlyList<UploadedPart> parts, CancellationToken cancellationToken);
    Task AbortMultipartUploadAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken);
    Task CheckReadyAsync(CancellationToken cancellationToken);
}

public sealed class GraphDownload(HttpResponseMessage response, ObjectInfo info) : IAsyncDisposable
{
    public HttpResponseMessage Response { get; } = response;
    public ObjectInfo Info { get; } = info;

    public ValueTask DisposeAsync()
    {
        Response.Dispose();
        return ValueTask.CompletedTask;
    }
}
