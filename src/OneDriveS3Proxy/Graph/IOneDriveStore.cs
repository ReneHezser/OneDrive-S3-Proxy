namespace OneDriveS3Proxy.Graph;

public interface IOneDriveStore
{
    Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken);
    Task CreateBucketAsync(string bucket, CancellationToken cancellationToken);
    Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken);
    Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken);
    Task<IReadOnlyList<ObjectInfo>> ListObjectsAsync(string bucket, CancellationToken cancellationToken);
    Task<ObjectInfo> PutObjectAsync(string bucket, string key, Stream content, long? length, CancellationToken cancellationToken);
    Task<GraphDownload> GetObjectAsync(string bucket, string key, string? range, CancellationToken cancellationToken);
    Task<ObjectInfo> GetObjectInfoAsync(string bucket, string key, CancellationToken cancellationToken);
    Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken);
    Task<ObjectInfo> CopyObjectAsync(string sourceBucket, string sourceKey, string destinationBucket, string destinationKey, CancellationToken cancellationToken);
    Task<ObjectState> GetObjectStateAsync(string bucket, string key, CancellationToken cancellationToken);
    Task PutObjectStateAsync(string bucket, string key, ObjectState state, CancellationToken cancellationToken);
    Task DeleteObjectStateAsync(string bucket, string key, CancellationToken cancellationToken);
    Task<MultipartUpload> CreateMultipartUploadAsync(string bucket, string key, ObjectState state, CancellationToken cancellationToken);
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
