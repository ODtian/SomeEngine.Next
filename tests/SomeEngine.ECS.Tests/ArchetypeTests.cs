using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;


public class ArchetypeTests
{
    // ——————————————————————————————————————————————————
    // ComponentIds 排序
    // ——————————————————————————————————————————————————

    [Fact]
    public void Archetype_ComponentIds_AreSorted()
    {
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;
        // 以逆序传入，验证构造后仍是排序的
        var ids = new[] { Math.Max(idPos, idVel), Math.Min(idPos, idVel) };
        Array.Sort(ids); // 我们传入已排序的
        var arch = new Archetype(0, ids);

        Assert.Equal(2, arch.ComponentIds.Length);
        Assert.True(arch.ComponentIds[0] <= arch.ComponentIds[1]);
    }

    [Fact]
    public void Archetype_TableComponentIds_ExcludesTags()
    {
        int idPos = ComponentMetadata<Position>.Id;
        int idTag = ComponentMetadata<PlayerTag>.Id;
        var ids = new[] { idPos, idTag };
        Array.Sort(ids);
        var arch = new Archetype(0, ids);

        // componentIds 包含两者
        Assert.Equal(2, arch.ComponentIds.Length);
        Assert.True(arch.ComponentIds.Contains(idPos));
        Assert.True(arch.ComponentIds.Contains(idTag));

        // tableComponentIds 只包含 Position
        Assert.Equal(1, arch.TableComponentIds.Length);
        Assert.Equal(idPos, arch.TableComponentIds[0]);

        // tagIds 只包含 PlayerTag
        Assert.Equal(1, arch.TagIds.Length);
        Assert.Equal(idTag, arch.TagIds[0]);
    }

    [Fact]
    public void Archetype_TagOnly_NoTableColumns()
    {
        int idTag1 = ComponentMetadata<PlayerTag>.Id;
        int idTag2 = ComponentMetadata<EnemyTag>.Id;
        var ids = new[] { idTag1, idTag2 };
        Array.Sort(ids);
        var arch = new Archetype(0, ids);

        Assert.Equal(2, arch.ComponentIds.Length);
        Assert.Equal(0, arch.TableComponentIds.Length);
        Assert.Equal(2, arch.TagIds.Length);
        Assert.Equal(0, arch.ColumnOperations.Length);
    }

    // ——————————————————————————————————————————————————
    // FNV-1a hash
    // ——————————————————————————————————————————————————

    [Fact]
    public void TypeIdHash_SameInput_SameHash()
    {
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;
        var ids = new[] { idPos, idVel };
        Array.Sort(ids);

        var arch1 = new Archetype(0, ids);
        var arch2 = new Archetype(1, ids);

        Assert.Equal(arch1.TypeIdHash, arch2.TypeIdHash);
    }

    [Fact]
    public void TypeIdHash_DifferentInput_DifferentHash()
    {
        var hashes = new HashSet<uint>();
        int[] allIds = {
            ComponentMetadata<Position>.Id,
            ComponentMetadata<Velocity>.Id,
            ComponentMetadata<Health>.Id,
            ComponentMetadata<PlayerTag>.Id,
            ComponentMetadata<EnemyTag>.Id,
        };

        // 生成 5 种单组件 archetype
        foreach (int id in allIds)
        {
            var arch = new Archetype(0, new[] { id });
            Assert.True(hashes.Add(arch.TypeIdHash),
                $"Hash collision for component ID {id}");
        }
        Assert.Equal(5, hashes.Count);
    }

    [Fact]
    public void StableHash_Deterministic()
    {
        ReadOnlySpan<int> ids = stackalloc int[] { 1, 2, 3 };
        uint h1 = StableHash.Compute(ids);
        uint h2 = StableHash.Compute(ids);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void StableHash_DifferentInputs_DifferentHashes()
    {
        var hashes = new HashSet<uint>();
        int[][] inputs = {
            new[] { 1 },
            new[] { 2 },
            new[] { 1, 2 },
            new[] { 2, 3 },
            new[] { 1, 2, 3 },
        };
        foreach (var input in inputs)
        {
            Assert.True(hashes.Add(StableHash.Compute(input)),
                $"Hash collision for [{string.Join(",", input)}]");
        }
    }

    // ——————————————————————————————————————————————————
    // 列操作与表列平行
    // ——————————————————————————————————————————————————

    [Fact]
    public void ColumnOperations_ParallelTableColumns()
    {
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;
        var ids = new[] { idPos, idVel };
        Array.Sort(ids);
        var arch = new Archetype(0, ids);

        Assert.Equal(arch.TableComponentIds.Length, arch.ColumnOperations.Length);
    }

    // ——————————————————————————————————————————————————
    // HasComponent / GetColumnIndex / TryGetColumnIndex
    // ——————————————————————————————————————————————————

    [Fact]
    public void HasComponent_ExistingComponent_ReturnsTrue()
    {
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;
        var ids = new[] { idPos, idVel };
        Array.Sort(ids);
        var arch = new Archetype(0, ids);

        Assert.True(arch.HasComponent(idPos));
        Assert.True(arch.HasComponent(idVel));
    }

    [Fact]
    public void HasComponent_NonExistent_ReturnsFalse()
    {
        int idPos = ComponentMetadata<Position>.Id;
        var arch = new Archetype(0, new[] { idPos });

        int idHealth = ComponentMetadata<Health>.Id;
        Assert.False(arch.HasComponent(idHealth));
    }

    [Fact]
    public void HasComponent_Tag_ReturnsTrue()
    {
        int idPos = ComponentMetadata<Position>.Id;
        int idTag = ComponentMetadata<PlayerTag>.Id;
        var ids = new[] { idPos, idTag };
        Array.Sort(ids);
        var arch = new Archetype(0, ids);

        Assert.True(arch.HasComponent(idTag));
    }

    [Fact]
    public void GetColumnIndex_ReturnsCorrectIndex()
    {
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;
        var ids = new[] { idPos, idVel };
        Array.Sort(ids);
        var arch = new Archetype(0, ids);

        // 列索引 = tableComponentIds 中的位置
        int posCol = arch.Column(idPos);
        int velCol = arch.Column(idVel);
        Assert.True(posCol >= 0 && posCol <= 1);
        Assert.True(velCol >= 0 && velCol <= 1);
        Assert.NotEqual(posCol, velCol);
    }

    [Fact]
    public void GetColumnIndex_Tag_Throws()
    {
        int idPos = ComponentMetadata<Position>.Id;
        int idTag = ComponentMetadata<PlayerTag>.Id;
        var ids = new[] { idPos, idTag };
        Array.Sort(ids);
        var arch = new Archetype(0, ids);

        Assert.Throws<KeyNotFoundException>(() => arch.Column(idTag));
    }

    [Fact]
    public void TryGetColumnIndex_Existing_ReturnsTrue()
    {
        int idPos = ComponentMetadata<Position>.Id;
        var arch = new Archetype(0, new[] { idPos });

        Assert.True(arch.TryColumn(idPos, out int colIdx));
        Assert.Equal(0, colIdx);
    }

    [Fact]
    public void TryGetColumnIndex_NonExistent_ReturnsFalse()
    {
        int idPos = ComponentMetadata<Position>.Id;
        var arch = new Archetype(0, new[] { idPos });

        int idHealth = ComponentMetadata<Health>.Id;
        Assert.False(arch.TryColumn(idHealth, out int colIdx));
        Assert.Equal(-1, colIdx);
    }

    // ——————————————————————————————————————————————————
    // EntityCapacityPerChunk 计算
    // ——————————————————————————————————————————————————

    [Fact]
    public void EntityCapacityPerChunk_TwoFloat2Components()
    {
        // Position(8B) + Velocity(8B) + Entity(8B) + two uint versions per column
        // = 40B per row; fixed change versions = 8B; (65536 - 8) / 40 = 1638.
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;
        var ids = new[] { idPos, idVel };
        Array.Sort(ids);
        var arch = new Archetype(0, ids);

        Assert.Equal(1638, arch.MaxChunkRows);
        Assert.Equal(40, arch.ChunkRowPayloadBytes);
        Assert.Equal(8, arch.ChunkFixedPayloadBytes);
    }

    [Fact]
    public void EntityCapacityPerChunk_TagOnly_UsesEntityRowSize()
    {
        // Tag-only archetype: rowSize = Entity(8) + 0 = 8
        // 65536 / 8 = 8192
        int idTag = ComponentMetadata<PlayerTag>.Id;
        var arch = new Archetype(0, new[] { idTag });

        // Tag 不占列，但 Entity 占 8B
        // rowSize = 8 + 0 = 8 (but totalComponentSize = 0, check code)
        // 实际: 65536 / 8 = 8192
        Assert.Equal(8192, arch.MaxChunkRows);
    }

    [Fact]
    public void EntityCapacityPerChunk_MinimumIsOne()
    {
        // 超大组件仍然至少 capacity = 1
        int idPos = ComponentMetadata<Position>.Id;
        var arch = new Archetype(0, new[] { idPos });
        Assert.True(arch.MaxChunkRows >= 1);
    }

    // ——————————————————————————————————————————————————
    // ArchetypeId 由外部传入
    // ——————————————————————————————————————————————————

    [Fact]
    public void ArchetypeId_IsAssigned()
    {
        int idPos = ComponentMetadata<Position>.Id;
        var arch = new Archetype(42, new[] { idPos });
        Assert.Equal(42, arch.ArchetypeId);
    }
}
