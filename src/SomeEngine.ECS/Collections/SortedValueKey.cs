namespace SomeEngine.ECS.Collections;

/// <summary>
/// Owns a stable sorted value sequence and its precomputed FNV-1a hash.
/// </summary>
internal readonly struct SortedValueKey : IEquatable<SortedValueKey>
{
    private readonly int[]? _ids;
    private readonly uint _hash;

    public SortedValueKey(ReadOnlySpan<int> sortedIds)
    {
        _ids = new int[sortedIds.Length];
        sortedIds.CopyTo(_ids);
        _hash = StableHash.Compute(sortedIds);
    }

    public ReadOnlySpan<int> Ids => _ids;

    public bool Equals(SortedValueKey other) =>
        _hash == other._hash && Ids.SequenceEqual(other.Ids);

    public override bool Equals(object? obj) => obj is SortedValueKey other && Equals(other);

    public override int GetHashCode() => (int)_hash;

    public static bool operator ==(SortedValueKey left, SortedValueKey right) => left.Equals(right);

    public static bool operator !=(SortedValueKey left, SortedValueKey right) => !left.Equals(right);

    public override string ToString() => $"[{string.Join(",", _ids ?? [])}]";
}

internal sealed class SortedValueComparer :
    IEqualityComparer<SortedValueKey>,
    IAlternateEqualityComparer<ReadOnlySpan<int>, SortedValueKey>
{
    public static readonly SortedValueComparer Instance = new();

    private SortedValueComparer()
    {
    }

    public bool Equals(SortedValueKey x, SortedValueKey y) => x.Equals(y);

    public int GetHashCode(SortedValueKey obj) => obj.GetHashCode();

    public SortedValueKey Create(ReadOnlySpan<int> alternate) =>
        new(alternate);

    public bool Equals(ReadOnlySpan<int> alternate, SortedValueKey other) =>
        alternate.SequenceEqual(other.Ids);

    public int GetHashCode(ReadOnlySpan<int> alternate) =>
        (int)StableHash.Compute(alternate);
}

