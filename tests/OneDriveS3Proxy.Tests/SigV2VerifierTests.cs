using System.Globalization;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using OneDriveS3Proxy.S3;

namespace OneDriveS3Proxy.Tests;

public sealed class SigV2VerifierTests
{
    private const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private static readonly DateTimeOffset Now = new(2007, 3, 27, 19, 36, 42, TimeSpan.Zero);

    [Theory]
    [InlineData("GET", "Tue, 27 Mar 2007 19:36:42 +0000", "", "", "bWq2s1WEIj+Ydj0vQ697zp+IXMU=")]
    [InlineData("PUT", "Tue, 27 Mar 2007 21:15:45 +0000", "image/jpeg", "", "MyyxeRY7whkBe+bq8fHCL/2kKUg=")]
    public async Task VerifyAsync_AcceptsAwsHeaderExamples(
        string method, string date, string contentType, string md5, string signature)
    {
        var request = CreateRequest(method, "/johnsmith/photos/puppy.jpg");
        request.Headers.Date = date;
        request.ContentType = contentType;
        request.Headers["Content-MD5"] = md5;
        request.Headers.Authorization = $"AWS {AccessKey}:{signature}";
        var clock = DateTimeOffset.Parse(date, CultureInfo.InvariantCulture);

        await CreateVerifier(clock).VerifyAsync(request, CancellationToken.None);
    }

    [Fact]
    public async Task VerifyAsync_AcceptsAwsPresignedExample()
    {
        var request = CreateRequest("GET", "/johnsmith/photos/puppy.jpg");
        request.Headers.Remove("Date");
        request.QueryString = new QueryString(
            $"?AWSAccessKeyId={AccessKey}&Expires=1175139620&Signature=NpgCjnDzrM%2BWFzoENXmpNDUsSn8%3D");

        await CreateVerifier(DateTimeOffset.FromUnixTimeSeconds(1175139500))
            .VerifyAsync(request, CancellationToken.None);
    }

    [Theory]
    [InlineData("Tue, 27 Mar 2007 19:36:42 GMT")]
    [InlineData("20070327T193642Z")]
    public async Task VerifyAsync_UsesAmzDateAndAnEmptyDateLine(string amzDate)
    {
        var request = CreateRequest();
        request.Headers.Date = "invalid but ignored";
        request.Headers["X-Amz-Date"] = amzDate;
        Sign(request, $"\nx-amz-date:{amzDate}\n/bucket/object.txt");

        await CreateVerifier().VerifyAsync(request, CancellationToken.None);
    }

    [Fact]
    public async Task VerifyAsync_CanonicalizesHeaderNamesFoldingAndRepeatedValues()
    {
        var request = CreateRequest();
        request.Headers["X-Amz-Meta-Z"] = " last ";
        request.Headers["X-Amz-Meta-A"] = new StringValues([" first  value\r\n continued ", " second "]);
        Sign(request, $"{request.Headers.Date}\nx-amz-meta-a:first  value continued,second\nx-amz-meta-z:last\n/bucket/object.txt");

        await CreateVerifier().VerifyAsync(request, CancellationToken.None);
    }

    [Fact]
    public async Task VerifyAsync_SignsTheOriginalEscapedPath()
    {
        const string path = "/bucket/folder%2fwith%20space%2B%25.txt";
        var request = CreateRequest("GET", path);
        Sign(request, $"{request.Headers.Date}\n{path}");

        await CreateVerifier().VerifyAsync(request, CancellationToken.None);
    }

    [Theory]
    [InlineData("?uploads", "?uploads")]
    [InlineData("?uploads=", "?uploads")]
    [InlineData("?prefix=ignored&max-keys=1", "")]
    [InlineData("?uploadId=a%2Bb%2Fc%3D&partNumber=2", "?partNumber=2&uploadId=a+b/c=")]
    [InlineData("?response-content-type=text%2Fplain&response-content-disposition=attachment%3B%20filename%3Da+b",
        "?response-content-disposition=attachment; filename=a+b&response-content-type=text/plain")]
    [InlineData("?tagging&versionId=abc", "?tagging&versionId=abc")]
    public async Task VerifyAsync_CanonicalizesOnlySignedQueryParameters(string query, string canonicalQuery)
    {
        var request = CreateRequest("GET", "/bucket/object.txt" + query);
        Sign(request, $"{request.Headers.Date}\n/bucket/object.txt{canonicalQuery}");

        await CreateVerifier().VerifyAsync(request, CancellationToken.None);
    }

    [Theory]
    [InlineData(-901, false)]
    [InlineData(-900, true)]
    [InlineData(900, true)]
    [InlineData(901, false)]
    public async Task VerifyAsync_EnforcesHeaderClockSkew(int seconds, bool accepted)
    {
        var request = CreateRequest();
        request.Headers.Date = Now.AddSeconds(seconds).ToString("r", CultureInfo.InvariantCulture);
        Sign(request, $"{request.Headers.Date}\n/bucket/object.txt");

        if (accepted)
        {
            await CreateVerifier().VerifyAsync(request, CancellationToken.None);
        }
        else
        {
            await Assert.ThrowsAsync<AuthenticationException>(() =>
                CreateVerifier().VerifyAsync(request, CancellationToken.None));
        }
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(86400, true)]
    public async Task VerifyAsync_HonorsPresignedExpiry(int seconds, bool accepted)
    {
        var request = CreatePresignedRequest(Now.AddSeconds(seconds).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        if (accepted)
        {
            await CreateVerifier().VerifyAsync(request, CancellationToken.None);
        }
        else
        {
            await Assert.ThrowsAsync<AuthenticationException>(() =>
                CreateVerifier().VerifyAsync(request, CancellationToken.None));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("not-a-timestamp")]
    [InlineData("9999999999999999999999999")]
    public async Task VerifyAsync_RejectsMalformedExpiry(string expires)
    {
        var request = CreatePresignedRequest(expires);

        await Assert.ThrowsAsync<AuthenticationException>(() =>
            CreateVerifier().VerifyAsync(request, CancellationToken.None));
    }

    [Theory]
    [InlineData("AWSAccessKeyId")]
    [InlineData("Signature")]
    [InlineData("Expires")]
    public async Task VerifyAsync_RejectsRepeatedAuthenticationParameters(string parameter)
    {
        var request = CreatePresignedRequest();
        request.QueryString = request.QueryString.Add(parameter, request.Query[parameter].ToString());

        await Assert.ThrowsAsync<AuthenticationException>(() =>
            CreateVerifier().VerifyAsync(request, CancellationToken.None));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("method")]
    [InlineData("content-type")]
    [InlineData("content-md5")]
    [InlineData("metadata")]
    [InlineData("subresource")]
    [InlineData("access-key")]
    [InlineData("signature")]
    [InlineData("missing-date")]
    [InlineData("invalid-date")]
    [InlineData("duplicate-date")]
    [InlineData("duplicate-authorization")]
    public async Task VerifyAsync_RejectsTamperedOrMalformedHeaderRequests(string change)
    {
        var request = CreateRequest();
        Sign(request, $"{request.Headers.Date}\n/bucket/object.txt");
        switch (change)
        {
            case "path":
                request.HttpContext.Features.Get<IHttpRequestFeature>()!.RawTarget = "/bucket/other.txt";
                request.Path = "/bucket/other.txt";
                break;
            case "method":
                request.Method = "DELETE";
                break;
            case "content-type":
                request.ContentType = "text/plain";
                break;
            case "content-md5":
                request.Headers["Content-MD5"] = "321c3cf486ed509164edec1e1981fec8=";
                break;
            case "metadata":
                request.Headers["x-amz-meta-added"] = "value";
                break;
            case "subresource":
                request.QueryString = new QueryString("?tagging");
                break;
            case "access-key":
                request.Headers.Authorization = request.Headers.Authorization.ToString().Replace(AccessKey, "wrong-key");
                break;
            case "signature":
                request.Headers.Authorization = $"AWS {AccessKey}:not-base64";
                break;
            case "missing-date":
                request.Headers.Remove("Date");
                break;
            case "invalid-date":
                request.Headers.Date = "invalid";
                break;
            case "duplicate-date":
                request.Headers.Date = new StringValues([request.Headers.Date.ToString(), request.Headers.Date.ToString()]);
                break;
            case "duplicate-authorization":
                request.Headers.Authorization = new StringValues(
                    [request.Headers.Authorization.ToString(), request.Headers.Authorization.ToString()]);
                break;
        }

        await Assert.ThrowsAsync<AuthenticationException>(() =>
            CreateVerifier().VerifyAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAsync_RejectsRepeatedSignedSubresources()
    {
        var request = CreateRequest("GET", "/bucket/object.txt?uploadId=one&uploadId=two");
        Sign(request, $"{request.Headers.Date}\n/bucket/object.txt?uploadId=one");

        await Assert.ThrowsAsync<AuthenticationException>(() =>
            CreateVerifier().VerifyAsync(request, CancellationToken.None));
    }

    [Theory]
    [InlineData("?%74agging")]
    [InlineData("?TAGGING")]
    [InlineData("?%75ploadId=upload")]
    [InlineData("?UPLOADID=upload")]
    public async Task VerifyAsync_RejectsAliasesForSignedQueryNames(string query)
    {
        var request = CreateRequest("GET", "/bucket/object.txt" + query);
        Sign(request, $"{request.Headers.Date}\n/bucket/object.txt");

        await Assert.ThrowsAsync<AuthenticationException>(() =>
            CreateVerifier().VerifyAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAsync_PromotesSignedQueryHeadersOnlyAfterVerification()
    {
        var request = CreatePresignedRequest(
            extraQuery: "&content-type=text%2Fplain&x-amz-meta-client=legacy",
            contentType: "text/plain", amzHeaders: "x-amz-meta-client:legacy\n");

        await CreateVerifier().VerifyAsync(request, CancellationToken.None);

        Assert.Equal("text/plain", request.ContentType);
        Assert.Equal("legacy", request.Headers["x-amz-meta-client"].ToString());
    }

    [Fact]
    public async Task VerifyAsync_RejectsConflictingHeaderAndQueryValues()
    {
        var request = CreatePresignedRequest(extraQuery: "&content-type=text%2Fplain", contentType: "text/plain");
        request.ContentType = "application/json";

        await Assert.ThrowsAsync<AuthenticationException>(() =>
            CreateVerifier().VerifyAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAsync_RejectsCombinedHeaderAndQueryAuthentication()
    {
        var request = CreatePresignedRequest();
        request.Headers.Authorization = $"AWS {AccessKey}:anything";

        await Assert.ThrowsAsync<AuthenticationException>(() =>
            CreateVerifier().VerifyAsync(request, CancellationToken.None));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task VerifyAsync_ValidatesContentMd5WhileReading(bool tampered, bool knownLength)
    {
        var request = CreateRequest("PUT");
        var original = "payload"u8.ToArray();
        request.Body = new MemoryStream(tampered ? "changed"u8.ToArray() : original);
        request.ContentLength = knownLength ? original.Length : null;
        request.Headers["Content-MD5"] = Convert.ToBase64String(MD5.HashData(original));
        Sign(request, $"{request.Headers.Date}\n/bucket/object.txt");
        await CreateVerifier().VerifyAsync(request, CancellationToken.None);

        if (tampered)
        {
            await Assert.ThrowsAsync<AuthenticationException>(async () =>
                await request.Body.CopyToAsync(Stream.Null));
        }
        else
        {
            await request.Body.CopyToAsync(Stream.Null);
        }
    }

    [Fact]
    public async Task VerifyAsync_RejectsAppendedBytesReadAfterContentLength()
    {
        var request = CreateRequest("PUT");
        var payload = "payload"u8.ToArray();
        request.ContentLength = payload.Length;
        request.Body = new MemoryStream("payload-extra"u8.ToArray());
        request.Headers["Content-MD5"] = Convert.ToBase64String(MD5.HashData(payload));
        Sign(request, $"{request.Headers.Date}\n/bucket/object.txt");

        await CreateVerifier().VerifyAsync(request, CancellationToken.None);

        Assert.Equal(payload.Length, await request.Body.ReadAsync(new byte[payload.Length].AsMemory()));
        await Assert.ThrowsAsync<AuthenticationException>(async () =>
            await request.Body.CopyToAsync(Stream.Null));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("c2hvcnQ=")]
    public async Task VerifyAsync_RejectsMalformedContentMd5EvenWithAValidSignature(string md5)
    {
        var request = CreateRequest("PUT");
        request.Headers["Content-MD5"] = md5;
        Sign(request, $"{request.Headers.Date}\n/bucket/object.txt");

        await Assert.ThrowsAsync<AuthenticationException>(() =>
            CreateVerifier().VerifyAsync(request, CancellationToken.None));
    }

    private static SigV2Verifier CreateVerifier(DateTimeOffset? now = null) =>
        new(Options.Create(new ProxyOptions
        {
            S3 = new S3Options { AccessKey = AccessKey, SecretKey = SecretKey }
        }), new FixedTimeProvider(now ?? Now));

    private static HttpRequest CreateRequest(string method = "GET", string target = "/bucket/object.txt")
    {
        var request = new DefaultHttpContext().Request;
        var parts = target.Split('?', 2);
        request.Method = method;
        request.Host = new HostString("proxy.example.com");
        request.Path = PathString.FromUriComponent(parts[0]);
        request.QueryString = parts.Length == 2 ? new QueryString("?" + parts[1]) : QueryString.Empty;
        request.Headers.Date = Now.ToString("r", CultureInfo.InvariantCulture);
        request.HttpContext.Features.Get<IHttpRequestFeature>()!.RawTarget = target;
        return request;
    }

    private static void Sign(HttpRequest request, string dateHeadersAndResource)
    {
        var value = $"{request.Method}\n{request.Headers["Content-MD5"]}\n{request.ContentType}\n{dateHeadersAndResource}";
        request.Headers.Authorization = $"AWS {AccessKey}:{Signature(value)}";
    }

    private static HttpRequest CreatePresignedRequest(
        string? expires = null, string extraQuery = "", string contentType = "", string amzHeaders = "")
    {
        expires ??= Now.AddMinutes(5).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var request = CreateRequest();
        request.Headers.Remove("Date");
        var signature = Signature($"GET\n\n{contentType}\n{expires}\n{amzHeaders}/bucket/object.txt");
        request.QueryString = new QueryString(
            $"?AWSAccessKeyId={AccessKey}&Expires={expires}&Signature={Uri.EscapeDataString(signature)}{extraQuery}");
        return request;
    }

    private static string Signature(string value) =>
        Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(SecretKey), Encoding.UTF8.GetBytes(value)));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
