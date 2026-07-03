using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class ComponentRegistryTests
{
    // ——————————————————————————————————————————————————
    // ComponentRegistry.Get 基本功能
    // ——————————————————————————————————————————————————

    [Fact]
    public void Get_ReturnsCorrectInfo_ForPosition()
    {
        // 触发注册
        int id = ComponentMetadata<Position>.Id;
        ref var info = ref ComponentRegistry.Get(id);

        Assert.Equal(id, info.Id);
        Assert.Equal(ComponentMetadata<Position>.Size, info.Size);
        Assert.Equal(StoragePath.Table, info.Storage);
        Assert.False(info.ContainsReferences);
    }

    [Fact]
    public void Get_ReturnsCorrectInfo_ForTag()
    {
        int id = ComponentMetadata<PlayerTag>.Id;
        ref var info = ref ComponentRegistry.Get(id);

        Assert.Equal(id, info.Id);
        Assert.Equal(StoragePath.Tag, info.Storage);
    }

    [Fact]
    public void Get_ThrowsOnInvalidId_Zero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ComponentRegistry.Get(0));
    }

    [Fact]
    public void Get_ThrowsOnInvalidId_Negative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ComponentRegistry.Get(-1));
    }

    [Fact]
    public void Get_ThrowsOnInvalidId_TooLarge()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ComponentRegistry.Get(999999));
    }

    // ——————————————————————————————————————————————————
    // ComponentOperations.CopyElement
    // ——————————————————————————————————————————————————

    [Fact]
    public unsafe void CopyElement_UnmanagedPosition_CopiesCorrectly()
    {
        int id = ComponentMetadata<Position>.Id;
        ref var info = ref ComponentRegistry.Get(id);

        var src = new Position[] { new() { X = 1.0f, Y = 2.0f }, new() { X = 3.0f, Y = 4.0f } };
        var dst = new Position[2];

        info.Operations.CopyElement(src, 0, dst, 1);

        Assert.Equal(1.0f, dst[1].X);
        Assert.Equal(2.0f, dst[1].Y);
    }

    [Fact]
    public unsafe void CopyElement_ManagedWithString_CopiesCorrectly()
    {
        int id = ComponentMetadata<NamedComponent>.Id;
        ref var info = ref ComponentRegistry.Get(id);

        var src = new NamedComponent[] { new() { Name = "hello", Id = 42 } };
        var dst = new NamedComponent[1];

        info.Operations.CopyElement(src, 0, dst, 0);

        Assert.Equal("hello", dst[0].Name);
        Assert.Equal(42, dst[0].Id);
    }

    [Fact]
    public unsafe void CopyElement_ManagedWithNullString_CopiesCorrectly()
    {
        int id = ComponentMetadata<NamedComponent>.Id;
        ref var info = ref ComponentRegistry.Get(id);

        var src = new NamedComponent[] { new() { Name = null!, Id = 7 } };
        var dst = new NamedComponent[] { new() { Name = "old", Id = 0 } };

        info.Operations.CopyElement(src, 0, dst, 0);

        Assert.Null(dst[0].Name);
        Assert.Equal(7, dst[0].Id);
    }

    // ——————————————————————————————————————————————————
    // ComponentOperations.SwapRemove
    // ——————————————————————————————————————————————————

    [Fact]
    public unsafe void SwapRemove_UnmanagedPosition_MovesLastToRemoved()
    {
        int id = ComponentMetadata<Position>.Id;
        ref var info = ref ComponentRegistry.Get(id);

        var arr = new Position[]
        {
            new() { X = 10, Y = 20 },  // idx 0 — to remove
            new() { X = 30, Y = 40 },  // idx 1
            new() { X = 50, Y = 60 },  // idx 2 — last
        };

        // 移除 idx 0，用 idx 2（lastIdx）填充
        info.Operations.SwapRemove(arr, 0, 2);

        Assert.Equal(50, arr[0].X);
        Assert.Equal(60, arr[0].Y);
        // idx 1 不变
        Assert.Equal(30, arr[1].X);
    }

    [Fact]
    public unsafe void SwapRemove_ManagedComponent_ClearsLastSlot()
    {
        int id = ComponentMetadata<NamedComponent>.Id;
        ref var info = ref ComponentRegistry.Get(id);

        var arr = new NamedComponent[]
        {
            new() { Name = "A", Id = 1 },  // idx 0 — to remove
            new() { Name = "B", Id = 2 },  // idx 1 — last
        };

        info.Operations.SwapRemove(arr, 0, 1);

        Assert.Equal("B", arr[0].Name);
        Assert.Equal(2, arr[0].Id);
        // 末位被清除（GC 友好）
        Assert.Null(arr[1].Name);
        Assert.Equal(0, arr[1].Id);
    }

    [Fact]
    public unsafe void SwapRemove_SameIndex_NoMove()
    {
        int id = ComponentMetadata<Position>.Id;
        ref var info = ref ComponentRegistry.Get(id);

        var arr = new Position[]
        {
            new() { X = 10, Y = 20 },
        };

        // removeIdx == lastIdx，不应该出问题
        info.Operations.SwapRemove(arr, 0, 0);
        // 数据不变（unmanaged 不清除）
        Assert.Equal(10, arr[0].X);
    }

    // ——————————————————————————————————————————————————
    // ComponentOperations.CreateArray
    // ——————————————————————————————————————————————————

    [Fact]
    public unsafe void CreateArray_ReturnsCorrectTypeAndLength()
    {
        int id = ComponentMetadata<Position>.Id;
        ref var info = ref ComponentRegistry.Get(id);

        var arr = Assert.IsType<Position[]>(info.Operations.CreateArray(64));

        Assert.Equal(64, arr.Length);
    }

    [Fact]
    public unsafe void CreateArray_ManagedType_ReturnsCorrectType()
    {
        int id = ComponentMetadata<NamedComponent>.Id;
        ref var info = ref ComponentRegistry.Get(id);

        var arr = Assert.IsType<NamedComponent[]>(info.Operations.CreateArray(32));

        Assert.Equal(32, arr.Length);
    }
}
