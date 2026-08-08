using System.Security.Cryptography;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Serialization.Tests;

public sealed class HashingStreamTests
{
    [Fact]
    public void Digest256PreservesTheExactSha256WireBytes()
    {
        byte[] payload = "shared digest contract"u8.ToArray();
        byte[] expected = SHA256.HashData(payload);

        Digest256 digest = Digest256.ComputeSha256(payload);
        Span<byte> wire = stackalloc byte[Digest256.Size];
        digest.Write(wire);

        Assert.Equal(expected, wire.ToArray());
        Assert.Equal(digest, Digest256.Read(wire));
        Assert.True(digest.FixedTimeEquals(expected));
        Assert.True(digest.FixedTimeEquals(Digest256.Read(expected)));
        Assert.False(digest.IsZero);

        Span<byte> prefix = stackalloc byte[Digest256.Prefix24Size];
        digest.WritePrefix24(prefix);
        Assert.Equal(expected.AsSpan(0, Digest256.Prefix24Size).ToArray(), prefix.ToArray());
        Assert.True(digest.FixedTimePrefix24Equals(Digest256.ReadPrefix24(prefix)));
    }

    [Fact]
    public void HashingWriteStreamWritesDirectlyAndCompletesOneDigest()
    {
        byte[] first = "write "u8.ToArray();
        byte[] second = "through"u8.ToArray();
        using var destination = new MemoryStream();
        using var stream = new HashingWriteStream(destination);

        stream.Write(first);
        Assert.Equal(first, destination.ToArray());
        stream.Write(second);

        byte[] expectedPayload = [.. first, .. second];
        Assert.Equal(expectedPayload, destination.ToArray());
        Assert.Equal(expectedPayload.Length, stream.BytesWritten);
        Assert.Equal(expectedPayload.Length, stream.Position);

        Digest256 digest = stream.CompleteDigest();
        Assert.Equal(SHA256.HashData(expectedPayload), digest.ToArray());
        Assert.False(stream.CanWrite);
        Assert.Throws<InvalidOperationException>(() => stream.WriteByte(0));
        Assert.Throws<InvalidOperationException>(() => stream.CompleteDigest());
    }

    [Fact]
    public void HashingWriteStreamRejectsLimitBeforeTouchingDestinationOrDigest()
    {
        byte[] accepted = [1, 2, 3];
        using var destination = new MemoryStream();
        using var stream = new HashingWriteStream(destination, maximumBytes: 4);

        stream.Write(accepted);
        Assert.Throws<InvalidDataException>(() => stream.Write(new byte[] { 4, 5 }));

        Assert.Equal(accepted, destination.ToArray());
        Assert.Equal(accepted.Length, stream.BytesWritten);
        Assert.Equal(SHA256.HashData(accepted), stream.CompleteDigest().ToArray());
    }

    [Fact]
    public void HashingReadStreamStopsAtDeclaredLengthAndLeavesFollowingBytesUnread()
    {
        byte[] payload = "bounded payload"u8.ToArray();
        byte[] following = "next envelope"u8.ToArray();
        using var source = new MemoryStream([.. payload, .. following], writable: false);
        using var stream = new HashingReadStream(source, payload.Length);
        byte[] destination = new byte[payload.Length + following.Length];

        int read = stream.Read(destination);

        Assert.Equal(payload.Length, read);
        Assert.Equal(payload, destination.AsSpan(0, read).ToArray());
        Assert.Equal(payload.Length, source.Position);
        Assert.Equal(0, stream.Remaining);
        Assert.Equal(-1, stream.ReadByte());
        Assert.Equal(SHA256.HashData(payload), stream.CompleteDigest().ToArray());
        Assert.False(stream.CanRead);
        Assert.Throws<InvalidOperationException>(() => stream.ReadByte());
    }

    [Fact]
    public void HashingReadStreamDrainsPayloadAndAuthenticatesDomainMetadata()
    {
        byte[] payload = new byte[12_345];
        new Random(0x5EED).NextBytes(payload);
        byte[] metadata = "durable metadata"u8.ToArray();
        using var source = new MemoryStream(payload, writable: false);
        using IncrementalHash hasher = IncrementalHash.CreateHMAC(
            HashAlgorithmName.SHA256,
            "shared secret with sufficient entropy"u8.ToArray());
        using var stream = new HashingReadStream(
            source,
            payload.Length,
            hasher,
            leaveHasherOpen: true);

        byte[] prefix = new byte[17];
        Assert.Equal(prefix.Length, stream.Read(prefix));
        Digest256 digest = stream.DrainAndCompleteDigest(metadata);

        using var expectedHasher = IncrementalHash.CreateHMAC(
            HashAlgorithmName.SHA256,
            "shared secret with sufficient entropy"u8.ToArray());
        expectedHasher.AppendData(payload);
        expectedHasher.AppendData(metadata);
        Assert.Equal(Digest256.Finish(expectedHasher), digest);
        Assert.Equal(payload.Length, stream.BytesRead);
        hasher.AppendData("still caller owned"u8);
    }

    [Fact]
    public void HashingReadStreamRejectsTruncationAndPrematureCompletion()
    {
        using var source = new MemoryStream(new byte[] { 1, 2 }, writable: false);
        using var stream = new HashingReadStream(source, length: 3);

        Assert.Throws<InvalidDataException>(() => stream.CompleteDigest());
        Span<byte> destination = stackalloc byte[3];
        Assert.Equal(2, stream.Read(destination));
        Assert.Throws<EndOfStreamException>(() => stream.ReadByte());
        Assert.Equal(1, stream.Remaining);
    }

    [Fact]
    public void HashingStreamsHonorDestinationAndSourceOwnership()
    {
        var destination = new TrackingMemoryStream();
        using (var output = new HashingWriteStream(destination, leaveOpen: false))
            output.WriteByte(1);
        Assert.True(destination.IsDisposed);

        var source = new TrackingMemoryStream([1]);
        using (var input = new HashingReadStream(source, length: 1, leaveOpen: false))
            Assert.Equal(1, input.ReadByte());
        Assert.True(source.IsDisposed);
    }

    [Fact]
    public void BoundedCountingWriteStreamValidatesBeforeForwardingAndRetainsNoFrame()
    {
        using var destination = new MemoryStream();
        var appends = new List<(long Current, int Count)>();
        using var stream = new BoundedCountingWriteStream(
            destination,
            maximumBytes: 3,
            validateAppend: (current, count) => appends.Add((current, count)));

        stream.Write(new byte[] { 1, 2 });
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => stream.Write(new byte[] { 3, 4 }));

        Assert.Contains("3-byte limit", error.Message, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 1, 2 }, destination.ToArray());
        Assert.Equal(2, stream.BytesWritten);
        Assert.Equal([(0L, 2)], appends);
    }

    [Fact]
    public void BoundedCountingReadStreamCountsOnlyCallerConsumedBytes()
    {
        using var source = new MemoryStream(new byte[] { 1, 2, 3 }, writable: false);
        using var stream = new BoundedCountingReadStream(source, maximumBytes: 2);

        Assert.Equal(1, stream.ReadByte());
        Assert.Equal(2, stream.ReadByte());
        Assert.Throws<InvalidDataException>(() => stream.ReadByte());

        Assert.Equal(2, stream.BytesRead);
        Assert.Equal(2, source.Position);
    }

    private sealed class TrackingMemoryStream : MemoryStream
    {
        internal TrackingMemoryStream()
        {
        }

        internal TrackingMemoryStream(byte[] buffer)
            : base(buffer, writable: false)
        {
        }

        internal bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
