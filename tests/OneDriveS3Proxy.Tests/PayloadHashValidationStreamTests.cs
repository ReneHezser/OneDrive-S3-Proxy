using System.Security.Authentication;
using System.Security.Cryptography;
using OneDriveS3Proxy.S3;

namespace OneDriveS3Proxy.Tests;

public sealed class PayloadHashValidationStreamTests
{
    public static TheoryData<string, bool> ReadModes => new()
    {
        { "MD5", false },
        { "MD5", true },
        { "SHA256", false },
        { "SHA256", true }
    };

    [Theory]
    [MemberData(nameof(ReadModes))]
    public async Task Read_AcceptsExactLengthAndRepeatedEndOfStream(string algorithm, bool asynchronous)
    {
        var payload = "payload"u8.ToArray();
        using var inner = new MemoryStream(payload);
        using var stream = CreateStream(inner, payload, algorithm, payload.Length);

        Assert.Equal(3, await ReadAsync(stream, new byte[3], asynchronous));
        Assert.Equal(4, await ReadAsync(stream, new byte[4], asynchronous));
        Assert.Equal(0, await ReadAsync(stream, new byte[1], asynchronous));
        Assert.Equal(0, await ReadAsync(stream, new byte[1], asynchronous));
    }

    [Theory]
    [MemberData(nameof(ReadModes))]
    public async Task Read_RejectsAppendedBytesAfterValidatingDeclaredLength(string algorithm, bool asynchronous)
    {
        var payload = "payload"u8.ToArray();
        using var inner = new MemoryStream("payload-extra"u8.ToArray());
        using var stream = CreateStream(inner, payload, algorithm, payload.Length);

        Assert.Equal(payload.Length, await ReadAsync(stream, new byte[payload.Length], asynchronous));
        await Assert.ThrowsAsync<AuthenticationException>(async () =>
            await ReadAsync(stream, new byte[1], asynchronous));
    }

    [Theory]
    [MemberData(nameof(ReadModes))]
    public async Task Read_RejectsReadThatExceedsDeclaredLength(string algorithm, bool asynchronous)
    {
        var payload = "payload"u8.ToArray();
        using var inner = new MemoryStream(payload);
        using var stream = CreateStream(inner, payload, algorithm, payload.Length - 1);

        await Assert.ThrowsAsync<AuthenticationException>(async () =>
            await ReadAsync(stream, new byte[payload.Length], asynchronous));
    }

    [Theory]
    [MemberData(nameof(ReadModes))]
    public async Task Read_RejectsTruncationEvenWhenHashMatches(string algorithm, bool asynchronous)
    {
        var payload = "payload"u8.ToArray();
        using var inner = new MemoryStream(payload);
        using var stream = CreateStream(inner, payload, algorithm, payload.Length + 1);

        Assert.Equal(payload.Length, await ReadAsync(stream, new byte[payload.Length], asynchronous));
        await Assert.ThrowsAsync<AuthenticationException>(async () =>
            await ReadAsync(stream, new byte[1], asynchronous));
    }

    [Theory]
    [MemberData(nameof(ReadModes))]
    public async Task Read_RejectsHashMismatchAtDeclaredLengthWithoutAnEofRead(string algorithm, bool asynchronous)
    {
        var payload = "payload"u8.ToArray();
        using var inner = new MemoryStream("changed"u8.ToArray());
        using var stream = CreateStream(inner, payload, algorithm, payload.Length);

        await Assert.ThrowsAsync<AuthenticationException>(async () =>
            await ReadAsync(stream, new byte[payload.Length], asynchronous));
    }

    [Theory]
    [MemberData(nameof(ReadModes))]
    public async Task Read_DoesNotTreatZeroLengthReadsAsEndOfStream(string algorithm, bool asynchronous)
    {
        var payload = "payload"u8.ToArray();
        using var inner = new MemoryStream(payload);
        using var stream = CreateStream(inner, payload, algorithm, payload.Length);

        Assert.Equal(0, await ReadAsync(stream, [], asynchronous));
        Assert.Equal(3, await ReadAsync(stream, new byte[3], asynchronous));
        Assert.Equal(0, await ReadAsync(stream, [], asynchronous));
        Assert.Equal(4, await ReadAsync(stream, new byte[4], asynchronous));
        Assert.Equal(0, await ReadAsync(stream, new byte[1], asynchronous));
    }

    [Theory]
    [MemberData(nameof(ReadModes))]
    public async Task Read_AcceptsEmptyPayload(string algorithm, bool asynchronous)
    {
        using var inner = new MemoryStream();
        using var stream = CreateStream(inner, [], algorithm, 0);

        Assert.Equal(0, await ReadAsync(stream, new byte[1], asynchronous));
        Assert.Equal(0, await ReadAsync(stream, new byte[1], asynchronous));
    }

    [Theory]
    [MemberData(nameof(ReadModes))]
    public async Task Read_RejectsNonemptyPayloadWithZeroDeclaredLength(string algorithm, bool asynchronous)
    {
        using var inner = new MemoryStream("extra"u8.ToArray());
        using var stream = CreateStream(inner, [], algorithm, 0);

        await Assert.ThrowsAsync<AuthenticationException>(async () =>
            await ReadAsync(stream, new byte[1], asynchronous));
    }

    [Theory]
    [MemberData(nameof(ReadModes))]
    public async Task Read_ValidatesUnknownLengthAtEndOfStream(string algorithm, bool asynchronous)
    {
        var payload = "payload"u8.ToArray();
        using var inner = new MemoryStream(payload);
        using var stream = CreateStream(inner, payload, algorithm, null);

        Assert.Equal(0, await ReadAsync(stream, [], asynchronous));
        Assert.Equal(payload.Length, await ReadAsync(stream, new byte[payload.Length], asynchronous));
        Assert.Equal(0, await ReadAsync(stream, new byte[1], asynchronous));
        Assert.Equal(0, await ReadAsync(stream, new byte[1], asynchronous));
    }

    [Theory]
    [MemberData(nameof(ReadModes))]
    public async Task Read_RejectsTamperedUnknownLengthAtEndOfStream(string algorithm, bool asynchronous)
    {
        var payload = "payload"u8.ToArray();
        using var inner = new MemoryStream("payload-extra"u8.ToArray());
        using var stream = CreateStream(inner, payload, algorithm, null);

        Assert.Equal(inner.Length, await ReadAsync(stream, new byte[(int)inner.Length], asynchronous));
        await Assert.ThrowsAsync<AuthenticationException>(async () =>
            await ReadAsync(stream, new byte[1], asynchronous));
    }

    private static PayloadHashValidationStream CreateStream(
        Stream inner, byte[] payload, string algorithm, long? expectedLength)
    {
        var name = new HashAlgorithmName(algorithm);
        using var hash = IncrementalHash.CreateHash(name);
        hash.AppendData(payload);
        return new PayloadHashValidationStream(inner, name, hash.GetHashAndReset(), expectedLength, "Hash mismatch.");
    }

    private static async Task<int> ReadAsync(Stream stream, byte[] buffer, bool asynchronous) =>
        asynchronous
            ? await stream.ReadAsync(buffer.AsMemory())
            : stream.Read(buffer, 0, buffer.Length);
}
