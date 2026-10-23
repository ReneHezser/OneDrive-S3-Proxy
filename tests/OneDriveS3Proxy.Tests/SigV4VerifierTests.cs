using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using OneDriveS3Proxy.S3;

namespace OneDriveS3Proxy.Tests;

public sealed class SigV4VerifierTests
{
    private const string AccessKey = "test-access";
    private const string SecretKey = "test-secret";
    private const string Region = "us-east-1";

    [Fact]
    public async Task VerifyAsync_AcceptsValidHeaderSignature()
    {
        var request = CreateSignedRequest("payload");

        await CreateVerifier().VerifyAsync(request, CancellationToken.None);
    }

    [Fact]
    public async Task VerifyAsync_RejectsChangedPayload()
    {
        var request = CreateSignedRequest("payload");
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes("changed"));

        await CreateVerifier().VerifyAsync(request, CancellationToken.None);
        await Assert.ThrowsAsync<AuthenticationException>(async () =>
            await request.Body.CopyToAsync(Stream.Null));
    }

    [Fact]
    public async Task VerifyAsync_RejectsWrongAccessKey()
    {
        var request = CreateSignedRequest("payload", "other-access");

        await Assert.ThrowsAsync<AuthenticationException>(
            () => CreateVerifier().VerifyAsync(request, CancellationToken.None));
    }

    private static SigV4Verifier CreateVerifier() =>
        new(Options.Create(new ProxyOptions
        {
            Entra = new EntraOptions { TenantId = "tenant", ClientId = "client", ClientSecret = "secret" },
            OneDrive = new OneDriveOptions { UserId = "user" },
            S3 = new S3Options { AccessKey = AccessKey, SecretKey = SecretKey, Region = Region }
        }));

    private static HttpRequest CreateSignedRequest(string body, string accessKey = AccessKey)
    {
        var context = new DefaultHttpContext();
        var request = context.Request;
        request.Method = HttpMethods.Put;
        request.Host = new HostString("proxy.example.com");
        request.Path = "/bucket/folder/object.txt";
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var date = timestamp[..8];
        var payloadHash = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        request.Headers["x-amz-date"] = timestamp;
        request.Headers["x-amz-content-sha256"] = payloadHash;

        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalRequest = string.Join('\n',
            "PUT",
            "/bucket/folder/object.txt",
            string.Empty,
            $"host:proxy.example.com\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{timestamp}\n",
            signedHeaders,
            payloadHash);
        var scope = $"{date}/{Region}/s3/aws4_request";
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            timestamp,
            scope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        var signature = Hex(Hmac(SigningKey(date), stringToSign));
        request.Headers.Authorization =
            $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
        return request;
    }

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
}
