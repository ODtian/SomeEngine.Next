using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Per-World access shared by relationship jobs that can move entities between archetypes or
/// publish protected relationship components.
/// </summary>
public static class RelationshipJobAccess
{
    private static readonly WorldStorageResourceKey s_topology =
        new(WorldStorageKind.Topology, ComponentId: 0);

    /// <summary>
    /// Declares a read of World table topology while resolving canonical relationship components.
    /// Immutable Children/adjacency generation snapshots do not require this access.
    /// </summary>
    public static JobResourceAccess TopologyRead(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return WorldStorageJobResources.Read(world, s_topology);
    }

    /// <summary>
    /// Declares a write to the World storage shared by hierarchy and relation topology writers.
    /// </summary>
    /// <remarks>
    /// Manual schedules must combine this access with the domain- or payload-qualified write.
    /// The typed Schedule methods on <see cref="HierarchyJobAccess{TDomain}"/> and
    /// <see cref="RelationJobAccess{T}"/> do that automatically.
    /// </remarks>
    public static JobResourceAccess TopologyWrite(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return WorldStorageJobResources.Write(world, s_topology);
    }

    internal static void RequireTopologyWrite(World world, bool requireSingleWorkItem)
    {
        JobSystem.RequireCurrentAccess(TopologyWrite(world), requireSingleWorkItem);
    }
}
