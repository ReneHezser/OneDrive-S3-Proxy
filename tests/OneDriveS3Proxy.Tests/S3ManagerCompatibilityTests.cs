using System.Net;
using System.Text;
using System.Xml.Linq;

namespace OneDriveS3Proxy.Tests;

public sealed class S3ManagerCompatibilityTests : IClassFixture<LonghornProxyFactory>
{
    private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";
    private readonly HttpClient _client;

    public S3ManagerCompatibilityTests(LonghornProxyFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task S3ManagerBasicOperations_AreSupported()
    {
        using var createBucket = await SendAsync(HttpMethod.Put, "/gui-test");
        Assert.Equal(HttpStatusCode.OK, createBucket.StatusCode);

        using var buckets = await SendAsync(HttpMethod.Get, "/");
        Assert.Equal(HttpStatusCode.OK, buckets.StatusCode);
        var bucketsXml = XDocument.Parse(await buckets.Content.ReadAsStringAsync());
        Assert.Contains(
            bucketsXml.Descendants(S3 + "Bucket"),
            bucket => bucket.Element(S3 + "Name")?.Value == "gui-test");

        using var location = await SendAsync(HttpMethod.Get, "/gui-test?location=");
        Assert.Equal(HttpStatusCode.OK, location.StatusCode);
        Assert.Equal("LocationConstraint", XDocument.Parse(await location.Content.ReadAsStringAsync()).Root?.Name.LocalName);

        var first = Encoding.UTF8.GetBytes("first");
        var second = Encoding.UTF8.GetBytes("second");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, "/gui-test/folder/first.txt", first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, "/gui-test/folder/second.txt", second)).StatusCode);

        using var presignedGet = await _client.GetAsync(LonghornSigV4Signer.PresignGet("/gui-test/folder/first.txt"));
        Assert.Equal(HttpStatusCode.OK, presignedGet.StatusCode);
        Assert.Equal(first, await presignedGet.Content.ReadAsByteArrayAsync());

        var deleteBody = Encoding.UTF8.GetBytes(
            """<Delete xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><Object><Key>folder/first.txt</Key></Object><Object><Key>folder/second.txt</Key></Object></Delete>""");
        using var deleteObjects = await SendAsync(HttpMethod.Post, "/gui-test?delete=", deleteBody);
        Assert.Equal(HttpStatusCode.OK, deleteObjects.StatusCode);
        var deleteXml = XDocument.Parse(await deleteObjects.Content.ReadAsStringAsync());
        Assert.Equal(2, deleteXml.Descendants(S3 + "Deleted").Count());

        using var deleteBucket = await SendAsync(HttpMethod.Delete, "/gui-test");
        Assert.Equal(HttpStatusCode.NoContent, deleteBucket.StatusCode);
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
