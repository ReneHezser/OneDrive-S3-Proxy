using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using OneDriveS3Proxy.Graph;

namespace OneDriveS3Proxy.S3;

public static partial class S3Endpoints
{
    private static readonly XNamespace S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    // Query sub-resources select a different S3 operation. Anything outside an operation's
    // supported set is rejected so it cannot silently run as a plain bucket/object operation.
    private static readonly HashSet<string> SubresourceNames = new(StringComparer.Ordinal)
    {
        "accelerate", "acl", "analytics", "attributes", "cors", "delete", "encryption",
        "intelligent-tiering", "inventory", "legal-hold", "lifecycle", "location", "logging",
        "metrics", "notification", "object-lock", "ownershipControls", "partNumber", "policy",
        "policyStatus", "publicAccessBlock", "renameObject", "replication", "requestPayment",
        "restore", "retention", "select", "session", "tagging", "torrent", "uploadId", "uploads",
        "versionId", "versioning", "versions", "website"
    };

    private static void RequireSupportedSubresources(HttpRequest request, params string[] supported)
    {
        foreach (var name in request.Query.Keys)
        {
            if (SubresourceNames.Contains(name) && Array.IndexOf(supported, name) < 0)
            {
                throw new S3Exception(501, "NotImplemented", $"The '{name}' sub-resource is not supported.");
            }
        }
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/", ListBucketsAsync);
        app.MapPut("/{bucket}", CreateBucketAsync);
        app.MapMethods("/{bucket}", [HttpMethods.Head], HeadBucketAsync);
        app.MapDelete("/{bucket}", DeleteBucketAsync);
        app.MapGet("/{bucket}", ListObjectsAsync);
        app.MapPost("/{bucket}", DeleteObjectsAsync);

        app.MapPut("/{bucket}/{**key}", PutObjectAsync);
        app.MapGet("/{bucket}/{**key}", GetObjectAsync);
        app.MapPost("/{bucket}/{**key}", PostObjectAsync);
        app.MapMethods("/{bucket}/{**key}", [HttpMethods.Head], HeadObjectAsync);
        app.MapDelete("/{bucket}/{**key}", DeleteObjectAsync);
    }

    private static async Task<IResult> ListBucketsAsync(IOneDriveStore store, CancellationToken cancellationToken)
    {
        var buckets = await store.ListBucketsAsync(cancellationToken);
        return Xml(new XElement(S3Namespace + "ListAllMyBucketsResult",
            new XElement(S3Namespace + "Owner",
                new XElement(S3Namespace + "ID", "onedrive"),
                new XElement(S3Namespace + "DisplayName", "OneDrive for Business")),
            new XElement(S3Namespace + "Buckets",
                buckets.Select(bucket => new XElement(S3Namespace + "Bucket",
                    new XElement(S3Namespace + "Name", bucket.Name),
                    new XElement(S3Namespace + "CreationDate", S3Date(bucket.Created)))))));
    }

    private static async Task<IResult> CreateBucketAsync(
        string bucket,
        HttpRequest request,
        IOneDriveStore store,
        CancellationToken cancellationToken)
    {
        RequireSupportedSubresources(request);
        ValidateBucket(bucket);
        if (await store.BucketExistsAsync(bucket, cancellationToken))
        {
            throw new S3Exception(409, "BucketAlreadyOwnedByYou", "The bucket already exists.");
        }

        await store.CreateBucketAsync(bucket, cancellationToken);
        return Results.StatusCode(StatusCodes.Status200OK);
    }

    private static async Task<IResult> HeadBucketAsync(
        string bucket,
        HttpRequest request,
        IOneDriveStore store,
        CancellationToken cancellationToken)
    {
        RequireSupportedSubresources(request);
        ValidateBucket(bucket);
        return await store.BucketExistsAsync(bucket, cancellationToken)
            ? Results.Ok()
            : throw new S3Exception(404, "NoSuchBucket", "The specified bucket does not exist.");
    }

    private static async Task<IResult> DeleteBucketAsync(
        string bucket,
        HttpRequest request,
        IOneDriveStore store,
        CancellationToken cancellationToken)
    {
        RequireSupportedSubresources(request);
        ValidateBucket(bucket);
        if (!await store.BucketExistsAsync(bucket, cancellationToken))
        {
            throw new S3Exception(404, "NoSuchBucket", "The specified bucket does not exist.");
        }

        await store.DeleteBucketAsync(bucket, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ListObjectsAsync(
        string bucket,
        HttpRequest request,
        IOneDriveStore store,
        IOptions<ProxyOptions> options,
        CancellationToken cancellationToken)
    {
        RequireSupportedSubresources(request, "location");
        ValidateBucket(bucket);
        if (!await store.BucketExistsAsync(bucket, cancellationToken))
        {
            throw new S3Exception(404, "NoSuchBucket", "The specified bucket does not exist.");
        }

        if (request.Query.ContainsKey("location"))
        {
            var region = options.Value.S3.Region == "us-east-1" ? null : options.Value.S3.Region;
            return Xml(new XElement(S3Namespace + "LocationConstraint", region));
        }

        var listType = request.Query["list-type"].FirstOrDefault();
        if (listType is not null && listType != "2")
        {
            throw new S3Exception(400, "InvalidArgument", "list-type must be 2.");
        }

        var token = request.Query["continuation-token"].FirstOrDefault();
        var isV2 = listType == "2" || token is not null;
        var prefix = request.Query["prefix"].FirstOrDefault() ?? string.Empty;
        var delimiter = request.Query["delimiter"].FirstOrDefault();
        var maxKeys = ParseMaxKeys(request.Query["max-keys"].FirstOrDefault());
        var marker = request.Query["marker"].FirstOrDefault();
        var startAfter = request.Query["start-after"].FirstOrDefault();
        var continuation = DecodeContinuation(token);
        var lowerBound = isV2
            ? MaxKey(continuation, startAfter)
            : marker;
        var listing = maxKeys == 0
            ? new ObjectListing([], [])
            : await store.ListObjectsAsync(bucket, prefix, delimiter, cancellationToken);

        var entries = listing.Objects.Select(item => new ListEntry(item.Key, item, null))
            .Concat(listing.CommonPrefixes.Select(item => new ListEntry(item, null, item)))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Where(item => lowerBound is null || string.CompareOrdinal(item.Key, lowerBound) > 0)
            .ToList();
        var page = maxKeys == 0 ? [] : entries.Take(maxKeys).ToList();
        var truncated = page.Count > 0 && entries.Count > page.Count;
        var lastKey = truncated ? page[^1].Key : null;

        return Xml(new XElement(S3Namespace + "ListBucketResult",
            new XElement(S3Namespace + "Name", bucket),
            new XElement(S3Namespace + "Prefix", prefix),
            isV2 ? null : new XElement(S3Namespace + "Marker", marker ?? string.Empty),
            delimiter is null ? null : new XElement(S3Namespace + "Delimiter", delimiter),
            new XElement(S3Namespace + "KeyCount", page.Count),
            new XElement(S3Namespace + "MaxKeys", maxKeys),
            new XElement(S3Namespace + "IsTruncated", truncated.ToString().ToLowerInvariant()),
            isV2 && token is not null ? new XElement(S3Namespace + "ContinuationToken", token) : null,
            isV2 && startAfter is not null ? new XElement(S3Namespace + "StartAfter", startAfter) : null,
            lastKey is null
                ? null
                : isV2
                    ? new XElement(S3Namespace + "NextContinuationToken", EncodeContinuation(lastKey))
                    : new XElement(S3Namespace + "NextMarker", lastKey),
            page.Select(entry => entry.Object is not null
                ? new XElement(S3Namespace + "Contents",
                    new XElement(S3Namespace + "Key", entry.Object.Key),
                    new XElement(S3Namespace + "LastModified", S3Date(entry.Object.LastModified)),
                    new XElement(S3Namespace + "ETag", QuoteEtag(entry.Object.ETag)),
                    new XElement(S3Namespace + "Size", entry.Object.Size),
                    new XElement(S3Namespace + "StorageClass", "STANDARD"))
                : new XElement(S3Namespace + "CommonPrefixes",
                    new XElement(S3Namespace + "Prefix", entry.Prefix)))));
    }

    private static string? MaxKey(string? first, string? second) =>
        first is null ? second
        : second is null ? first
        : string.CompareOrdinal(first, second) >= 0 ? first : second;

    private static async Task<IResult> DeleteObjectsAsync(
        string bucket,
        HttpRequest request,
        IOneDriveStore store,
        CancellationToken cancellationToken)
    {
        ValidateBucket(bucket);
        if (!request.Query.ContainsKey("delete"))
        {
            throw new S3Exception(501, "NotImplemented", "The requested bucket operation is not supported.");
        }

        RequireSupportedSubresources(request, "delete");
        if (!await store.BucketExistsAsync(bucket, cancellationToken))
        {
            throw new S3Exception(404, "NoSuchBucket", "The specified bucket does not exist.");
        }

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(request.Body, new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = 1024 * 1024
            });
            document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        }
        catch (XmlException)
        {
            throw new S3Exception(400, "MalformedXML", "The XML request body is malformed.");
        }

        var keys = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Object")
            .Select(element =>
            {
                if (element.Elements().Any(child => child.Name.LocalName == "VersionId"))
                {
                    throw new S3Exception(501, "NotImplemented", "Versioned deletes are not supported.");
                }

                return element.Elements().FirstOrDefault(child => child.Name.LocalName == "Key")?.Value
                    ?? throw new S3Exception(400, "MalformedXML", "Each object must include a key.");
            })
            .ToList();
        if (keys.Count is 0 or > 1000)
        {
            throw new S3Exception(400, "MalformedXML", "Delete requests must contain between 1 and 1000 object keys.");
        }

        foreach (var key in keys)
        {
            ValidateKey(key);
        }

        var quiet = string.Equals(
            document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Quiet")?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            await store.DeleteObjectAsync(bucket, key, cancellationToken);
        }

        return Xml(new XElement(S3Namespace + "DeleteResult",
            quiet
                ? null
                : keys.Select(key => new XElement(S3Namespace + "Deleted",
                    new XElement(S3Namespace + "Key", key)))));
    }

    private static async Task<IResult> PutObjectAsync(
        string bucket,
        string key,
        HttpRequest request,
        HttpResponse response,
        IOneDriveStore store,
        CancellationToken cancellationToken)
    {
        RequireSupportedSubresources(request, "tagging", "uploadId", "partNumber");
        ValidateBucket(bucket);
        ValidateKey(key);

        if (request.Query.ContainsKey("partNumber") && !request.Query.ContainsKey("uploadId"))
        {
            throw new S3Exception(400, "InvalidArgument", "partNumber requires uploadId.");
        }

        if (request.Query.ContainsKey("tagging"))
        {
            await store.GetObjectInfoAsync(bucket, key, cancellationToken);
            var tags = ParseTaggingDocument(await ReadXmlAsync(request, cancellationToken));
            var state = await store.GetObjectStateAsync(bucket, key, cancellationToken);
            await store.PutObjectStateAsync(bucket, key, state with { Tags = tags }, cancellationToken);
            return Results.Ok();
        }

        if (request.Query.TryGetValue("uploadId", out var uploadId))
        {
            if (request.Headers.ContainsKey("x-amz-copy-source"))
            {
                throw new S3Exception(501, "NotImplemented", "UploadPartCopy is not supported.");
            }
            var partNumber = ParsePartNumber(request.Query["partNumber"].FirstOrDefault());
            var part = await store.PutPartAsync(
                bucket,
                key,
                uploadId.ToString(),
                partNumber,
                request.Body,
                request.ContentLength,
                cancellationToken);
            response.Headers.ETag = QuoteEtag(part.ETag);
            return Results.Ok();
        }

        if (request.Headers.TryGetValue("x-amz-copy-source", out var copySource))
        {
            var (sourceBucket, sourceKey) = ParseCopySource(copySource.ToString());
            var sourceState = await store.GetObjectStateAsync(sourceBucket, sourceKey, cancellationToken);
            var state = string.Equals(
                request.Headers["x-amz-metadata-directive"].FirstOrDefault(),
                "REPLACE",
                StringComparison.OrdinalIgnoreCase)
                ? CaptureObjectState(request) with { Tags = sourceState.Tags }
                : sourceState;
            if (string.Equals(
                    request.Headers["x-amz-tagging-directive"].FirstOrDefault(),
                    "REPLACE",
                    StringComparison.OrdinalIgnoreCase))
            {
                state = state with { Tags = ParseTagHeader(request.Headers["x-amz-tagging"].FirstOrDefault()) };
            }

            var copied = sourceBucket == bucket && sourceKey == key
                ? await store.GetObjectInfoAsync(bucket, key, cancellationToken)
                : await store.CopyObjectAsync(
                    sourceBucket,
                    sourceKey,
                    bucket,
                    key,
                    cancellationToken);
            await store.PutObjectStateAsync(bucket, key, state with
            {
                ETag = copied.ETag,
                StorageETag = copied.StorageETag
            }, cancellationToken);
            return Xml(new XElement(S3Namespace + "CopyObjectResult",
                new XElement(S3Namespace + "LastModified", S3Date(copied.LastModified)),
                new XElement(S3Namespace + "ETag", QuoteEtag(copied.ETag))));
        }

        var objectState = CaptureObjectState(request);
        var item = await store.PutObjectAsync(bucket, key, request.Body, request.ContentLength, cancellationToken);
        await store.PutObjectStateAsync(bucket, key, objectState with
        {
            ETag = item.ETag,
            StorageETag = item.StorageETag
        }, cancellationToken);
        response.Headers.ETag = QuoteEtag(item.ETag);
        return Results.Ok();
    }

    private static async Task<IResult> GetObjectAsync(
        string bucket,
        string key,
        HttpRequest request,
        HttpResponse response,
        IOneDriveStore store,
        CancellationToken cancellationToken)
    {
        RequireSupportedSubresources(request, "tagging", "uploadId");
        ValidateBucket(bucket);
        ValidateKey(key);

        if (request.Query.ContainsKey("tagging"))
        {
            await store.GetObjectInfoAsync(bucket, key, cancellationToken);
            var state = await store.GetObjectStateAsync(bucket, key, cancellationToken);
            return Xml(TaggingElement(state.Tags));
        }

        if (request.Query.TryGetValue("uploadId", out var uploadId))
        {
            var parts = await store.ListPartsAsync(bucket, key, uploadId.ToString(), cancellationToken);
            return Xml(new XElement(S3Namespace + "ListPartsResult",
                new XElement(S3Namespace + "Bucket", bucket),
                new XElement(S3Namespace + "Key", key),
                new XElement(S3Namespace + "UploadId", uploadId.ToString()),
                parts.Select(part => new XElement(S3Namespace + "Part",
                    new XElement(S3Namespace + "PartNumber", part.PartNumber),
                    new XElement(S3Namespace + "LastModified", S3Date(part.LastModified)),
                    new XElement(S3Namespace + "ETag", QuoteEtag(part.ETag)),
                    new XElement(S3Namespace + "Size", part.Size)))));
        }

        var range = request.Headers.Range.FirstOrDefault();
        if (range?.Contains(',', StringComparison.Ordinal) == true)
        {
            throw new S3Exception(416, "InvalidRange", "Multiple byte ranges are not supported.");
        }

        var stateForGet = await store.GetObjectStateAsync(bucket, key, cancellationToken);
        await using var download = await store.GetObjectAsync(bucket, key, range, cancellationToken);
        response.StatusCode = (int)download.Response.StatusCode;
        ApplyObjectState(response, stateForGet);
        response.ContentLength = download.Response.Content.Headers.ContentLength ?? download.Info.Size;
        response.Headers.ETag = QuoteEtag(download.Info.ETag);
        response.Headers.LastModified = download.Info.LastModified.ToString("R");
        response.Headers.AcceptRanges = "bytes";
        if (download.Response.Content.Headers.ContentRange is not null)
        {
            response.Headers.ContentRange = download.Response.Content.Headers.ContentRange.ToString();
        }

        await download.Response.Content.CopyToAsync(response.Body, cancellationToken);
        return Results.Empty;
    }

    private static async Task<IResult> HeadObjectAsync(
        string bucket,
        string key,
        HttpRequest request,
        HttpResponse response,
        IOneDriveStore store,
        CancellationToken cancellationToken)
    {
        RequireSupportedSubresources(request);
        ValidateBucket(bucket);
        ValidateKey(key);
        var item = await store.GetObjectInfoAsync(bucket, key, cancellationToken);
        var state = await store.GetObjectStateAsync(bucket, key, cancellationToken);
        response.ContentLength = item.Size;
        ApplyObjectState(response, state);
        response.Headers.ETag = QuoteEtag(item.ETag);
        response.Headers.LastModified = item.LastModified.ToString("R");
        response.Headers.AcceptRanges = "bytes";
        return Results.Ok();
    }

    private static async Task<IResult> DeleteObjectAsync(
        string bucket,
        string key,
        HttpRequest request,
        IOneDriveStore store,
        CancellationToken cancellationToken)
    {
        RequireSupportedSubresources(request, "tagging", "uploadId");
        ValidateBucket(bucket);
        ValidateKey(key);

        if (request.Query.ContainsKey("tagging"))
        {
            await store.GetObjectInfoAsync(bucket, key, cancellationToken);
            var state = await store.GetObjectStateAsync(bucket, key, cancellationToken);
            await store.PutObjectStateAsync(bucket, key, state with { Tags = [] }, cancellationToken);
            return Results.NoContent();
        }

        if (request.Query.TryGetValue("uploadId", out var uploadId))
        {
            await store.AbortMultipartUploadAsync(bucket, key, uploadId.ToString(), cancellationToken);
            return Results.NoContent();
        }

        await store.DeleteObjectAsync(bucket, key, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> PostObjectAsync(
        string bucket,
        string key,
        HttpRequest request,
        IOneDriveStore store,
        CancellationToken cancellationToken)
    {
        RequireSupportedSubresources(request, "uploads", "uploadId");
        ValidateBucket(bucket);
        ValidateKey(key);
        if (request.Query.ContainsKey("uploads"))
        {
            var upload = await store.CreateMultipartUploadAsync(
                bucket,
                key,
                CaptureObjectState(request),
                cancellationToken);
            return Xml(new XElement(S3Namespace + "InitiateMultipartUploadResult",
                new XElement(S3Namespace + "Bucket", bucket),
                new XElement(S3Namespace + "Key", key),
                new XElement(S3Namespace + "UploadId", upload.UploadId)));
        }

        if (request.Query.TryGetValue("uploadId", out var uploadId))
        {
            var document = await ReadXmlAsync(request, cancellationToken);
            var requestedParts = document
                .Descendants()
                .Where(element => element.Name.LocalName == "Part")
                .Select(element => new UploadedPart(
                    ParsePartNumber(element.Elements().FirstOrDefault(child => child.Name.LocalName == "PartNumber")?.Value),
                    0,
                    element.Elements().FirstOrDefault(child => child.Name.LocalName == "ETag")?.Value
                        ?? throw new S3Exception(400, "MalformedXML", "Each part must include an ETag."),
                    default))
                .ToList();
            var item = await store.CompleteMultipartUploadAsync(
                bucket,
                key,
                uploadId.ToString(),
                requestedParts,
                cancellationToken);
            return Xml(new XElement(S3Namespace + "CompleteMultipartUploadResult",
                new XElement(S3Namespace + "Location", $"/{bucket}/{key}"),
                new XElement(S3Namespace + "Bucket", bucket),
                new XElement(S3Namespace + "Key", key),
                new XElement(S3Namespace + "ETag", QuoteEtag(item.ETag))));
        }

        throw new S3Exception(501, "NotImplemented", "The requested object operation is not supported.");
    }

    private static IResult Xml(XElement root) =>
        Results.Text(new XDocument(root).ToString(SaveOptions.DisableFormatting), "application/xml", Encoding.UTF8);

    private static ObjectState CaptureObjectState(HttpRequest request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "Content-Encoding", "Content-Disposition", "Cache-Control", "Expires" })
        {
            if (request.Headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                headers[name] = value.ToString();
            }
        }

        var metadata = request.Headers
            .Where(header => header.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                header => header.Key["x-amz-meta-".Length..],
                header => header.Value.ToString(),
                StringComparer.OrdinalIgnoreCase);
        return new ObjectState
        {
            ContentType = request.ContentType ?? "application/octet-stream",
            Headers = headers,
            Metadata = metadata,
            Tags = ParseTagHeader(request.Headers["x-amz-tagging"].FirstOrDefault())
        };
    }

    private static void ApplyObjectState(HttpResponse response, ObjectState state)
    {
        response.ContentType = state.ContentType;
        foreach (var header in state.Headers)
        {
            response.Headers[header.Key] = header.Value;
        }

        foreach (var metadata in state.Metadata)
        {
            response.Headers[$"x-amz-meta-{metadata.Key}"] = metadata.Value;
        }

        if (state.Tags.Count > 0)
        {
            response.Headers["x-amz-tagging-count"] = state.Tags.Count.ToString();
        }
    }

    private static Dictionary<string, string> ParseTagHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in QueryHelpers.ParseQuery($"?{value}"))
        {
            if (pair.Value.Count != 1 || !tags.TryAdd(pair.Key, pair.Value[0] ?? string.Empty))
            {
                throw new S3Exception(400, "InvalidTag", "Tag keys must be unique.");
            }
        }

        ValidateTags(tags);
        return tags;
    }

    private static Dictionary<string, string> ParseTaggingDocument(XDocument document)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in document.Descendants().Where(element => element.Name.LocalName == "Tag"))
        {
            var key = element.Elements().FirstOrDefault(child => child.Name.LocalName == "Key")?.Value
                ?? throw new S3Exception(400, "MalformedXML", "Each tag must include a key.");
            var value = element.Elements().FirstOrDefault(child => child.Name.LocalName == "Value")?.Value
                ?? throw new S3Exception(400, "MalformedXML", "Each tag must include a value.");
            if (!tags.TryAdd(key, value))
            {
                throw new S3Exception(400, "InvalidTag", "Tag keys must be unique.");
            }
        }

        ValidateTags(tags);
        return tags;
    }

    private static void ValidateTags(IReadOnlyDictionary<string, string> tags)
    {
        if (tags.Count > 10 ||
            tags.Any(tag => tag.Key.Length is < 1 or > 128 || tag.Value.Length > 256))
        {
            throw new S3Exception(400, "InvalidTag", "Objects support at most 10 tags with 128-character keys and 256-character values.");
        }
    }

    private static XElement TaggingElement(IReadOnlyDictionary<string, string> tags) =>
        new(S3Namespace + "Tagging",
            new XElement(S3Namespace + "TagSet",
                tags.OrderBy(tag => tag.Key, StringComparer.Ordinal)
                    .Select(tag => new XElement(S3Namespace + "Tag",
                        new XElement(S3Namespace + "Key", tag.Key),
                        new XElement(S3Namespace + "Value", tag.Value)))));

    private static async Task<XDocument> ReadXmlAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var reader = XmlReader.Create(request.Body, new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = 1024 * 1024
            });
            return await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        }
        catch (XmlException)
        {
            throw new S3Exception(400, "MalformedXML", "The XML request body is malformed.");
        }
    }

    private static (string Bucket, string Key) ParseCopySource(string value)
    {
        var decoded = Uri.UnescapeDataString(value).TrimStart('/');
        var queryIndex = decoded.IndexOf('?');
        if (queryIndex >= 0)
        {
            if (QueryHelpers.ParseQuery(decoded[queryIndex..]).ContainsKey("versionId"))
            {
                throw new S3Exception(501, "NotImplemented", "Versioned copy sources are not supported.");
            }

            decoded = decoded[..queryIndex];
        }

        var parts = decoded.Split('/', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new S3Exception(400, "InvalidRequest", "x-amz-copy-source must identify a bucket and object key.");
        }

        ValidateBucket(parts[0]);
        ValidateKey(parts[1]);
        return (parts[0], parts[1]);
    }

    private static int ParsePartNumber(string? value)
    {
        if (!int.TryParse(value, out var partNumber) || partNumber is < 1 or > 10000)
        {
            throw new S3Exception(400, "InvalidArgument", "partNumber must be between 1 and 10000.");
        }

        return partNumber;
    }

    private static int ParseMaxKeys(string? value)
    {
        if (value is null)
        {
            return 1000;
        }

        if (!int.TryParse(value, out var parsed) || parsed is < 0 or > 1000)
        {
            throw new S3Exception(400, "InvalidArgument", "max-keys must be between 0 and 1000.");
        }

        return parsed;
    }

    private static string? DecodeContinuation(string? token)
    {
        if (token is null)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(token));
        }
        catch (FormatException)
        {
            throw new S3Exception(400, "InvalidArgument", "The continuation token is invalid.");
        }
    }

    private static string EncodeContinuation(string key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(key));

    private static void ValidateBucket(string bucket)
    {
        if (!BucketNameRegex().IsMatch(bucket))
        {
            throw new S3Exception(400, "InvalidBucketName", "Bucket names must be 3-63 lowercase letters, numbers, dots, or hyphens.");
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.EndsWith('/') || key.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new S3Exception(400, "InvalidObjectName", "The object key is invalid.");
        }
    }

    private static string QuoteEtag(string etag) =>
        etag.StartsWith('"') ? etag : $"\"{etag}\"";

    private static string S3Date(DateTimeOffset date) => date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    [GeneratedRegex("^(?!.*\\.\\.)(?!.*\\.-)(?!.*-\\.)(?!\\d+\\.\\d+\\.\\d+\\.\\d+$)[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")]
    private static partial Regex BucketNameRegex();

    private sealed record ListEntry(string Key, ObjectInfo? Object, string? Prefix);
}
