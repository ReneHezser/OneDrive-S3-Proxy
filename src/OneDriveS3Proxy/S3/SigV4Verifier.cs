using System.Globalization;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace OneDriveS3Proxy.S3;

public sealed class SigV4Verifier(IOptions<ProxyOptions> options)
{
    private readonly S3Options _options = options.Value.S3;

    public Task VerifyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var presigned = request.Query.ContainsKey("X-Amz-Signature");
        var values = presigned ? ParsePresigned(request) : ParseAuthorization(request);

        ValidateCredential(values.Credential);
        var requestTime = ParseRequestTime(values.AmzDate);
        ValidateTime(requestTime, presigned ? values.Expires : null);
        ValidateSignedHeaders(request, values.SignedHeaders);

        var payloadHash = request.Headers["x-amz-content-sha256"].FirstOrDefault()
            ?? (presigned
                ? "UNSIGNED-PAYLOAD"
                : throw new AuthenticationException("x-amz-content-sha256 is required."));
        if (payloadHash != "UNSIGNED-PAYLOAD" &&
            (payloadHash.Length != 64 || payloadHash.Any(character => !Uri.IsHexDigit(character))))
        {
            throw new AuthenticationException("x-amz-content-sha256 must be a SHA-256 hash or UNSIGNED-PAYLOAD.");
        }

        var canonicalRequest = string.Join('\n',
            request.Method,
            CanonicalUri(request.Path),
            CanonicalQuery(request.QueryString.Value, presigned),
            CanonicalHeaders(request, values.SignedHeaders),
            values.SignedHeaders,
            payloadHash);
        var scope = values.Credential[(values.Credential.IndexOf('/') + 1)..];
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            values.AmzDate,
            scope,
            Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));
        var expected = Hex(Hmac(
            SigningKey(_options.SecretKey, requestTime.ToString("yyyyMMdd"), _options.Region),
            stringToSign));

        if (!FixedTimeHexEquals(expected, values.Signature))
        {
            throw new AuthenticationException("The calculated request signature does not match the supplied signature.");
        }

        if (payloadHash != "UNSIGNED-PAYLOAD")
        {
            request.Body = new PayloadHashValidationStream(request.Body, payloadHash, request.ContentLength);
        }

        return Task.CompletedTask;
    }

    private SigV4Values ParseAuthorization(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.FirstOrDefault();
        if (authorization is null || !authorization.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal))
        {
            throw new AuthenticationException("AWS Signature Version 4 authorization is required.");
        }

        var fields = authorization["AWS4-HMAC-SHA256 ".Length..]
            .Split(',', StringSplitOptions.TrimEntries)
            .Select(field => field.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        if (!fields.TryGetValue("Credential", out var credential) ||
            !fields.TryGetValue("SignedHeaders", out var signedHeaders) ||
            !fields.TryGetValue("Signature", out var signature))
        {
            throw new AuthenticationException("The authorization header is malformed.");
        }

        var amzDate = request.Headers["x-amz-date"].FirstOrDefault()
            ?? throw new AuthenticationException("x-amz-date is required.");
        return new SigV4Values(credential, signedHeaders, signature, amzDate, null);
    }

    private static SigV4Values ParsePresigned(HttpRequest request)
    {
        string Required(string name) => request.Query[name].FirstOrDefault()
            ?? throw new AuthenticationException($"{name} is required.");

        if (Required("X-Amz-Algorithm") != "AWS4-HMAC-SHA256")
        {
            throw new AuthenticationException("Only AWS4-HMAC-SHA256 presigned requests are supported.");
        }

        if (!int.TryParse(Required("X-Amz-Expires"), out var expires) || expires is < 1 or > 604800)
        {
            throw new AuthenticationException("X-Amz-Expires must be between 1 and 604800 seconds.");
        }

        return new SigV4Values(
            Required("X-Amz-Credential"),
            Required("X-Amz-SignedHeaders"),
            Required("X-Amz-Signature"),
            Required("X-Amz-Date"),
            expires);
    }

    private void ValidateCredential(string credential)
    {
        var parts = credential.Split('/');
        if (parts.Length != 5 ||
            parts[0] != _options.AccessKey ||
            parts[2] != _options.Region ||
            parts[3] != "s3" ||
            parts[4] != "aws4_request")
        {
            throw new AuthenticationException("The credential scope or access key is invalid.");
        }
    }

    private static DateTimeOffset ParseRequestTime(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            throw new AuthenticationException("The request timestamp is invalid.");
        }

        return parsed;
    }

    private static void ValidateTime(DateTimeOffset requestTime, int? expires)
    {
        var now = DateTimeOffset.UtcNow;
        var skew = TimeSpan.FromMinutes(15);
        var earliest = requestTime - skew;
        var latest = expires is null ? requestTime + skew : requestTime.AddSeconds(expires.Value);
        if (now < earliest || now > latest)
        {
            throw new AuthenticationException("The request timestamp is outside the allowed time window.");
        }
    }

    // Requests must sign the host, and every x-amz-* header the handlers act on must be signed;
    // the payload hash and date are already bound by the canonical request and string to sign.
    private static void ValidateSignedHeaders(HttpRequest request, string signedHeaders)
    {
        var signed = signedHeaders
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!signed.Contains("host"))
        {
            throw new AuthenticationException("The host header must be signed.");
        }

        foreach (var name in request.Headers.Keys)
        {
            if (name.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("x-amz-content-sha256", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("x-amz-date", StringComparison.OrdinalIgnoreCase) &&
                !signed.Contains(name))
            {
                throw new AuthenticationException($"The header '{name}' must be signed.");
            }
        }
    }

    private static string CanonicalUri(PathString path)
    {
        var value = path.Value ?? "/";
        return string.Join("/", value.Split('/').Select(AwsEncode));
    }

    private static string CanonicalQuery(string? rawQuery, bool excludeSignature)
    {
        var parsed = QueryHelpers.ParseQuery(rawQuery ?? string.Empty);
        return string.Join("&", parsed
            .Where(pair => !excludeSignature || !pair.Key.Equals("X-Amz-Signature", StringComparison.Ordinal))
            .SelectMany(pair => pair.Value.Select(value => (Name: AwsEncode(pair.Key), Value: AwsEncode(value ?? string.Empty))))
            .OrderBy(pair => pair.Name, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => $"{pair.Name}={pair.Value}"));
    }

    private static string CanonicalHeaders(HttpRequest request, string signedHeaders)
    {
        var builder = new StringBuilder();
        foreach (var name in signedHeaders.Split(';'))
        {
            var value = name == "host"
                ? request.Host.Value
                : request.Headers[name].ToString();
            if (string.IsNullOrEmpty(value))
            {
                throw new AuthenticationException($"The signed header '{name}' is missing.");
            }

            builder.Append(name);
            builder.Append(':');
            builder.Append(string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static byte[] SigningKey(string secret, string date, string region)
    {
        var dateKey = Hmac(Encoding.UTF8.GetBytes($"AWS4{secret}"), date);
        var regionKey = Hmac(dateKey, region);
        var serviceKey = Hmac(regionKey, "s3");
        return Hmac(serviceKey, "aws4_request");
    }

    private static byte[] Hmac(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private static string Sha256Hex(byte[] value) => Hex(SHA256.HashData(value));

    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();

    private static bool FixedTimeHexEquals(string expected, string actual)
    {
        if (expected.Length != actual.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(actual.ToLowerInvariant()));
    }

    private static string AwsEncode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var builder = new StringBuilder(bytes.Length);
        foreach (var valueByte in bytes)
        {
            var character = (char)valueByte;
            if ((character >= 'A' && character <= 'Z') ||
                (character >= 'a' && character <= 'z') ||
                (character >= '0' && character <= '9') ||
                character is '-' or '_' or '.' or '~')
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('%');
                builder.Append(valueByte.ToString("X2"));
            }
        }

        return builder.ToString();
    }

    private sealed record SigV4Values(
        string Credential,
        string SignedHeaders,
        string Signature,
        string AmzDate,
        int? Expires);

    private sealed class PayloadHashValidationStream(
        Stream inner,
        string expectedHash,
        long? expectedLength) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _bytesRead;
        private bool _validated;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            ValidateRead(buffer.AsSpan(offset, read), read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            ValidateRead(buffer.Span[..read], read);
            return read;
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void ValidateRead(ReadOnlySpan<byte> value, int count)
        {
            if (count > 0)
            {
                _hash.AppendData(value);
                _bytesRead += count;
            }

            if (!_validated && (count == 0 || expectedLength is not null && _bytesRead == expectedLength))
            {
                var actualHash = Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
                _hash.Dispose();
                _validated = true;
                if (!FixedTimeHexEquals(expectedHash, actualHash))
                {
                    throw new AuthenticationException(
                        "The request payload hash does not match x-amz-content-sha256.");
                }
            }
        }
    }
}
