using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class ChunkTests
{
    private static ComponentOperations[] CreateColumnOperations(params int[] componentIds)
    {
        var operations = new ComponentOperations[componentIds.Length];
        for (int i = 0; i < componentIds.Length; i++)
        {
            ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentIds[i]);
            operations[i] = info.Operations;
        }
        return operations;
    }

    // ——————————————————————————————————————————————————
    // Capacity 计算（做在 Archetype 中，这里验证端到端）
    // ——————————————————————————————————————————————————

    [Fact]
    public void Capacity_PositionVelocity_IncludesRowVersions()
    {
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;
        var ids = new[] { idPos, idVel };
        Array.Sort(ids);
        var arch = new Archetype(0, ids);

        // (65536 - two change-version uints) /
        // (Entity + Position + Velocity + Add/Write uints for both columns) = 1638.
        Assert.Equal(1638, arch.MaxChunkRows);

        var chunk = new Chunk(arch.MaxChunkRows, arch.ColumnOperations);
        Assert.Equal(1638, chunk.Capacity);
        Assert.True(
            arch.ChunkFixedPayloadBytes +
            ((long)chunk.Capacity * arch.ChunkRowPayloadBytes) <= 64 * 1024);
    }

    [Fact]
    public void Capacity_Minimum_IsOne()
    {
        // 创建一个 chunk 容量至少为 1
        var operations = CreateColumnOperations(ComponentMetadata<Position>.Id);
        var chunk = new Chunk(1, operations);
        Assert.Equal(1, chunk.Capacity);
    }

    // ——————————————————————————————————————————————————
    // AllocateRow
    // ——————————————————————————————————————————————————

    [Fact]
    public void AllocateRow_IncreasesCount()
    {
        var operations = CreateColumnOperations(ComponentMetadata<Position>.Id);
        var chunk = new Chunk(10, operations);

        Assert.Equal(0, chunk.Count);
        var e1 = TestEntity.Create(1);
        int row = chunk.AllocateRow(e1);

        Assert.Equal(0, row);
        Assert.Equal(1, chunk.Count);
        Assert.Equal(e1, chunk.Entities[0]);
    }

    [Fact]
    public void AllocateRow_SequentialRows()
    {
        var operations = CreateColumnOperations(ComponentMetadata<Position>.Id);
        var chunk = new Chunk(10, operations);

        for (int i = 0; i < 5; i++)
        {
            int row = chunk.AllocateRow(TestEntity.Create(i + 1));
            Assert.Equal(i, row);
        }
        Assert.Equal(5, chunk.Count);
    }

    // ——————————————————————————————————————————————————
    // WriteComponent / ReadComponent
    // ——————————————————————————————————————————————————

    [Fact]
    public void WriteRead_Unmanaged_Roundtrip()
    {
        var operations = CreateColumnOperations(ComponentMetadata<Position>.Id);
        var chunk = new Chunk(10, operations);
        chunk.AllocateRow(TestEntity.Create(1));

        var pos = new Position { X = 1.5f, Y = 2.5f };
        chunk.WriteComponent(0, 0, pos);

        var read = chunk.ReadComponent<Position>(0, 0);
        Assert.Equal(1.5f, read.X);
        Assert.Equal(2.5f, read.Y);
    }

    [Fact]
    public void WriteRead_Managed_Roundtrip()
    {
        var operations = CreateColumnOperations(ComponentMetadata<NamedComponent>.Id);
        var chunk = new Chunk(10, operations);
        chunk.AllocateRow(TestEntity.Create(1));

        var named = new NamedComponent { Name = "test", Id = 42 };
        chunk.WriteComponent(0, 0, named);

        var read = chunk.ReadComponent<NamedComponent>(0, 0);
        Assert.Equal("test", read.Name);
        Assert.Equal(42, read.Id);
    }

    [Fact]
    public void GetComponentRef_ModifiesInPlace()
    {
        var operations = CreateColumnOperations(ComponentMetadata<Position>.Id);
        var chunk = new Chunk(10, operations);
        chunk.AllocateRow(TestEntity.Create(1));
        chunk.WriteComponent(0, 0, new Position { X = 1.0f, Y = 2.0f });

        ref var pos = ref chunk.GetComponentRef<Position>(0, 0);
        pos.X = 99.0f;

        var read = chunk.ReadComponent<Position>(0, 0);
        Assert.Equal(99.0f, read.X);
        Assert.Equal(2.0f, read.Y);
    }

    // ——————————————————————————————————————————————————
    // RemoveRow — swap-remove
    // ——————————————————————————————————————————————————

    [Fact]
    public void RemoveRow_MiddleRow_SwapsLastToRemoved()
    {
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;
        var ids = new[] { idPos, idVel };
        Array.Sort(ids);
        var arch = new Archetype(0, ids);
        var chunk = new Chunk(arch.MaxChunkRows, arch.ColumnOperations);

        // 添加 3 个 entity
        var e1 = TestEntity.Create(1);
        var e2 = TestEntity.Create(2);
        var e3 = TestEntity.Create(3);
        chunk.AllocateRow(e1);
        chunk.AllocateRow(e2);
        chunk.AllocateRow(e3);

        int posCol = arch.Column(idPos);
        int velCol = arch.Column(idVel);

        chunk.WriteComponent(posCol, 0, new Position { X = 1, Y = 10 });
        chunk.WriteComponent(posCol, 1, new Position { X = 2, Y = 20 });
        chunk.WriteComponent(posCol, 2, new Position { X = 3, Y = 30 });

        chunk.WriteComponent(velCol, 0, new Velocity { X = 100, Y = 1000 });
        chunk.WriteComponent(velCol, 1, new Velocity { X = 200, Y = 2000 });
        chunk.WriteComponent(velCol, 2, new Velocity { X = 300, Y = 3000 });

        // 删除 row 0（e1）→ e3 的数据应移到 row 0
        var movedEntity = chunk.RemoveRow(0, arch.ColumnOperations);

        Assert.Equal(2, chunk.Count);
        Assert.Equal(e3, movedEntity); // e3 被移到 row 0

        // row 0 现在应有 e3 的数据
        Assert.Equal(e3, chunk.Entities[0]);
        var pos0 = chunk.ReadComponent<Position>(posCol, 0);
        Assert.Equal(3, pos0.X);
        Assert.Equal(30, pos0.Y);
        var vel0 = chunk.ReadComponent<Velocity>(velCol, 0);
        Assert.Equal(300, vel0.X);

        // row 1 应有 e2 的数据（未动）
        Assert.Equal(e2, chunk.Entities[1]);
        var pos1 = chunk.ReadComponent<Position>(posCol, 1);
        Assert.Equal(2, pos1.X);
    }

    [Fact]
    public void RemoveRow_LastRow_ReturnsNull()
    {
        var operations = CreateColumnOperations(ComponentMetadata<Position>.Id);
        var chunk = new Chunk(10, operations);

        chunk.AllocateRow(TestEntity.Create(1));
        chunk.AllocateRow(TestEntity.Create(2));
        chunk.WriteComponent(0, 0, new Position { X = 1 });
        chunk.WriteComponent(0, 1, new Position { X = 2 });

        // 删除最后一行
        var moved = chunk.RemoveRow(1, new[] { operations[0] });
        Assert.Equal(Entity.Null, moved);
        Assert.Equal(1, chunk.Count);

        // row 0 不受影响
        var pos = chunk.ReadComponent<Position>(0, 0);
        Assert.Equal(1, pos.X);
    }

    [Fact]
    public void RemoveRow_OnlyRow_CountBecomesZero()
    {
        var operations = CreateColumnOperations(ComponentMetadata<Position>.Id);
        var chunk = new Chunk(10, operations);

        chunk.AllocateRow(TestEntity.Create(1));
        chunk.WriteComponent(0, 0, new Position { X = 42 });

        var moved = chunk.RemoveRow(0, operations);
        Assert.Equal(Entity.Null, moved);
        Assert.Equal(0, chunk.Count);
    }

    // ——————————————————————————————————————————————————
    // IsFull
    // ——————————————————————————————————————————————————

    [Fact]
    public void IsFull_WhenCountEqualsCapacity()
    {
        var operations = CreateColumnOperations(ComponentMetadata<Position>.Id);
        var chunk = new Chunk(3, operations);

        Assert.False(chunk.IsFull);
        chunk.AllocateRow(TestEntity.Create(1));
        Assert.False(chunk.IsFull);
        chunk.AllocateRow(TestEntity.Create(2));
        Assert.False(chunk.IsFull);
        chunk.AllocateRow(TestEntity.Create(3));
        Assert.True(chunk.IsFull);
    }

    // ——————————————————————————————————————————————————
    // 多列 swap-remove 一致性
    // ——————————————————————————————————————————————————

    [Fact]
    public void RemoveRow_MultipleColumns_AllConsistent()
    {
        int idPos = ComponentMetadata<Position>.Id;
        int idHealth = ComponentMetadata<Health>.Id;
        var ids = new[] { idPos, idHealth };
        Array.Sort(ids);
        var arch = new Archetype(0, ids);
        var chunk = new Chunk(10, arch.ColumnOperations);

        // 填充 4 个 entity
        for (int i = 0; i < 4; i++)
        {
            chunk.AllocateRow(TestEntity.Create(i + 1));
            int posCol = arch.Column(idPos);
            int hpCol = arch.Column(idHealth);
            chunk.WriteComponent(posCol, i, new Position { X = i * 10, Y = i * 100 });
            chunk.WriteComponent(hpCol, i, new Health { Value = (i + 1) * 100 });
        }

        // 删除 row 1 → row 3 数据应移到 row 1
        var moved = chunk.RemoveRow(1, arch.ColumnOperations);
        Assert.Equal(3, chunk.Count);
        Assert.Equal(TestEntity.Create(4), moved);

        int posCol2 = arch.Column(idPos);
        int hpCol2 = arch.Column(idHealth);

        // row 1 现在应有 entity 4 的数据
        var pos1 = chunk.ReadComponent<Position>(posCol2, 1);
        Assert.Equal(30, pos1.X);
        var hp1 = chunk.ReadComponent<Health>(hpCol2, 1);
        Assert.Equal(400, hp1.Value);
    }

    [Fact]
    public void RemoveRow_ManagedComponent_ClearsTrailingSlot()
    {
        var operations = CreateColumnOperations(ComponentMetadata<NamedComponent>.Id);
        var chunk = new Chunk(4, operations);

        chunk.AllocateRow(TestEntity.Create(1));
        chunk.AllocateRow(TestEntity.Create(2));
        chunk.AllocateRow(TestEntity.Create(3));

        chunk.WriteComponent(0, 0, new NamedComponent { Name = "first", Id = 1 });
        chunk.WriteComponent(0, 1, new NamedComponent { Name = "second", Id = 2 });
        chunk.WriteComponent(0, 2, new NamedComponent { Name = "third", Id = 3 });

        var moved = chunk.RemoveRow(0, operations);

        Assert.Equal(TestEntity.Create(3), moved);
        Assert.Equal(2, chunk.Count);

        var movedData = chunk.ReadComponent<NamedComponent>(0, 0);
        Assert.Equal("third", movedData.Name);
        Assert.Equal(3, movedData.Id);

        NamedComponent trailing = chunk.ComponentRows<NamedComponent>(0)[2];
        Assert.Null(trailing.Name);
        Assert.Equal(0, trailing.Id);
    }
}
