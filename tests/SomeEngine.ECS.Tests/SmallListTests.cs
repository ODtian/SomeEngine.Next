using SomeEngine.ECS.Collections;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class SmallListTests
{
    [Fact]
    public void SmallList_DefaultValue_UsesInlinePathAndEnumeratesInOrder()
    {
        SmallList<string> list = default;

        list.Add("a");
        list.Add("b");
        list.Add("c");

        Assert.Equal(3, list.Count);
        Assert.Equal(new[] { "a", "b", "c" }, list.AsSpan().ToArray());

        var enumerated = new List<string>();
        foreach (var item in list)
            enumerated.Add(item);

        Assert.Equal(new[] { "a", "b", "c" }, enumerated);
    }

    [Fact]
    public void SmallList_FourthItem_UsesOverflowPathButPreservesOrder()
    {
        SmallList<int> list = default;

        list.Add(10);
        list.Add(20);
        list.Add(30);
        list.Add(40);

        Assert.Equal(4, list.Count);
        Assert.Equal(new[] { 10, 20, 30, 40 }, list.AsSpan().ToArray());
        Assert.Equal(30, list[2]);
    }

    [Fact]
    public void SmallList_RemoveAt_PreservesOrder_ForInlineAndOverflow()
    {
        SmallList<int> inline = default;
        inline.Add(1);
        inline.Add(2);
        inline.Add(3);
        inline.RemoveAt(1);

        Assert.Equal(new[] { 1, 3 }, inline.AsSpan().ToArray());

        SmallList<int> overflow = default;
        overflow.Add(1);
        overflow.Add(2);
        overflow.Add(3);
        overflow.Add(4);
        overflow.RemoveAt(1);

        Assert.Equal(new[] { 1, 3, 4 }, overflow.AsSpan().ToArray());
    }

    [Fact]
    public void SmallList_Clear_AllowsReuse()
    {
        SmallList<string> list = default;
        list.Add("before");
        list.Add("clear");

        list.Clear();
        Assert.Equal(0, list.Count);

        list.Add("after");
        Assert.Equal(new[] { "after" }, list.AsSpan().ToArray());
    }

    [Fact]
    public void SmallList_Indexer_AllowsInPlaceMutationForValueTypes()
    {
        SmallList<int> list = default;
        list.Add(1);
        list.Add(2);
        list.Add(3);

        ref var item = ref list[1];
        item = 99;

        Assert.Equal(new[] { 1, 99, 3 }, list.AsSpan().ToArray());
    }

    [Fact]
    public void SmallList_RemoveStable_RemovesByValueAndPreservesOrder()
    {
        SmallList<int> list = default;
        list.Add(1);
        list.Add(2);
        list.Add(3);

        Assert.True(list.RemoveStable(2));
        Assert.False(list.RemoveStable(9));
        Assert.Equal(new[] { 1, 3 }, list.AsSpan().ToArray());
    }

    [Fact]
    public void SmallList_RemoveSwapBack_RemovesByValueWithoutPreservingOrder()
    {
        SmallList<int> list = default;
        list.Add(1);
        list.Add(2);
        list.Add(3);

        Assert.True(list.RemoveSwapBack(1));
        Assert.False(list.RemoveSwapBack(9));
        Assert.Equal(new[] { 3, 2 }, list.AsSpan().ToArray());
    }
}
