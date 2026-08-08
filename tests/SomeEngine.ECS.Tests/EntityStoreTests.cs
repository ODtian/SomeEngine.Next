using SomeEngine.ECS.Entities;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class EntityStoreTests
{
    // ——————————————————————————————————————————————————
    // Entity 基本语义
    // ——————————————————————————————————————————————————

    [Fact]
    public void Entity_Null_IsDefault()
    {
        Assert.Equal(default(Entity), Entity.Null);
        Assert.Equal(0, Entity.Null.Index);
        Assert.Equal(0, Entity.Null.Generation);
    }

    [Fact]
    public void Entity_Equals_SameValues()
    {
        var a = new Entity(5, 3);
        var b = new Entity(5, 3);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a.Equals(b));
        Assert.True(a.Equals((object)b));
    }

    [Fact]
    public void Entity_NotEquals_DifferentIndex()
    {
        var a = new Entity(1, 0);
        var b = new Entity(2, 0);
        Assert.True(a != b);
        Assert.False(a == b);
    }

    [Fact]
    public void Entity_NotEquals_DifferentGeneration()
    {
        var a = new Entity(1, 0);
        var b = new Entity(1, 1);
        Assert.True(a != b);
    }

    [Fact]
    public void Entity_GetHashCode_ConsistentForEqualValues()
    {
        var a = new Entity(42, 7);
        var b = new Entity(42, 7);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Entity_GetHashCode_DifferentForDifferentValues()
    {
        var a = new Entity(1, 0);
        var b = new Entity(2, 0);
        // 极小概率碰撞是允许的，但通常不同
        // 这个测试只验证基本合理性
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Entity_CanBeUsedAsDictionaryKey()
    {
        var dict = new Dictionary<Entity, string>();
        var e1 = new Entity(1, 0);
        var e2 = new Entity(2, 0);
        dict[e1] = "first";
        dict[e2] = "second";
        Assert.Equal("first", dict[e1]);
        Assert.Equal("second", dict[e2]);
    }

    // ——————————————————————————————————————————————————
    // EntityStore.Allocate
    // ——————————————————————————————————————————————————

    [Fact]
    public void Allocate_ReturnsIndex_GreaterThanZero()
    {
        var store = new EntityStore();
        var id = store.Allocate();
        Assert.True(id.Index >= 1, $"Expected Index >= 1, got {id.Index}");
    }

    [Fact]
    public void Allocate_100_AllUnique()
    {
        var store = new EntityStore();
        var ids = new HashSet<Entity>();
        for (int i = 0; i < 100; i++)
        {
            var id = store.Allocate();
            Assert.True(ids.Add(id), $"Duplicate Entity: {id}");
        }
        Assert.Equal(100, ids.Count);
    }

    [Fact]
    public void Allocate_IndicesAreSequential()
    {
        var store = new EntityStore();
        var prev = store.Allocate();
        Assert.Equal(1, prev.Index);
        for (int i = 2; i <= 10; i++)
        {
            var cur = store.Allocate();
            Assert.Equal(i, cur.Index);
        }
    }

    // ——————————————————————————————————————————————————
    // EntityStore.IsAlive
    // ——————————————————————————————————————————————————

    [Fact]
    public void IsAlive_True_ForFreshEntity()
    {
        var store = new EntityStore();
        var id = store.Allocate();
        Assert.True(store.IsAlive(id));
    }

    [Fact]
    public void IsAlive_False_AfterFree()
    {
        var store = new EntityStore();
        var id = store.Allocate();
        store.Free(id);
        Assert.False(store.IsAlive(id));
    }

    [Fact]
    public void IsAlive_False_ForNullEntity()
    {
        var store = new EntityStore();
        Assert.False(store.IsAlive(Entity.Null));
    }

    [Fact]
    public void IsAlive_False_ForStaleGeneration()
    {
        var store = new EntityStore();
        var id1 = store.Allocate();
        store.Free(id1);
        var id2 = store.Allocate(); // 复用同一 index
        Assert.Equal(id1.Index, id2.Index);
        Assert.False(store.IsAlive(id1)); // 旧 generation 失效
        Assert.True(store.IsAlive(id2));  // 新 generation 有效
    }

    // ——————————————————————————————————————————————————
    // EntityStore.Free
    // ——————————————————————————————————————————————————

    [Fact]
    public void Free_ThenAllocate_ReusesIndex_WithIncrementedGeneration()
    {
        var store = new EntityStore();
        var id1 = store.Allocate();
        int originalIndex = id1.Index;
        int originalGen = id1.Generation;

        store.Free(id1);
        var id2 = store.Allocate();

        Assert.Equal(originalIndex, id2.Index);
        Assert.Equal(originalGen + 1, id2.Generation);
    }

    [Fact]
    public void Free_SameEntity_Twice_ThrowsException()
    {
        var store = new EntityStore();
        var id = store.Allocate();
        store.Free(id);

        Assert.Throws<InvalidOperationException>(() => store.Free(id));
    }

    // ——————————————————————————————————————————————————
    // EntityStore.GetRecord
    // ——————————————————————————————————————————————————

    [Fact]
    public void GetRecord_ThrowsForDeadEntity()
    {
        var store = new EntityStore();
        var id = store.Allocate();
        store.Free(id);

        Assert.Throws<InvalidOperationException>(() => store.GetRecord(id));
    }

    [Fact]
    public void GetRecord_ReturnsWriter_AndCanModify()
    {
        var store = new EntityStore();
        var id = store.Allocate();

        EntityRecordWriter record = store.GetRecord(id);
        record.RowInChunk = 42;

        EntityRecordWriter record2 = store.GetRecord(id);
        Assert.Equal(42, record2.RowInChunk);
    }

    [Fact]
    public void GetGeneration_ThrowsForSentinelAndUnallocatedIndex()
    {
        var store = new EntityStore();
        store.Allocate();

        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetGeneration(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetGeneration(store.Count + 1));
    }

    [Fact]
    public void SerializationRestore_ReservesLiveSlotsAndRejectsDeadPreservedAllocation()
    {
        var store = new EntityStore();
        store.BeginSerializationRestore(3);
        store.AppendSerializationSlot(1, 2, isAlive: false);
        store.AppendSerializationSlot(2, 4, isAlive: true);
        store.AppendSerializationSlot(3, 6, isAlive: false);
        store.CompleteSerializationRestore();

        _ = store.AllocatePreserved(new Entity(2, 4));

        var firstReused = store.Allocate();
        var secondReused = store.Allocate();

        Assert.Equal(new Entity(1, 2), firstReused);
        Assert.Equal(new Entity(3, 6), secondReused);

        var deadStore = new EntityStore();
        deadStore.BeginSerializationRestore(1);
        deadStore.AppendSerializationSlot(1, 2, isAlive: false);
        deadStore.CompleteSerializationRestore();

        Assert.Throws<InvalidOperationException>(() =>
            deadStore.AllocatePreserved(new Entity(1, 2)));
    }

    [Fact]
    public void SerializationRestore_InvalidStateTransitionsFailClosed()
    {
        var store = new EntityStore();

        Assert.Throws<ArgumentOutOfRangeException>(() => store.BeginSerializationRestore(-1));
        Assert.Throws<InvalidOperationException>(() => store.AppendSerializationSlot(1, 0, isAlive: false));
        Assert.Throws<InvalidOperationException>(store.CompleteSerializationRestore);

        store.BeginSerializationRestore(2);
        Assert.Throws<InvalidOperationException>(() => store.BeginSerializationRestore(2));
        Assert.Throws<InvalidOperationException>(() => store.AppendSerializationSlot(2, 0, isAlive: false));
        Assert.Throws<InvalidOperationException>(() => store.AppendSerializationSlot(1, -1, isAlive: false));
        Assert.Throws<InvalidOperationException>(store.CompleteSerializationRestore);

        store.AppendSerializationSlot(1, 3, isAlive: false);
        store.AppendSerializationSlot(2, 5, isAlive: true);
        store.CompleteSerializationRestore();

        Assert.Throws<InvalidOperationException>(() => store.AppendSerializationSlot(3, 0, isAlive: false));
        Assert.Throws<InvalidOperationException>(store.CompleteSerializationRestore);
        _ = store.AllocatePreserved(new Entity(2, 5));
        Assert.Equal(new Entity(1, 3), store.Allocate());
    }

    // ——————————————————————————————————————————————————
    // 大批量 Allocate → Free → Allocate 复用
    // ——————————————————————————————————————————————————

    [Fact]
    public void BulkAllocate_Free_Reallocate_FreeListCorrect()
    {
        var store = new EntityStore(16);
        var ids = new Entity[200];

        // 分配 200 个
        for (int i = 0; i < 200; i++)
            ids[i] = store.Allocate();

        // 释放前 100 个
        for (int i = 0; i < 100; i++)
            store.Free(ids[i]);

        // 验证前 100 个已死
        for (int i = 0; i < 100; i++)
            Assert.False(store.IsAlive(ids[i]));

        // 验证后 100 个仍活
        for (int i = 100; i < 200; i++)
            Assert.True(store.IsAlive(ids[i]));

        // 重新分配 100 个——应该复用已释放的 index
        var reused = new Entity[100];
        for (int i = 0; i < 100; i++)
            reused[i] = store.Allocate();

        // 验证复用的 index 在 [1, 100] 范围内
        var reusedIndices = new HashSet<int>(reused.Select(e => e.Index));
        Assert.Equal(100, reusedIndices.Count); // 全不重复
        foreach (var idx in reusedIndices)
        {
            Assert.True(idx >= 1 && idx <= 100,
                $"Expected reused index in [1,100], got {idx}");
        }

        // 验证复用后 generation 递增
        foreach (var r in reused)
        {
            Assert.True(store.IsAlive(r));
            Assert.Equal(1, r.Generation); // 第一次回收后 generation=1
        }
    }

    [Fact]
    public void MultipleRecycles_GenerationKeepsIncrementing()
    {
        var store = new EntityStore();
        var id = store.Allocate();
        int index = id.Index;

        for (int gen = 0; gen < 10; gen++)
        {
            Assert.Equal(gen, id.Generation);
            Assert.True(store.IsAlive(id));
            store.Free(id);
            Assert.False(store.IsAlive(id));
            id = store.Allocate();
            Assert.Equal(index, id.Index);
        }
        Assert.Equal(10, id.Generation);
    }
}
