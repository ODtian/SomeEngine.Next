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

}
