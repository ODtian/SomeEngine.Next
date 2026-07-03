using SomeEngine.ECS.Collections;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class SortedValueKeyTests
{
    [Fact]
    public void StableHash_Deterministic()
    {
        ReadOnlySpan<int> ids = stackalloc int[] { 5, 10, 15 };
        uint h1 = StableHash.Compute(ids);
        uint h2 = StableHash.Compute(ids);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void StableHash_DifferentInputs_DifferentHashes()
    {
        var hashes = new HashSet<uint>();
        int[][] inputs =
        [
            [1],
            [2],
            [1, 2],
            [2, 3],
            [1, 2, 3],
        ];

        foreach (var input in inputs)
        {
            Assert.True(
                hashes.Add(StableHash.Compute(input)),
                $"Hash collision for [{string.Join(",", input)}]");
        }

        Assert.Equal(5, hashes.Count);
    }

    [Fact]
    public void SortedValueKey_Equal_SameIds()
    {
        var a = new SortedValueKey(new[] { 1, 2, 3 });
        var b = new SortedValueKey(new[] { 1, 2, 3 });
        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void SortedValueKey_NotEqual_DifferentIds()
    {
        var a = new SortedValueKey(new[] { 1, 2 });
        var b = new SortedValueKey(new[] { 1, 3 });
        Assert.False(a.Equals(b));
        Assert.True(a != b);
    }

    [Fact]
    public void SortedValueKey_CreateOwnedCopy_EqualsFromArray()
    {
        var arr = new[] { 5, 10 };
        var fromArr = new SortedValueKey(arr);
        var fromSpan = SortedValueKey.CreateOwnedCopy(arr.AsSpan());
        Assert.True(fromArr == fromSpan);
    }

    [Fact]
    public void SortedValueKey_CanBeUsedAsDictionaryKey()
    {
        var dict = new Dictionary<SortedValueKey, string>();
        var k1 = new SortedValueKey(new[] { 1, 2 });
        var k2 = new SortedValueKey(new[] { 3, 4 });
        dict[k1] = "a";
        dict[k2] = "b";
        Assert.Equal("a", dict[k1]);
        Assert.Equal("b", dict[k2]);
        Assert.Equal("a", dict[new SortedValueKey(new[] { 1, 2 })]);
    }
}
