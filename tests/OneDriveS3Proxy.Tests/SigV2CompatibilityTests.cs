using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace OneDriveS3Proxy.Tests;

public sealed class SigV2CompatibilityTests(LonghornProxyFactory factory) : IClassFixture<LonghornProxyFactory>
{
    private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task HeaderAuthentication_SupportsObjectOperationsAndSigV4Interoperability()
    {
        const string path = "/longhorn/sigv2/header.txt";
        var body = "legacy payload"u8.ToArray();
        using var put = await SendAsync(HttpMethod.Put, path, body);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, path);
        LonghornSigV4Signer.Sign(getRequest, []);
        using var get = await _client.SendAsync(getRequest);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(body, await get.Content.ReadAsByteArrayAsync());

        using var head = await SendAsync(HttpMethod.Head, path);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(body.Length, head.Content.Headers.ContentLength);

        using var list = await SendAsync(HttpMethod.Get, "/longhorn?prefix=sigv2%2Fheader");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains("sigv2/header.txt", await list.Content.ReadAsStringAsync());

        using var delete = await SendAsync(HttpMethod.Delete, path);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var missing = await SendAsync(HttpMethod.Head, path);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task PresignedUrls_SupportUploadsDownloadsAndSignedQueryMetadata()
    {
        const string path = "/longhorn/sigv2/presigned.txt";
        var expires = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var putUrl = Presign("PUT", path, expires, "text/plain", "x-amz-meta-client:legacy\n") +
            "&content-type=text%2Fplain&x-amz-meta-client=legacy";
        using var put = await _client.PutAsync(putUrl, new ByteArrayContent("presigned payload"u8.ToArray()));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var get = await _client.GetAsync(Presign("GET", path, expires));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("presigned payload", await get.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", get.Content.Headers.ContentType?.MediaType);
        Assert.Equal("legacy", Assert.Single(get.Headers.GetValues("x-amz-meta-client")));

        using var expired = await _client.GetAsync(Presign("GET", path, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()));
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
    }

    [Theory]
    [InlineData("/longhorn/sigv2/with%20space%2B%25.txt")]
    [InlineData("/longhorn/sigv2/caf%C3%A9.txt")]
    public async Task HeaderAuthentication_SupportsEscapedObjectKeys(string path)
    {
        // TestServer loses some original URI escapes; SigV2 must verify the wire path.
        await using var server = new LonghornProxyFactory();
        server.UseKestrel(0);
        using var client = server.CreateClient();
        using var putRequest = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new ByteArrayContent("escaped key"u8.ToArray())
        };
        Sign(putRequest);
        using var put = await client.SendAsync(putRequest);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, path);
        Sign(getRequest);
        using var get = await client.SendAsync(getRequest);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("escaped key", await get.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HeaderAuthentication_SupportsMultipartUploadsAndTagging()
    {
        const string path = "/longhorn/sigv2/multipart.txt";
        using var initiate = await SendAsync(HttpMethod.Post, path + "?uploads", canonicalResource: path + "?uploads");
        Assert.Equal(HttpStatusCode.OK, initiate.StatusCode);
        var uploadId = XDocument.Parse(await initiate.Content.ReadAsStringAsync()).Descendants(S3 + "UploadId").Single().Value;
        var queryUploadId = Uri.EscapeDataString(uploadId);

        using var part = await SendAsync(
            HttpMethod.Put, $"{path}?uploadId={queryUploadId}&partNumber=1", "part body"u8.ToArray(),
            $"{path}?partNumber=1&uploadId={uploadId}");
        Assert.Equal(HttpStatusCode.OK, part.StatusCode);
        var etag = part.Headers.ETag?.ToString();
        Assert.NotNull(etag);

        var completion = Encoding.UTF8.GetBytes(
            $"<CompleteMultipartUpload><Part><PartNumber>1</PartNumber><ETag>{etag}</ETag></Part></CompleteMultipartUpload>");
        using var complete = await SendAsync(
            HttpMethod.Post, $"{path}?uploadId={queryUploadId}", completion, $"{path}?uploadId={uploadId}");
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        using var get = await SendAsync(HttpMethod.Get, path);
        Assert.Equal("part body", await get.Content.ReadAsStringAsync());

        var tagging = "<Tagging><TagSet><Tag><Key>client</Key><Value>sigv2</Value></Tag></TagSet></Tagging>"u8.ToArray();
        using var putTags = await SendAsync(HttpMethod.Put, path + "?tagging", tagging, path + "?tagging");
        Assert.Equal(HttpStatusCode.OK, putTags.StatusCode);
        using var getTags = await SendAsync(HttpMethod.Get, path + "?tagging", canonicalResource: path + "?tagging");
        Assert.Equal(HttpStatusCode.OK, getTags.StatusCode);
        Assert.Contains("<Value>sigv2</Value>", await getTags.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HeaderAuthentication_SupportsBucketOperationsAndPreservesUnsupportedSubresourceErrors()
    {
        const string path = "/sigv2-bucket";
        using var list = await SendAsync(HttpMethod.Get, "/");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var create = await SendAsync(HttpMethod.Put, path);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var head = await SendAsync(HttpMethod.Head, path);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        using var location = await SendAsync(HttpMethod.Get, path + "?location", canonicalResource: path + "?location");
        Assert.Equal(HttpStatusCode.OK, location.StatusCode);
        using var unsupported = await SendAsync(HttpMethod.Get, path + "?acl", canonicalResource: path + "?acl");
        Assert.Equal(HttpStatusCode.NotImplemented, unsupported.StatusCode);
        using var delete = await SendAsync(HttpMethod.Delete, path);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("timestamp")]
    [InlineData("metadata")]
    [InlineData("subresource")]
    [InlineData("encoded-subresource")]
    [InlineData("miscased-subresource")]
    [InlineData("mixed-query")]
    [InlineData("mixed-v4")]
    public async Task InvalidRequests_ReturnS3AuthenticationErrorsWithoutWritingAnObject(string change)
    {
        var path = $"/longhorn/sigv2/rejected-{change}.txt";
        var body = "original"u8.ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentMD5 = MD5.HashData(body);
        Sign(request, signedAt: change == "timestamp" ? DateTimeOffset.UtcNow.AddHours(-1) : null);
        switch (change)
        {
            case "payload":
                request.Content = new ByteArrayContent("modified"u8.ToArray());
                request.Content.Headers.ContentMD5 = MD5.HashData(body);
                break;
            case "metadata":
                request.Headers.TryAddWithoutValidation("x-amz-meta-added", "unsigned");
                break;
            case "subresource":
                request.RequestUri = new Uri(path + "?tagging", UriKind.Relative);
                break;
            case "encoded-subresource":
                request.RequestUri = new Uri(path + "?%74agging", UriKind.Relative);
                break;
            case "miscased-subresource":
                request.RequestUri = new Uri(path + "?TAGGING", UriKind.Relative);
                break;
            case "mixed-query":
                request.RequestUri = new Uri(Presign("PUT", path, DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()), UriKind.Relative);
                break;
            case "mixed-v4":
                request.RequestUri = new Uri(path + "?X-Amz-Signature=invalid", UriKind.Relative);
                break;
        }

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("SignatureDoesNotMatch", XDocument.Parse(await response.Content.ReadAsStringAsync()).Root?.Element("Code")?.Value);
        using var missing = await SendAsync(HttpMethod.Head, path);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, byte[]? body = null, string? canonicalResource = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentMD5 = MD5.HashData(body);
        }

        Sign(request, canonicalResource);
        return await _client.SendAsync(request);
    }

    internal static void Sign(HttpRequestMessage request, string? canonicalResource = null, DateTimeOffset? signedAt = null)
    {
        var date = (signedAt ?? DateTimeOffset.UtcNow).ToString("r", CultureInfo.InvariantCulture);
        request.Headers.TryAddWithoutValidation("Date", date);
        canonicalResource ??= request.RequestUri!.OriginalString.Split('?', 2)[0];
        var md5 = request.Content?.Headers.ContentMD5 is { } digest ? Convert.ToBase64String(digest) : string.Empty;
        var value = $"{request.Method.Method}\n{md5}\n{request.Content?.Headers.ContentType}\n{date}\n{canonicalResource}";
        request.Headers.TryAddWithoutValidation("Authorization", $"AWS {LonghornSigV4Signer.AccessKey}:{Signature(value)}");
    }

    private static string Presign(
        string method, string path, long expires, string contentType = "", string amzHeaders = "")
    {
        var expiry = expires.ToString(CultureInfo.InvariantCulture);
        var signature = Signature($"{method}\n\n{contentType}\n{expiry}\n{amzHeaders}{path}");
        return $"{path}?AWSAccessKeyId={LonghornSigV4Signer.AccessKey}&Expires={expiry}&Signature={Uri.EscapeDataString(signature)}";
    }

    private static string Signature(string value) =>
        Convert.ToBase64String(HMACSHA1.HashData(
            Encoding.UTF8.GetBytes(LonghornSigV4Signer.SecretKey), Encoding.UTF8.GetBytes(value)));
}
