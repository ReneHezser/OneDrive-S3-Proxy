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
