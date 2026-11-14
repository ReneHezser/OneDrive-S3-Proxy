using System.Security.Cryptography;

namespace OneDriveS3Proxy.Performance;

internal sealed class PatternStream(long length) : Stream
{
    private static readonly byte[] Block = CreateBlock();
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, length - _position);
        var written = 0;
        while (written < count)
        {
            var offset = (int)(_position % Block.Length);
            var chunk = Math.Min(count - written, Block.Length - offset);
            Block.AsSpan(offset, chunk).CopyTo(buffer[written..]);
            written += chunk;
            _position += chunk;
        }

        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private static byte[] CreateBlock()
    {
        var block = new byte[64 * 1024];
        new Random(42).NextBytes(block);
        return block;
    }
}

internal sealed record PayloadDigest(string Sha256, string Md5)
{
    public static async Task<PayloadDigest> CreateAsync(long length, CancellationToken cancellationToken)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var stream = new PatternStream(length);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            sha256.AppendData(buffer, 0, read);
            md5.AppendData(buffer, 0, read);
        }

        return new PayloadDigest(
            Convert.ToHexStringLower(sha256.GetHashAndReset()),
            Convert.ToHexStringLower(md5.GetHashAndReset()));
    }
}
