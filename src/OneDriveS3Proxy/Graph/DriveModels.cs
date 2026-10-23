using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneDriveS3Proxy.Graph;

public sealed record BucketInfo(string Name, DateTimeOffset Created);

public sealed record ObjectInfo(string Key, long Size, DateTimeOffset LastModified, string ETag);

public sealed record ObjectState
{
    public string? Key { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Tags { get; init; } = new(StringComparer.Ordinal);
}

public sealed record MultipartUpload(string UploadId, string Bucket, string Key, ObjectState State);

public sealed record UploadedPart(
    int PartNumber,
    long Size,
    string ETag,
    DateTimeOffset LastModified,
    string? StorageName = null);

internal sealed class DriveItemCollection
{
    [JsonPropertyName("value")]
    public List<DriveItem> Value { get; init; } = [];

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; init; }
}

internal sealed class DriveItem
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("eTag")]
    public string ETag { get; init; } = string.Empty;

    [JsonPropertyName("createdDateTime")]
    public DateTimeOffset CreatedDateTime { get; init; }

    [JsonPropertyName("lastModifiedDateTime")]
    public DateTimeOffset LastModifiedDateTime { get; init; }

    [JsonPropertyName("folder")]
    public FolderFacet? Folder { get; init; }
}

internal sealed class FolderFacet
{
    [JsonPropertyName("childCount")]
    public int ChildCount { get; init; }
}

internal sealed class UploadSession
{
    [JsonPropertyName("uploadUrl")]
    public string UploadUrl { get; init; } = string.Empty;
}

internal sealed class AsyncJobStatus
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("resourceId")]
    public string? ResourceId { get; init; }

    [JsonPropertyName("error")]
    public JsonElement? Error { get; init; }
}
