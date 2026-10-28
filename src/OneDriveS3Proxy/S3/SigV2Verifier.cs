using System.Globalization;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace OneDriveS3Proxy.S3;

public sealed class SigV2Verifier(IOptions<ProxyOptions> options, TimeProvider timeProvider)
{
    private readonly S3Options _options = options.Value.S3;

    private static readonly HashSet<string> SignedParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "accelerate", "acl", "analytics", "cors", "defaultObjectAcl", "delete", "inventory",
        "lifecycle", "location", "logging", "metrics", "notification", "object-lock",
        "partNumber", "policy", "replication", "requestPayment", "restore", "select",
        "select-type", "storageClass", "tagging", "torrent", "uploadId", "uploads",
        "versionId", "versioning", "versions", "website",
        "response-cache-control", "response-content-disposition", "response-content-encoding",
        "response-content-language", "response-content-type", "response-expires"
    };

    public Task VerifyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var presigned = request.Query.ContainsKey("AWSAccessKeyId") || request.Query.ContainsKey("Signature");
        var headers = CanonicalHeaders(request, presigned);
        string accessKey;
        string signature;
        string date;
        if (presigned)
        {
            if (request.Headers.ContainsKey("Authorization"))
            {
                throw new AuthenticationException("Header and query authentication cannot be combined.");
            }

            accessKey = RequiredQuery(request, "AWSAccessKeyId");
            signature = RequiredQuery(request, "Signature");
            date = RequiredQuery(request, "Expires");
            if (!long.TryParse(date, NumberStyles.None, CultureInfo.InvariantCulture, out var expires) ||
                timeProvider.GetUtcNow().ToUnixTimeSeconds() > expires)
            {
                throw new AuthenticationException("The presigned request expiry is invalid or has passed.");
            }
        }
        else
        {
            var authorization = request.Headers.Authorization;
            if (authorization.Count != 1 ||
                authorization[0] is not { } value ||
                !value.StartsWith("AWS ", StringComparison.Ordinal))
            {
                throw new AuthenticationException("AWS Signature Version 2 authorization is required.");
            }

            var separator = value.IndexOf(':', 4);
            if (separator <= 4)
            {
                throw new AuthenticationException("The authorization header is malformed.");
            }

            accessKey = value[4..separator];
            signature = value[(separator + 1)..];
            var hasAmzDate = headers.TryGetValue("x-amz-date", out var amzDate);
            var timestamp = hasAmzDate ? amzDate : headers.GetValueOrDefault("date");
            if (!TryParseDate(timestamp, out var requestTime))
            {
                throw new AuthenticationException("A valid Date or x-amz-date header is required.");
            }

            var now = timeProvider.GetUtcNow();
            if (requestTime < now.AddMinutes(-15) || requestTime > now.AddMinutes(15))
            {
                throw new AuthenticationException("The request timestamp is outside the allowed time window.");
            }

            date = hasAmzDate ? string.Empty : timestamp!;
        }

        if (!string.Equals(accessKey, _options.AccessKey, StringComparison.Ordinal))
        {
            throw new AuthenticationException("The access key is invalid.");
        }

        var contentMd5 = headers.GetValueOrDefault("content-md5", string.Empty);
        var stringToSign = string.Join('\n',
            request.Method,
            contentMd5,
            headers.GetValueOrDefault("content-type", string.Empty),
            date,
            string.Concat(headers
                .Where(header => header.Key.StartsWith("x-amz-", StringComparison.Ordinal))
                .Select(header => $"{header.Key}:{header.Value}\n")) + CanonicalResource(request));
        var expected = HMACSHA1.HashData(
            Encoding.UTF8.GetBytes(_options.SecretKey),
            Encoding.UTF8.GetBytes(stringToSign));
        Span<byte> supplied = stackalloc byte[20];
        if (signature.Length != 28 ||
            !Convert.TryFromBase64String(signature, supplied, out var signatureLength) ||
            signatureLength != expected.Length ||
            !CryptographicOperations.FixedTimeEquals(expected, supplied))
        {
            throw new AuthenticationException("The calculated request signature does not match the supplied signature.");
        }

        if (contentMd5.Length > 0)
        {
            Span<byte> digest = stackalloc byte[16];
            if (!Convert.TryFromBase64String(contentMd5, digest, out var digestLength) || digestLength != 16)
            {
                throw new AuthenticationException("Content-MD5 must be a Base64-encoded MD5 digest.");
            }

            request.Body = new PayloadHashValidationStream(
                request.Body, HashAlgorithmName.MD5, digest.ToArray(), request.ContentLength,
                "The request payload hash does not match Content-MD5.");
        }

        // SigV2 presigning can carry signed content and x-amz-* headers in the query.
        foreach (var header in headers)
        {
            if (!request.Headers.ContainsKey(header.Key))
            {
                request.Headers[header.Key] = header.Value;
            }
        }

        return Task.CompletedTask;
    }

    private static bool TryParseDate(string? value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp) ||
        DateTimeOffset.TryParseExact(
            value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);

    private static string RequiredQuery(HttpRequest request, string name)
    {
        var values = request.Query[name];
        if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            throw new AuthenticationException($"{name} must be supplied exactly once.");
        }

        return values[0]!;
    }

    private static SortedDictionary<string, string> CanonicalHeaders(HttpRequest request, bool presigned)
    {
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in request.Headers)
        {
            var name = header.Key.ToLowerInvariant();
            if (!IsSignedHeader(name))
            {
                continue;
            }

            if (IsSingleValueHeader(name) && header.Value.Count != 1)
            {
                throw new AuthenticationException($"The header '{name}' must be supplied exactly once.");
            }

            headers.Add(name, string.Join(',', header.Value.Select(Unfold)));
        }

        if (presigned)
        {
            foreach (var parameter in request.Query)
            {
                var name = parameter.Key.ToLowerInvariant();
                if (!IsSignedHeader(name) || name == "date")
                {
                    continue;
                }

                if (IsSingleValueHeader(name) && parameter.Value.Count != 1)
                {
                    throw new AuthenticationException($"The query parameter '{name}' must be supplied exactly once.");
                }

                var value = string.Join(',', parameter.Value.Select(Unfold));
                if (headers.TryGetValue(name, out var headerValue) && headerValue != value)
                {
                    throw new AuthenticationException($"The header and query values for '{name}' must match.");
                }

                headers[name] = value;
            }
        }

        return headers;
    }

    private static bool IsSignedHeader(string name) =>
        name is "content-md5" or "content-type" or "date" ||
        name.StartsWith("x-amz-", StringComparison.Ordinal);

    private static bool IsSingleValueHeader(string name) =>
        name is "content-md5" or "content-type" or "date" or "x-amz-date";

    private static string Unfold(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split('\n').Select(line => line.Trim()));

    private static string CanonicalResource(HttpRequest request)
    {
        // SigV2 signs the escaped path exactly as sent, not the decoded ASP.NET path.
        var rawTarget = request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        var path = rawTarget is not null && rawTarget.StartsWith('/')
            ? rawTarget.Split('?', 2)[0]
            : request.PathBase.Add(request.Path).ToUriComponent();
        if (path.Length == 0)
        {
            path = "/";
        }

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in (request.QueryString.Value ?? string.Empty).TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = parameter.Split('=', 2);
            var name = Uri.UnescapeDataString(parts[0]);
            if (!SignedParameters.TryGetValue(name, out var canonicalName))
            {
                continue;
            }

            if (parts[0] != canonicalName)
            {
                throw new AuthenticationException("Signed query parameter names must use their canonical spelling.");
            }

            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            if (!parameters.TryAdd(parts[0], value))
            {
                throw new AuthenticationException($"The signed query parameter '{parts[0]}' must be supplied exactly once.");
            }
        }

        return parameters.Count == 0
            ? path
            : path + "?" + string.Join('&', parameters.Select(parameter =>
                parameter.Value.Length == 0 ? parameter.Key : $"{parameter.Key}={parameter.Value}"));
    }
}
