using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace OneDriveS3Proxy.Tests;

public sealed class ExtendedS3CompatibilityTests : IClassFixture<LonghornProxyFactory>
{
    private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";
    private readonly HttpClient _client;

    public ExtendedS3CompatibilityTests(LonghornProxyFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task RangeCopyMetadataAndTags_RoundTrip()
    {
        var body = Encoding.UTF8.GetBytes("hello world");
        using var put = await SendAsync(
            HttpMethod.Put,
            "/longhorn/source.txt",
            body,
            new Dictionary<string, string>
            {
                ["x-amz-meta-owner"] = "backup-team",
                ["x-amz-tagging"] = "environment=test&kind=backup"
            },
            "text/plain");
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var range = await SendAsync(
            HttpMethod.Get,
            "/longhorn/source.txt",
            headers: new Dictionary<string, string> { ["Range"] = "bytes=6-10" });
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal("world", await range.Content.ReadAsStringAsync());
        Assert.Equal("bytes 6-10/11", range.Content.Headers.ContentRange?.ToString());

        using var tags = await SendAsync(HttpMethod.Get, "/longhorn/source.txt?tagging=");
        var tagsXml = XDocument.Parse(await tags.Content.ReadAsStringAsync());
        Assert.Equal(2, tagsXml.Descendants(S3 + "Tag").Count());

        var replacementTags = Encoding.UTF8.GetBytes(
            """<Tagging xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><TagSet><Tag><Key>environment</Key><Value>production</Value></Tag></TagSet></Tagging>""");
        using var putTags = await SendAsync(HttpMethod.Put, "/longhorn/source.txt?tagging=", replacementTags);
        Assert.Equal(HttpStatusCode.OK, putTags.StatusCode);

        using var copy = await SendAsync(
            HttpMethod.Put,
            "/longhorn/copied.txt",
            headers: new Dictionary<string, string> { ["x-amz-copy-source"] = "/longhorn/source.txt" });
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);

        using var headCopy = await SendAsync(HttpMethod.Head, "/longhorn/copied.txt");
        Assert.Equal("text/plain", headCopy.Content.Headers.ContentType?.MediaType);
        Assert.Equal("backup-team", headCopy.Headers.GetValues("x-amz-meta-owner").Single());

        using var copiedTags = await SendAsync(HttpMethod.Get, "/longhorn/copied.txt?tagging=");
        var copiedTagsXml = XDocument.Parse(await copiedTags.Content.ReadAsStringAsync());
        Assert.Equal(
            "production",
            copiedTagsXml.Descendants(S3 + "Tag").Single().Element(S3 + "Value")?.Value);

        using var replaceMetadata = await SendAsync(
            HttpMethod.Put,
            "/longhorn/copied.txt",
            headers: new Dictionary<string, string>
            {
                ["x-amz-copy-source"] = "/longhorn/copied.txt",
                ["x-amz-metadata-directive"] = "REPLACE",
                ["x-amz-meta-owner"] = "archive-team"
            },
            contentType: "text/custom");
        Assert.Equal(HttpStatusCode.OK, replaceMetadata.StatusCode);
        using var replacedHead = await SendAsync(HttpMethod.Head, "/longhorn/copied.txt");
        Assert.Equal("archive-team", replacedHead.Headers.GetValues("x-amz-meta-owner").Single());
        using var preservedTags = await SendAsync(HttpMethod.Get, "/longhorn/copied.txt?tagging=");
        Assert.Equal(
            "production",
            XDocument.Parse(await preservedTags.Content.ReadAsStringAsync())
                .Descendants(S3 + "Tag")
                .Single()
                .Element(S3 + "Value")?.Value);
    }

    [Fact]
    public async Task MultipartUpload_CanListCompleteAndAbort()
    {
        using var initiate = await SendAsync(
            HttpMethod.Post,
            "/longhorn/large.bin?uploads=",
            headers: new Dictionary<string, string> { ["x-amz-meta-purpose"] = "multipart-test" },
            contentType: "application/octet-stream");
        Assert.Equal(HttpStatusCode.OK, initiate.StatusCode);
        var uploadId = XDocument.Parse(await initiate.Content.ReadAsStringAsync())
            .Descendants(S3 + "UploadId")
            .Single()
            .Value;

        var firstBody = Enumerable.Repeat((byte)'a', 5 * 1024 * 1024).ToArray();
        var secondBody = Encoding.UTF8.GetBytes("tail");
        using var first = await SendAsync(
            HttpMethod.Put,
            $"/longhorn/large.bin?partNumber=1&uploadId={uploadId}",
            firstBody);
        using var second = await SendAsync(
            HttpMethod.Put,
            $"/longhorn/large.bin?partNumber=2&uploadId={uploadId}",
            secondBody);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstEtag = first.Headers.ETag?.Tag ?? throw new Xunit.Sdk.XunitException("Part 1 ETag was missing.");
        var secondEtag = second.Headers.ETag?.Tag ?? throw new Xunit.Sdk.XunitException("Part 2 ETag was missing.");

        using var listed = await SendAsync(HttpMethod.Get, $"/longhorn/large.bin?uploadId={uploadId}");
        Assert.Equal(2, XDocument.Parse(await listed.Content.ReadAsStringAsync()).Descendants(S3 + "Part").Count());

        var completeBody = Encoding.UTF8.GetBytes(
            $"<CompleteMultipartUpload><Part><PartNumber>1</PartNumber><ETag>{firstEtag}</ETag></Part>" +
            $"<Part><PartNumber>2</PartNumber><ETag>{secondEtag}</ETag></Part></CompleteMultipartUpload>");
        using var complete = await SendAsync(
            HttpMethod.Post,
            $"/longhorn/large.bin?uploadId={uploadId}",
            completeBody);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        using var head = await SendAsync(HttpMethod.Head, "/longhorn/large.bin");
        Assert.Equal(firstBody.Length + secondBody.Length, head.Content.Headers.ContentLength);
        Assert.Equal("multipart-test", head.Headers.GetValues("x-amz-meta-purpose").Single());

        using var secondInitiate = await SendAsync(HttpMethod.Post, "/longhorn/aborted.bin?uploads=");
        var secondUploadId = XDocument.Parse(await secondInitiate.Content.ReadAsStringAsync())
            .Descendants(S3 + "UploadId")
            .Single()
            .Value;
        using var abort = await SendAsync(HttpMethod.Delete, $"/longhorn/aborted.bin?uploadId={secondUploadId}");
        Assert.Equal(HttpStatusCode.NoContent, abort.StatusCode);

        using var missing = await SendAsync(HttpMethod.Delete, "/longhorn/aborted.bin?uploadId=missing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("<Code>NoSuchUpload</Code>", await missing.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        byte[]? body = null,
        IReadOnlyDictionary<string, string>? headers = null,
        string? contentType = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            if (contentType is not null)
            {
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            }
        }
        else if (contentType is not null)
        {
            request.Headers.TryAddWithoutValidation("Content-Type", contentType);
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
