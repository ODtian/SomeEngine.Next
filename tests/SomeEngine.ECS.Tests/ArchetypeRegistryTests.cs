using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class ArchetypeRegistryTests
{
    // ——————————————————————————————————————————————————
    // GetOrCreate
    // ——————————————————————————————————————————————————

    [Fact]
    public void GetOrCreate_CreatesNewArchetype()
    {
        var registry = new ArchetypeRegistry();
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;
        var ids = new[] { idPos, idVel };
        Array.Sort(ids);

        var arch = registry.GetOrCreate(ids);
        Assert.NotNull(arch);
        Assert.Equal(0, arch.ArchetypeId); // 第一个id=0
    }

    [Fact]
    public void GetOrCreate_SameIds_ReturnsSameObject()
    {
        var registry = new ArchetypeRegistry();
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;
        var ids = new[] { idPos, idVel };
        Array.Sort(ids);

        var first = registry.GetOrCreate(ids);
        var second = registry.GetOrCreate(ids);
        Assert.Same(first, second);
    }

    [Fact]
    public void GetOrCreate_DifferentIds_ReturnsDifferentObjects()
    {
        var registry = new ArchetypeRegistry();
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;
        int idHp = ComponentMetadata<Health>.Id;

        var arch1 = registry.GetOrCreate(new[] { idPos, idVel }.OrderBy(x => x).ToArray());
        var arch2 = registry.GetOrCreate(new[] { idPos, idHp }.OrderBy(x => x).ToArray());

        Assert.NotSame(arch1, arch2);
    }

    [Fact]
    public void GetOrCreate_ArchetypeId_Increments()
    {
        var registry = new ArchetypeRegistry();
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;
        int idHp = ComponentMetadata<Health>.Id;

        var a0 = registry.GetOrCreate(new[] { idPos });
        var a1 = registry.GetOrCreate(new[] { idVel });
        var a2 = registry.GetOrCreate(new[] { idHp });

        Assert.Equal(0, a0.ArchetypeId);
        Assert.Equal(1, a1.ArchetypeId);
        Assert.Equal(2, a2.ArchetypeId);
    }

    [Fact]
    public void AllArchetypes_ReturnsAll()
    {
        var registry = new ArchetypeRegistry();
        registry.GetOrCreate(new[] { ComponentMetadata<Position>.Id });
        registry.GetOrCreate(new[] { ComponentMetadata<Velocity>.Id });
        registry.GetOrCreate(new[] { ComponentMetadata<Health>.Id });

        Assert.Equal(3, registry.AllArchetypes.Length);
    }

    // ——————————————————————————————————————————————————
    // 边缘缓存: AddEdge
    // ——————————————————————————————————————————————————

    [Fact]
    public void GetOrCreateAddEdge_FirstCall_Computes()
    {
        var registry = new ArchetypeRegistry();
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;

        var srcArch = registry.GetOrCreate(new[] { idPos });
        var edge = registry.AddEdge(srcArch, idVel);

        Assert.NotNull(edge.Target);
        Assert.True(edge.Target.HasComponent(idPos));
        Assert.True(edge.Target.HasComponent(idVel));
    }

    [Fact]
    public void GetOrCreateAddEdge_SecondCall_HitsCache()
    {
        var registry = new ArchetypeRegistry();
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;

        var srcArch = registry.GetOrCreate(new[] { idPos });
        var first = registry.AddEdge(srcArch, idVel);
        var second = registry.AddEdge(srcArch, idVel);

        Assert.Same(first.Target, second.Target);
    }

    // ——————————————————————————————————————————————————
    // 边缘缓存: RemoveEdge
    // ——————————————————————————————————————————————————

    [Fact]
    public void GetOrCreateRemoveEdge_FirstCall_Computes()
    {
        var registry = new ArchetypeRegistry();
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;
        var ids = new[] { idPos, idVel };
        Array.Sort(ids);

        var srcArch = registry.GetOrCreate(ids);
        var edge = registry.RemoveEdge(srcArch, idVel);

        Assert.NotNull(edge.Target);
        Assert.True(edge.Target.HasComponent(idPos));
        Assert.False(edge.Target.HasComponent(idVel));
    }

    [Fact]
    public void GetOrCreateRemoveEdge_SecondCall_HitsCache()
    {
        var registry = new ArchetypeRegistry();
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;
        var ids = new[] { idPos, idVel };
        Array.Sort(ids);

        var srcArch = registry.GetOrCreate(ids);
        var first = registry.RemoveEdge(srcArch, idVel);
        var second = registry.RemoveEdge(srcArch, idVel);

        Assert.Same(first.Target, second.Target);
    }

    [Fact]
    public void ResolveStructuralTransitionToInclude_SingleId_MatchesCachedEdge()
    {
        var registry = new ArchetypeRegistry();
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;

        var srcArch = registry.GetOrCreate(new[] { idPos });
        var edge = registry.AddEdge(srcArch, idVel);
        var plan = registry.IncludeTransition(srcArch, new[] { idVel });

        Assert.Same(edge.Target, plan.Target);
        AssertSameBacking(edge.SharedColumns, plan.SharedColumns);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void ResolveStructuralTransitionToInclude_MultiId_SecondCall_HitsCacheWithoutAllocation()
    {
        var registry = new ArchetypeRegistry();
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;
        int idHp = ComponentMetadata<Health>.Id;

        var srcArch = registry.GetOrCreate(new[] { idPos });
        var componentIds = new[] { idVel, idHp };
        Array.Sort(componentIds);

        var first = registry.IncludeTransition(srcArch, componentIds);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var second = registry.IncludeTransition(srcArch, componentIds);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Same(first.Target, second.Target);
        AssertSameBacking(first.SharedColumns, second.SharedColumns);
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void ResolveStructuralTransitionToInclude_LargeDescriptorUsesBoundedScratchStorage()
    {
        var registry = new ArchetypeRegistry();
        int componentId = ComponentMetadata<Position>.Id;
        var source = registry.GetOrCreate(new[] { componentId });
        int[] repeatedDescriptor = Enumerable.Repeat(componentId, 4_096).ToArray();

        StructuralTransition transition = registry.IncludeTransition(source, repeatedDescriptor);

        Assert.Same(source, transition.Target);
    }

    // ——————————————————————————————————————————————————
    // SharedColumnMapping 正确性
    // ——————————————————————————————————————————————————

    [Fact]
    public void SharedColumnMapping_SrcAB_DstABC_MapsAAndB()
    {
        var registry = new ArchetypeRegistry();
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;
        int idHp = ComponentMetadata<Health>.Id;

        var ids_ab = new[] { idPos, idVel };
        Array.Sort(ids_ab);
        var archAB = registry.GetOrCreate(ids_ab);

        // 通过 AddEdge 计算 AB → ABC
        var edge = registry.AddEdge(archAB, idHp);

        // SharedColumns 应包含 Position 和 Velocity 的映射
        Assert.Equal(2, edge.SharedColumns.Length);

        // 验证映射的 src 列和 dst 列都是有效的
        var srcCols = new HashSet<int>();
        var dstCols = new HashSet<int>();
        for (int index = 0; index < edge.SharedColumns.Length; index++)
        {
            srcCols.Add(edge.SharedColumns[index].SourceColumnIndex);
            dstCols.Add(edge.SharedColumns[index].DestinationColumnIndex);
        }
        Assert.Equal(2, srcCols.Count);
        Assert.Equal(2, dstCols.Count);
    }

    // ——————————————————————————————————————————————————
    // 双向回填
    // ——————————————————————————————————————————————————

    [Fact]
    public void AddEdge_BidirectionalFill_RemoveEdgeAlsoFilled()
    {
        var registry = new ArchetypeRegistry();
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;

        var archA = registry.GetOrCreate(new[] { idPos });
        var addEdge = registry.AddEdge(archA, idVel);
        var archAB = addEdge.Target;

        // archAB 的 removeEdges 中应该自动有 idVel → archA
        Assert.True(archAB.TryGetRemoveTransition(idVel, out StructuralTransition reverse));
        Assert.Same(archA, reverse.Target);
    }

    [Fact]
    public void RemoveEdge_BidirectionalFill_AddEdgeAlsoFilled()
    {
        var registry = new ArchetypeRegistry();
        int idPos = ComponentMetadata<Position>.Id;
        int idVel = ComponentMetadata<Velocity>.Id;
        var ids = new[] { idPos, idVel };
        Array.Sort(ids);

        var archAB = registry.GetOrCreate(ids);
        var removeEdge = registry.RemoveEdge(archAB, idVel);
        var archA = removeEdge.Target;

        // archA 的 addEdges 中应该自动有 idVel → archAB
        Assert.True(archA.TryGetAddTransition(idVel, out StructuralTransition reverse));
        Assert.Same(archAB, reverse.Target);
    }

    // ——————————————————————————————————————————————————
    // InsertSorted / RemoveSorted
    // ——————————————————————————————————————————————————

    [Fact]
    public void InsertSorted_InsertsInOrder()
    {
        var result = ArchetypeRegistry.InsertSorted(new[] { 1, 3, 5 }, 4);
        Assert.Equal(new[] { 1, 3, 4, 5 }, result);
    }

    [Fact]
    public void InsertSorted_AtBeginning()
    {
        var result = ArchetypeRegistry.InsertSorted(new[] { 2, 4 }, 1);
        Assert.Equal(new[] { 1, 2, 4 }, result);
    }

    [Fact]
    public void InsertSorted_AtEnd()
    {
        var result = ArchetypeRegistry.InsertSorted(new[] { 1, 2 }, 5);
        Assert.Equal(new[] { 1, 2, 5 }, result);
    }

    [Fact]
    public void RemoveSorted_RemovesCorrectly()
    {
        var result = ArchetypeRegistry.RemoveSorted(new[] { 1, 3, 5 }, 3);
        Assert.Equal(new[] { 1, 5 }, result);
    }

    [Fact]
    public void RemoveSorted_NotFound_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ArchetypeRegistry.RemoveSorted(new[] { 1, 3 }, 2));
    }

    private static void AssertSameBacking<T>(ReadOnlySpan<T> first, ReadOnlySpan<T> second)
    {
        Assert.Equal(first.Length, second.Length);
        if (first.IsEmpty)
            return;

        Assert.True(Unsafe.AreSame(
            ref Unsafe.AsRef(in first[0]),
            ref Unsafe.AsRef(in second[0])));
    }
}
