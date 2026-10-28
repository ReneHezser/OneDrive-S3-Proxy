using System.Security.Authentication;
using System.Security.Cryptography;

namespace OneDriveS3Proxy.S3;

internal sealed class PayloadHashValidationStream(
    Stream inner,
    HashAlgorithmName algorithm,
    byte[] expectedHash,
    long? expectedLength,
    string mismatchMessage) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(algorithm);
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
        ValidateRead(buffer.AsSpan(offset, read), count);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        ValidateRead(buffer.Span[..read], buffer.Length);
        return read;
    }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ValidateRead(ReadOnlySpan<byte> value, int requestedCount)
    {
        if (requestedCount == 0)
        {
            return;
        }

        _bytesRead += value.Length;
        if (expectedLength is { } length &&
            (_bytesRead > length || value.IsEmpty && _bytesRead != length))
        {
            throw new AuthenticationException("The request payload length does not match Content-Length.");
        }

        if (_validated)
        {
            if (!value.IsEmpty)
            {
                throw new AuthenticationException("The request body contains bytes beyond the authenticated payload.");
            }

            return;
        }

        if (!value.IsEmpty)
        {
            _hash.AppendData(value);
        }

        // Upload sessions can stop reading at Content-Length without requesting EOF.
        if (value.IsEmpty || expectedLength is not null && _bytesRead == expectedLength)
        {
            var actualHash = _hash.GetHashAndReset();
            _hash.Dispose();
            _validated = true;
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            {
                throw new AuthenticationException(mismatchMessage);
            }
        }
    }
}
