namespace SomeEngine.ECS.Collections;

/// <summary>
/// Sorted int[] 的包装 key 类型。持有预计算的 FNV-1a hash，实现 IEquatable。
/// 可直接作为 Dictionary key。
/// </summary>
internal readonly struct SortedValueKey : IEquatable<SortedValueKey>
{
    /// <summary>内部持有的 sorted int 数组。</summary>
    public readonly int[] Ids;
    private readonly uint _hash;

    public SortedValueKey(int[] sortedIds)
    {
        Ids = sortedIds;
        _hash = StableHash.Compute(sortedIds);
    }

    public static SortedValueKey CreateOwnedCopy(ReadOnlySpan<int> sortedIds) =>
        new(sortedIds.ToArray());

    public bool Equals(SortedValueKey other) =>
        _hash == other._hash && Ids.AsSpan().SequenceEqual(other.Ids.AsSpan());

    public override bool Equals(object? obj) => obj is SortedValueKey other && Equals(other);

    public override int GetHashCode() => (int)_hash;

    public static bool operator ==(SortedValueKey left, SortedValueKey right) => left.Equals(right);

    public static bool operator !=(SortedValueKey left, SortedValueKey right) => !left.Equals(right);

    public override string ToString() => $"[{string.Join(",", Ids)}]";
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
        SortedValueKey.CreateOwnedCopy(alternate);

    public bool Equals(ReadOnlySpan<int> alternate, SortedValueKey other) =>
        alternate.SequenceEqual(other.Ids);

    public int GetHashCode(ReadOnlySpan<int> alternate) =>
        (int)StableHash.Compute(alternate);
}

