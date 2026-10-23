using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OneDriveS3Proxy.Graph;

namespace OneDriveS3Proxy.Tests;

public sealed class LonghornS3CompatibilityTests : IClassFixture<LonghornProxyFactory>
{
    private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";
    private readonly HttpClient _client;

    public LonghornS3CompatibilityTests(LonghornProxyFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task LonghornVolumeBackupOperations_AreSupported()
    {
        const string firstKey = "backupstore/volumes/aa/volume.cfg";
        const string secondKey = "backupstore/volumes/bb/backups/backup_test.cfg";
        var firstBody = Encoding.UTF8.GetBytes("""{"name":"volume"}""");
        var secondBody = Encoding.UTF8.GetBytes("""{"name":"backup"}""");

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/longhorn/{firstKey}", firstBody)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/longhorn/{secondKey}", secondBody)).StatusCode);

        using var head = await SendAsync(HttpMethod.Head, $"/longhorn/{firstKey}");
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(firstBody.Length, head.Content.Headers.ContentLength);
        Assert.NotNull(head.Content.Headers.LastModified);

        using var get = await SendAsync(HttpMethod.Get, $"/longhorn/{firstKey}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(firstBody, await get.Content.ReadAsByteArrayAsync());

        using var list = await SendAsync(
            HttpMethod.Get,
            "/longhorn?list-type=2&delimiter=%2F&prefix=backupstore%2Fvolumes%2F");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listXml = XDocument.Parse(await list.Content.ReadAsStringAsync());
        var prefixes = listXml.Descendants(S3 + "CommonPrefixes")
            .Select(element => element.Element(S3 + "Prefix")?.Value)
            .ToList();
        Assert.Equal(
            ["backupstore/volumes/aa/", "backupstore/volumes/bb/"],
            prefixes);

        using var delete = await SendAsync(HttpMethod.Delete, $"/longhorn/{firstKey}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var missing = await SendAsync(HttpMethod.Head, $"/longhorn/{firstKey}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, byte[]? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
        }

        LonghornSigV4Signer.Sign(request, body ?? []);
        return await _client.SendAsync(request);
    }
}

public sealed class LonghornProxyFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Entra:TenantId"] = "tenant",
                ["Entra:ClientId"] = "client",
                ["Entra:ClientSecret"] = "entra-secret",
                ["OneDrive:UserId"] = "user@example.com",
                ["OneDrive:RootFolder"] = "S3",
                ["S3:AccessKey"] = LonghornSigV4Signer.AccessKey,
                ["S3:SecretKey"] = LonghornSigV4Signer.SecretKey,
                ["S3:Region"] = LonghornSigV4Signer.Region
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOneDriveStore>();
            services.AddSingleton<IOneDriveStore, InMemoryOneDriveStore>();
        });
    }
}

internal sealed class InMemoryOneDriveStore : IOneDriveStore
{
    private readonly ConcurrentDictionary<string, byte> _buckets = new(StringComparer.Ordinal)
    {
        ["longhorn"] = 0
    };
    private readonly ConcurrentDictionary<(string Bucket, string Key), byte[]> _objects = new();
    private readonly ConcurrentDictionary<(string Bucket, string Key), ObjectState> _states = new();
    private readonly ConcurrentDictionary<string, TestUpload> _uploads = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BucketInfo>>(_buckets.Keys
            .Select(name => new BucketInfo(name, DateTimeOffset.UtcNow))
            .ToList());

    public Task CreateBucketAsync(string bucket, CancellationToken cancellationToken)
    {
        _buckets[bucket] = 0;
        return Task.CompletedTask;
    }

    public Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken) =>
        Task.FromResult(_buckets.ContainsKey(bucket));

    public Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken)
    {
        _buckets.TryRemove(bucket, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ObjectInfo>> ListObjectsAsync(string bucket, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ObjectInfo>>(_objects
            .Where(pair => pair.Key.Bucket == bucket)
            .Select(pair => Info(pair.Key.Key, pair.Value))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToList());

    public async Task<ObjectListing> ListObjectsAsync(string bucket, string prefix, string? delimiter, CancellationToken cancellationToken) =>
        ObjectListing.FromObjects(await ListObjectsAsync(bucket, cancellationToken), prefix, delimiter);

    public async Task<ObjectInfo> PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        long? length,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var value = buffer.ToArray();
        _objects[(bucket, key)] = value;
        return Info(key, value);
    }

    public Task<GraphDownload> GetObjectAsync(
        string bucket,
        string key,
        string? range,
        CancellationToken cancellationToken)
    {
        var value = Get(bucket, key);
        var status = HttpStatusCode.OK;
        var content = value;
        ContentRangeHeaderValue? contentRange = null;
        if (range is not null)
        {
            var bounds = range["bytes=".Length..].Split('-', 2);
            var start = long.Parse(bounds[0]);
            var end = string.IsNullOrEmpty(bounds[1]) ? value.Length - 1 : long.Parse(bounds[1]);
            content = value[(int)start..((int)end + 1)];
            status = HttpStatusCode.PartialContent;
            contentRange = new ContentRangeHeaderValue(start, end, value.Length);
        }

        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(content)
        };
        response.Content.Headers.ContentType = new("application/octet-stream");
        response.Content.Headers.ContentRange = contentRange;
        return Task.FromResult(new GraphDownload(response, Info(key, value)));
    }

    public Task<ObjectInfo> GetObjectInfoAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        var value = Get(bucket, key);
        return Task.FromResult(Info(key, value));
    }

    public Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        _objects.TryRemove((bucket, key), out _);
        _states.TryRemove((bucket, key), out _);
        return Task.CompletedTask;
    }

    public Task<ObjectInfo> CopyObjectAsync(
        string sourceBucket,
        string sourceKey,
        string destinationBucket,
        string destinationKey,
        CancellationToken cancellationToken)
    {
        var value = Get(sourceBucket, sourceKey).ToArray();
        _objects[(destinationBucket, destinationKey)] = value;
        return Task.FromResult(Info(destinationKey, value));
    }

    public Task<ObjectState> GetObjectStateAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken) =>
        Task.FromResult(_states.TryGetValue((bucket, key), out var state) ? state : new ObjectState());

    public Task PutObjectStateAsync(
        string bucket,
        string key,
        ObjectState state,
        CancellationToken cancellationToken)
    {
        _states[(bucket, key)] = state with { Key = key };
        return Task.CompletedTask;
    }

    public Task DeleteObjectStateAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        _states.TryRemove((bucket, key), out _);
        return Task.CompletedTask;
    }

    public Task<MultipartUpload> CreateMultipartUploadAsync(
        string bucket,
        string key,
        ObjectState state,
        CancellationToken cancellationToken)
    {
        var upload = new MultipartUpload(Guid.NewGuid().ToString("N"), bucket, key, state);
        _uploads[upload.UploadId] = new TestUpload(upload);
        return Task.FromResult(upload);
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
        var upload = GetUpload(bucket, key, uploadId);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var value = buffer.ToArray();
        var part = new UploadedPart(partNumber, value.Length, ETag(value), DateTimeOffset.UtcNow);
        upload.Parts[partNumber] = (value, part);
        return part;
    }

    public Task<IReadOnlyList<UploadedPart>> ListPartsAsync(
        string bucket,
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        var upload = GetUpload(bucket, key, uploadId);
        return Task.FromResult<IReadOnlyList<UploadedPart>>(upload.Parts.Values
            .Select(value => value.Part)
            .OrderBy(part => part.PartNumber)
            .ToList());
    }

    public Task<ObjectInfo> CompleteMultipartUploadAsync(
        string bucket,
        string key,
        string uploadId,
        IReadOnlyList<UploadedPart> parts,
        CancellationToken cancellationToken)
    {
        var upload = GetUpload(bucket, key, uploadId);
        using var result = new MemoryStream();
        foreach (var part in parts)
        {
            var stored = upload.Parts[part.PartNumber];
            result.Write(stored.Content);
        }

        var value = result.ToArray();
        _objects[(bucket, key)] = value;
        _states[(bucket, key)] = upload.Upload.State;
        _uploads.TryRemove(uploadId, out _);
        return Task.FromResult(Info(key, value));
    }

    public Task AbortMultipartUploadAsync(
        string bucket,
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        GetUpload(bucket, key, uploadId);
        _uploads.TryRemove(uploadId, out _);
        return Task.CompletedTask;
    }

    public Task CheckReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private byte[] Get(string bucket, string key) =>
        _objects.TryGetValue((bucket, key), out var value)
            ? value
            : throw new GraphStoreException(404, "Object not found.");

    private TestUpload GetUpload(string bucket, string key, string uploadId) =>
        _uploads.TryGetValue(uploadId, out var upload) &&
        upload.Upload.Bucket == bucket &&
        upload.Upload.Key == key
            ? upload
            : throw new GraphStoreException(404, "Upload not found.", "NoSuchUpload");

    private static ObjectInfo Info(string key, byte[] value) =>
        new(key, value.Length, DateTimeOffset.UtcNow, ETag(value));

    private static string ETag(byte[] value) =>
        Convert.ToHexString(MD5.HashData(value)).ToLowerInvariant();

    private sealed record TestUpload(MultipartUpload Upload)
    {
        public ConcurrentDictionary<int, (byte[] Content, UploadedPart Part)> Parts { get; } = new();
    }
}

internal static class LonghornSigV4Signer
{
    public const string AccessKey = "longhorn-access";
    public const string SecretKey = "longhorn-secret";
    public const string Region = "us-east-1";

    public static void Sign(HttpRequestMessage request, byte[] body, DateTimeOffset? signedAt = null)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
        var rawTarget = uri.OriginalString;
        var queryIndex = rawTarget.IndexOf('?');
        var path = queryIndex >= 0 ? rawTarget[..queryIndex] : rawTarget;
        var query = queryIndex >= 0 ? rawTarget[queryIndex..] : string.Empty;
        var timestamp = (signedAt ?? DateTimeOffset.UtcNow).ToString("yyyyMMdd'T'HHmmss'Z'");
        var date = timestamp[..8];
        var payloadHash = Hex(SHA256.HashData(body));
        request.Headers.Host = "localhost";
        request.Headers.TryAddWithoutValidation("x-amz-date", timestamp);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        const string hostHeader = "host";
        var signedHeaderValues = request.Headers
            .Where(header => header.Key.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase))
            .Select(header => (Name: header.Key.ToLowerInvariant(), Value: string.Join(",", header.Value).Trim()))
            .Append((Name: hostHeader, Value: "localhost"))
            .OrderBy(header => header.Name, StringComparer.Ordinal)
            .ToList();
        var signedHeaders = string.Join(';', signedHeaderValues.Select(header => header.Name));
        var canonicalHeaders = string.Concat(signedHeaderValues.Select(header => $"{header.Name}:{header.Value}\n"));
        var canonicalRequest = string.Join('\n',
            request.Method.Method,
            path,
            CanonicalQuery(query),
            canonicalHeaders,
            signedHeaders,
            payloadHash);
        var scope = $"{date}/{Region}/s3/aws4_request";
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            timestamp,
            scope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        var signature = Hex(Hmac(SigningKey(date), stringToSign));
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"AWS4-HMAC-SHA256 Credential={AccessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    public static string PresignGet(string path, DateTimeOffset? signedAt = null)
    {
        var timestamp = (signedAt ?? DateTimeOffset.UtcNow).ToString("yyyyMMdd'T'HHmmss'Z'");
        var date = timestamp[..8];
        var scope = $"{date}/{Region}/s3/aws4_request";
        var query = string.Join('&',
            "X-Amz-Algorithm=AWS4-HMAC-SHA256",
            $"X-Amz-Credential={Uri.EscapeDataString($"{AccessKey}/{scope}")}",
            $"X-Amz-Date={timestamp}",
            "X-Amz-Expires=300",
            "X-Amz-SignedHeaders=host");
        var canonicalRequest = string.Join('\n',
            "GET",
            path,
            CanonicalQuery($"?{query}"),
            "host:localhost\n",
            "host",
            "UNSIGNED-PAYLOAD");
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            timestamp,
            scope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        var signature = Hex(Hmac(SigningKey(date), stringToSign));
        return $"{path}?{query}&X-Amz-Signature={signature}";
    }

    private static string CanonicalQuery(string query) =>
        string.Join("&", QueryHelpers.ParseQuery(query)
            .SelectMany(pair => pair.Value.Select(value => (Name: Encode(pair.Key), Value: Encode(value ?? string.Empty))))
            .OrderBy(pair => pair.Name, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => $"{pair.Name}={pair.Value}"));

    private static byte[] SigningKey(string date)
    {
        var dateKey = Hmac(Encoding.UTF8.GetBytes($"AWS4{SecretKey}"), date);
        var regionKey = Hmac(dateKey, Region);
        var serviceKey = Hmac(regionKey, "s3");
        return Hmac(serviceKey, "aws4_request");
    }

    private static byte[] Hmac(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();

    private static string Encode(string value) => Uri.EscapeDataString(value).Replace("%7E", "~", StringComparison.Ordinal);
}
