namespace SomeEngine.Serialization.Containers;

/// <summary>
/// Root-authenticated logical reference to one external document chunk. Physical offsets,
/// compression, hashes, and alignment remain exclusively in the document directory.
/// </summary>
public readonly struct BinaryChunkRef : IEquatable<BinaryChunkRef>
{
    public BinaryChunkRef(ulong key, long decodedLength)
    {
        if (key == 0)
            throw new ArgumentOutOfRangeException(nameof(key), "Binary chunk key zero is reserved.");
        ArgumentOutOfRangeException.ThrowIfNegative(decodedLength);
        Key = key;
        DecodedLength = decodedLength;
    }

    public ulong Key { get; }
    public long DecodedLength { get; }
    public bool IsValid => Key != 0 && DecodedLength >= 0;

    public bool Equals(BinaryChunkRef other)
        => Key == other.Key && DecodedLength == other.DecodedLength;

    public override bool Equals(object? obj)
        => obj is BinaryChunkRef other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Key, DecodedLength);

    public static bool operator ==(BinaryChunkRef left, BinaryChunkRef right) => left.Equals(right);
    public static bool operator !=(BinaryChunkRef left, BinaryChunkRef right) => !left.Equals(right);

    public override string ToString() => IsValid ? $"0x{Key:X16}:{DecodedLength}" : "Invalid";
}
