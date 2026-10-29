using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OneDriveS3Proxy.Graph;

namespace OneDriveS3Proxy.Tests;

public sealed class S3HardeningTests : IClassFixture<LonghornProxyFactory>
{
    private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";
    private readonly LonghornProxyFactory _factory;
    private readonly HttpClient _client;

    public S3HardeningTests(LonghornProxyFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public static TheoryData<string> InvalidTagHeaders => new()
    {
        string.Join('&', Enumerable.Range(0, 11).Select(index => $"key{index}=value")),
        "duplicate=one&duplicate=two",
        $"{new string('k', 129)}=value",
        $"key={new string('v', 257)}",
        "=value"
    };

    [Fact]
    public async Task HealthProbes_AreAnonymousOnlyForExactGetPaths()
    {
        using var live = await _client.GetAsync("/health/live");
        using var ready = await _client.GetAsync("/health/ready");
        using var bucketObject = await _client.GetAsync("/health/secret.txt");
        using var bucket = await _client.GetAsync("/health");
        using var put = await _client.PutAsync("/health/live", new ByteArrayContent([1]));

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, bucketObject.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, bucket.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Theory]
    [InlineData("PUT", "/longhorn/acl.txt?acl=")]
    [InlineData("GET", "/longhorn/acl.txt?acl=")]
    [InlineData("GET", "/longhorn/acl.txt?versionId=1")]
    [InlineData("DELETE", "/longhorn/acl.txt?versionId=1")]
    [InlineData("HEAD", "/longhorn/acl.txt?partNumber=1")]
    [InlineData("PUT", "/longhorn/acl.txt?retention=")]
    [InlineData("PUT", "/longhorn?versioning=")]
    [InlineData("DELETE", "/longhorn?policy=")]
    [InlineData("DELETE", "/longhorn?lifecycle=")]
    [InlineData("GET", "/longhorn?versions=")]
    [InlineData("GET", "/longhorn?uploads=")]
    [InlineData("GET", "/longhorn?acl=")]
    public async Task UnsupportedSubresources_AreRejectedWithoutRunningPlainOperations(string method, string path)
    {
        var original = Encoding.UTF8.GetBytes("original");
        using var seed = await SendAsync(HttpMethod.Put, "/longhorn/acl.txt", original);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        using var response = await SendAsync(new HttpMethod(method), path, method == "PUT" ? "<xml/>"u8.ToArray() : null);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        using var get = await SendAsync(HttpMethod.Get, "/longhorn/acl.txt");
        Assert.Equal(original, await get.Content.ReadAsByteArrayAsync());
        using var bucket = await SendAsync(HttpMethod.Head, "/longhorn");
        Assert.Equal(HttpStatusCode.OK, bucket.StatusCode);
    }

    [Theory]
    [MemberData(nameof(InvalidTagHeaders))]
    public async Task PutObject_RejectsInvalidTagsBeforeChangingObjects(string invalidTags)
    {
        const string existingPath = "/longhorn/invalid-tags-existing.txt";
        const string missingPath = "/longhorn/invalid-tags-missing.txt";
        var original = "original"u8.ToArray();
        using var seed = await SendAsync(HttpMethod.Put, existingPath, original,
            new Dictionary<string, string>
            {
                ["x-amz-meta-owner"] = "backup-team",
                ["x-amz-tagging"] = "purpose=backup"
            });
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        foreach (var path in new[] { existingPath, missingPath })
        {
            using var rejected = await SendAsync(HttpMethod.Put, path, "replacement"u8.ToArray(),
                new Dictionary<string, string>
                {
                    ["x-amz-meta-owner"] = "replacement-owner",
                    ["x-amz-tagging"] = invalidTags
                });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains("<Code>InvalidTag</Code>", await rejected.Content.ReadAsStringAsync());
        }

        using var get = await SendAsync(HttpMethod.Get, existingPath);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(original, await get.Content.ReadAsByteArrayAsync());
        Assert.Equal(seed.Headers.ETag, get.Headers.ETag);
        Assert.Equal("backup-team", get.Headers.GetValues("x-amz-meta-owner").Single());
        using var tags = await SendAsync(HttpMethod.Get, existingPath + "?tagging=");
        Assert.Equal("backup", XDocument.Parse(await tags.Content.ReadAsStringAsync())
            .Descendants(S3 + "Tag").Single().Element(S3 + "Value")?.Value);
        using var missing = await SendAsync(HttpMethod.Head, missingPath);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task PutObject_AcceptsTenTagsAndPersistsMetadata()
    {
        const string path = "/longhorn/ten-tags.txt";
        var payload = "valid upload"u8.ToArray();
        var tags = string.Join('&', Enumerable.Range(0, 10).Select(index => $"key{index}=value{index}"));
        using var put = await SendAsync(HttpMethod.Put, path, payload,
            new Dictionary<string, string>
            {
                ["x-amz-meta-owner"] = "backup-team",
                ["x-amz-tagging"] = tags
            });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var get = await SendAsync(HttpMethod.Get, path);
        Assert.Equal(payload, await get.Content.ReadAsByteArrayAsync());
        Assert.Equal(put.Headers.ETag, get.Headers.ETag);
        Assert.Equal("backup-team", get.Headers.GetValues("x-amz-meta-owner").Single());
        Assert.Equal("10", get.Headers.GetValues("x-amz-tagging-count").Single());
        using var tagging = await SendAsync(HttpMethod.Get, path + "?tagging=");
        Assert.Equal(10, XDocument.Parse(await tagging.Content.ReadAsStringAsync()).Descendants(S3 + "Tag").Count());
    }

    [Theory]
    [InlineData("previous", false, false)]
    [InlineData("", false, false)]
    [InlineData("null", false, false)]
    [InlineData("previous", true, false)]
    [InlineData("previous", false, true)]
    public async Task CopyObject_RejectsVersionedSourcesBeforeChangingObjects(
        string versionId,
        bool encoded,
        bool selfCopy)
    {
        const string sourcePath = "/longhorn/versioned-copy-source.txt";
        const string destinationPath = "/longhorn/versioned-copy-destination.txt";
        using var sourceSeed = await SendAsync(HttpMethod.Put, sourcePath, "current-source"u8.ToArray());
        using var destinationSeed = await SendAsync(HttpMethod.Put, destinationPath, "original-destination"u8.ToArray());
        Assert.Equal(HttpStatusCode.OK, sourceSeed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, destinationSeed.StatusCode);

        var copySource = sourcePath + "?versionId=" + Uri.EscapeDataString(versionId);
        using var copy = await SendAsync(HttpMethod.Put, selfCopy ? sourcePath : destinationPath,
            headers: new Dictionary<string, string>
            {
                ["x-amz-copy-source"] = encoded ? Uri.EscapeDataString(copySource) : copySource,
                ["x-amz-metadata-directive"] = "REPLACE",
                ["x-amz-meta-owner"] = "replacement-owner"
            });
        Assert.Equal(HttpStatusCode.NotImplemented, copy.StatusCode);
        Assert.Contains("<Code>NotImplemented</Code>", await copy.Content.ReadAsStringAsync());

        using var source = await SendAsync(HttpMethod.Get, sourcePath);
        Assert.Equal("current-source", await source.Content.ReadAsStringAsync());
        Assert.Equal(sourceSeed.Headers.ETag, source.Headers.ETag);
        Assert.False(source.Headers.Contains("x-amz-meta-owner"));
        using var destination = await SendAsync(HttpMethod.Get, destinationPath);
        Assert.Equal("original-destination", await destination.Content.ReadAsStringAsync());
        Assert.Equal(destinationSeed.Headers.ETag, destination.Headers.ETag);
        Assert.False(destination.Headers.Contains("x-amz-meta-owner"));
    }

    [Theory]
    [InlineData("bad/../key", false)]
    [InlineData("bad/../key", true)]
    [InlineData("", false)]
    [InlineData("folder/", false)]
    [InlineData(null, false)]
    public async Task DeleteObjects_ValidatesAllKeysBeforeDeleting(string? invalidKey, bool quiet)
    {
        var keys = new[] { "validated-delete-first.txt", "validated-delete-last.txt" };
        foreach (var key in keys)
        {
            using var seed = await SendAsync(HttpMethod.Put, "/longhorn/" + key, "original"u8.ToArray());
            Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        }

        var document = new XElement(S3 + "Delete",
            new XElement(S3 + "Object", new XElement(S3 + "Key", keys[0])),
            new XElement(S3 + "Object", invalidKey is null ? null : new XElement(S3 + "Key", invalidKey)),
            new XElement(S3 + "Object", new XElement(S3 + "Key", keys[1])),
            new XElement(S3 + "Quiet", quiet.ToString().ToLowerInvariant()));
        using var rejected = await SendAsync(HttpMethod.Post, "/longhorn?delete=",
            Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting)));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var code = invalidKey is null ? "MalformedXML" : "InvalidObjectName";
        Assert.Contains($"<Code>{code}</Code>", await rejected.Content.ReadAsStringAsync());

        foreach (var key in keys)
        {
            using var get = await SendAsync(HttpMethod.Get, "/longhorn/" + key);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal("original", await get.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteObjects_ValidRequestsPreserveQuietMode(bool quiet)
    {
        const string key = "valid-quiet-delete.txt";
        using var seed = await SendAsync(HttpMethod.Put, "/longhorn/" + key, "original"u8.ToArray());
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        var body = Encoding.UTF8.GetBytes(
            $"<Delete><Object><Key>{key}</Key></Object><Quiet>{quiet.ToString().ToLowerInvariant()}</Quiet></Delete>");
        using var response = await SendAsync(HttpMethod.Post, "/longhorn?delete=", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var deleted = XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants(S3 + "Deleted").ToList();
        Assert.Equal(quiet ? 0 : 1, deleted.Count);
        if (!quiet)
        {
            Assert.Equal(key, deleted.Single().Element(S3 + "Key")?.Value);
        }

        using var missing = await SendAsync(HttpMethod.Head, "/longhorn/" + key);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task UploadPartCopy_IsRejectedInsteadOfStoringAnEmptyPart()
    {
        using var initiate = await SendAsync(HttpMethod.Post, "/longhorn/copy-part.bin?uploads=");
        var uploadId = XDocument.Parse(await initiate.Content.ReadAsStringAsync())
            .Descendants(S3 + "UploadId")
            .Single()
            .Value;

        using var response = await SendAsync(
            HttpMethod.Put,
            $"/longhorn/copy-part.bin?partNumber=1&uploadId={uploadId}",
            headers: new Dictionary<string, string> { ["x-amz-copy-source"] = "/longhorn/acl.txt" });

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task UnsignedAmzHeaders_AreRejected()
    {
        using var seed = await SendAsync(HttpMethod.Put, "/longhorn/private-source.txt", "secret"u8.ToArray());
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Put, "/longhorn/stolen.txt")
        {
            Content = new ByteArrayContent([])
        };
        LonghornSigV4Signer.Sign(request, []);
        request.Headers.TryAddWithoutValidation("x-amz-copy-source", "/longhorn/private-source.txt");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var stolen = await SendAsync(HttpMethod.Head, "/longhorn/stolen.txt");
        Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);
    }

    [Fact]
    public async Task HeaderSignedRequests_RejectTimestampsOutsideTheSkewWindow()
    {
        var stale = new HttpRequestMessage(HttpMethod.Get, "/");
        LonghornSigV4Signer.Sign(stale, [], DateTimeOffset.UtcNow.AddHours(-1));
        var future = new HttpRequestMessage(HttpMethod.Get, "/");
        LonghornSigV4Signer.Sign(future, [], DateTimeOffset.UtcNow.AddHours(1));
        var slightlyOff = new HttpRequestMessage(HttpMethod.Get, "/");
        LonghornSigV4Signer.Sign(slightlyOff, [], DateTimeOffset.UtcNow.AddMinutes(-5));

        using var staleResponse = await _client.SendAsync(stale);
        using var futureResponse = await _client.SendAsync(future);
        using var slightlyOffResponse = await _client.SendAsync(slightlyOff);

        Assert.Equal(HttpStatusCode.Forbidden, staleResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, futureResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, slightlyOffResponse.StatusCode);
    }

    [Fact]
    public async Task PresignedUrls_ToleratePositiveClockSkewButHonorExpiry()
    {
        using var seed = await SendAsync(HttpMethod.Put, "/longhorn/skew.txt", "skew"u8.ToArray());
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        using var slightlyAhead = await _client.GetAsync(
            LonghornSigV4Signer.PresignGet("/longhorn/skew.txt", DateTimeOffset.UtcNow.AddMinutes(2)));
        using var farAhead = await _client.GetAsync(
            LonghornSigV4Signer.PresignGet("/longhorn/skew.txt", DateTimeOffset.UtcNow.AddHours(1)));
        using var expired = await _client.GetAsync(
            LonghornSigV4Signer.PresignGet("/longhorn/skew.txt", DateTimeOffset.UtcNow.AddHours(-1)));

        Assert.Equal(HttpStatusCode.OK, slightlyAhead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, farAhead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
    }

    [Fact]
    public async Task ListObjects_SupportsMarkerStartAfterAndZeroMaxKeys()
    {
        foreach (var key in new[] { "page/a", "page/b", "page/c" })
        {
            using var put = await SendAsync(HttpMethod.Put, $"/longhorn/{key}", Encoding.UTF8.GetBytes(key));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        var firstPage = await ListAsync("/longhorn?prefix=page%2F&max-keys=2");
        Assert.Equal(["page/a", "page/b"], Keys(firstPage));
        Assert.Equal("true", firstPage.Descendants(S3 + "IsTruncated").Single().Value);
        var nextMarker = firstPage.Descendants(S3 + "NextMarker").Single().Value;
        Assert.Equal("page/b", nextMarker);

        var secondPage = await ListAsync($"/longhorn?prefix=page%2F&max-keys=2&marker={Uri.EscapeDataString(nextMarker)}");
        Assert.Equal(["page/c"], Keys(secondPage));
        Assert.Equal("false", secondPage.Descendants(S3 + "IsTruncated").Single().Value);

        var startAfter = await ListAsync("/longhorn?list-type=2&prefix=page%2F&start-after=page%2Fa");
        Assert.Equal(["page/b", "page/c"], Keys(startAfter));

        var zero = await ListAsync("/longhorn?list-type=2&prefix=page%2F&max-keys=0");
        Assert.Empty(Keys(zero));
        Assert.Equal("false", zero.Descendants(S3 + "IsTruncated").Single().Value);
        Assert.Empty(zero.Descendants(S3 + "NextContinuationToken"));
    }

    [Fact]
    public void Kestrel_AllowsUploadsUpToTheConfiguredObjectLimit()
    {
        var limits = _factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;

        Assert.Equal((238000L + 1) * 1024 * 1024, limits.MaxRequestBodySize);
    }

    [Fact]
    public void StartupValidation_RejectsMissingNestedOptions()
    {
        using var factory = new MisconfiguredProxyFactory();

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("S3", exception.ToString());
        Assert.Contains("OptionsValidationException", exception.ToString());
    }

    private async Task<XDocument> ListAsync(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return XDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static string[] Keys(XDocument document) =>
        document.Descendants(S3 + "Contents").Select(item => item.Element(S3 + "Key")!.Value).ToArray();

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        byte[]? body = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
        }

        if (headers is not null)
        {
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        LonghornSigV4Signer.Sign(request, body ?? []);
        return await _client.SendAsync(request);
    }
}

public sealed class MisconfiguredProxyFactory : WebApplicationFactory<Program>
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
                ["S3:AccessKey"] = "",
                ["S3:SecretKey"] = ""
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOneDriveStore>();
            services.AddSingleton<IOneDriveStore, InMemoryOneDriveStore>();
        });
    }
}
