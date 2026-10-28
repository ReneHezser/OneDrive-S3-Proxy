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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ValidateRead(ReadOnlySpan<byte> value, int count)
    {
        if (_validated)
        {
            return;
        }

        if (count > 0)
        {
            _hash.AppendData(value);
            _bytesRead += count;
        }

        if (count == 0 || expectedLength is not null && _bytesRead == expectedLength)
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
