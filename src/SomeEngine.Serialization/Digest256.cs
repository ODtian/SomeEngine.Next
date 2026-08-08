using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SomeEngine.Serialization;

/// <summary>
/// Inline 256-bit digest shared by binary documents, asset authentication, checkpoints, and
/// durable envelopes. The value owns no managed byte backing; callers materialize bytes only when
/// a final wire field requires them.
/// </summary>
public readonly record struct Digest256(
    ulong Part0,
    ulong Part1,
    ulong Part2,
    ulong Part3)
{
    public const int Size = 32;
    public const int Prefix24Size = 24;

    public bool IsZero => (Part0 | Part1 | Part2 | Part3) == 0;

    public static Digest256 Read(ReadOnlySpan<byte> source)
    {
        if (source.Length != Size)
            throw new ArgumentException($"A 256-bit digest must contain exactly {Size} bytes.", nameof(source));
        return new Digest256(
            BinaryPrimitives.ReadUInt64LittleEndian(source[0..8]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[8..16]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[16..24]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[24..32]));
    }

    public static Digest256 ReadPrefix24(ReadOnlySpan<byte> source)
    {
        if (source.Length != Prefix24Size)
        {
            throw new ArgumentException(
                $"A 192-bit digest prefix must contain exactly {Prefix24Size} bytes.",
                nameof(source));
        }
        return new Digest256(
            BinaryPrimitives.ReadUInt64LittleEndian(source[0..8]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[8..16]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[16..24]),
            0);
    }

    public static Digest256 Finish(IncrementalHash hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        Span<byte> bytes = stackalloc byte[Size];
        if (!hash.TryGetHashAndReset(bytes, out int written) || written != Size)
            throw new CryptographicException("The incremental digest did not produce 32 bytes.");
        return Read(bytes);
    }

    public static Digest256 ComputeSha256(ReadOnlySpan<byte> source)
    {
        Span<byte> bytes = stackalloc byte[Size];
        if (!SHA256.TryHashData(source, bytes, out int written) || written != Size)
            throw new CryptographicException("SHA-256 did not produce a 32-byte digest.");
        return Read(bytes);
    }

    public void Write(Span<byte> destination)
    {
        if (destination.Length != Size)
            throw new ArgumentException($"A digest destination must contain exactly {Size} bytes.", nameof(destination));
        BinaryPrimitives.WriteUInt64LittleEndian(destination[0..8], Part0);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..16], Part1);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..24], Part2);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..32], Part3);
    }

    public void WritePrefix24(Span<byte> destination)
    {
        if (destination.Length != Prefix24Size)
        {
            throw new ArgumentException(
                $"A digest-prefix destination must contain exactly {Prefix24Size} bytes.",
                nameof(destination));
        }
        BinaryPrimitives.WriteUInt64LittleEndian(destination[0..8], Part0);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..16], Part1);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..24], Part2);
    }

    public byte[] ToArray()
    {
        var bytes = new byte[Size];
        Write(bytes);
        return bytes;
    }

    public bool FixedTimeEquals(Digest256 other) =>
        ((Part0 ^ other.Part0) |
         (Part1 ^ other.Part1) |
         (Part2 ^ other.Part2) |
         (Part3 ^ other.Part3)) == 0;

    public bool FixedTimeEquals(ReadOnlySpan<byte> other)
    {
        if (other.Length != Size)
            return false;
        Span<byte> expected = stackalloc byte[Size];
        Write(expected);
        return CryptographicOperations.FixedTimeEquals(expected, other);
    }

    public bool FixedTimePrefix24Equals(Digest256 other) =>
        ((Part0 ^ other.Part0) |
         (Part1 ^ other.Part1) |
         (Part2 ^ other.Part2)) == 0;
}
