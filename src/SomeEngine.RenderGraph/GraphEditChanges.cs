namespace SomeEngine.RenderGraph;

[Flags]
internal enum GraphStoreKind : ushort
{
    None = 0,
    Buffers = 1 << 0,
    Textures = 1 << 1,
    PersistentBindings = 1 << 2,
    QueryPools = 1 << 3,
    ShaderTables = 1 << 4,
    Views = 1 << 5,
    Passes = 1 << 6,
    Accesses = 1 << 7,
    Orders = 1 << 9,
    Regions = 1 << 10,
}

internal sealed class GraphEditChanges
{
    internal readonly HashSet<GraphIdentity> Buffers = [];
    internal readonly HashSet<GraphIdentity> Textures = [];
    internal readonly HashSet<GraphIdentity> PersistentBindings = [];
    internal readonly HashSet<GraphIdentity> QueryPools = [];
    internal readonly HashSet<GraphIdentity> ShaderTables = [];
    internal readonly HashSet<GraphIdentity> Views = [];
    internal readonly HashSet<GraphIdentity> ReleasedViews = [];
    internal readonly HashSet<GraphIdentity> Passes = [];
    internal readonly HashSet<GraphIdentity> AffectedPasses = [];
    internal readonly HashSet<GraphIdentity> Accesses = [];
    internal readonly HashSet<GraphIdentity> Regions = [];
    internal readonly HashSet<GraphIdentity> AffectedResources = [];
    internal readonly HashSet<GraphIdentity> OrderPasses = [];
    internal readonly HashSet<ExplicitPassOrder> Orders = [];
    internal readonly HashSet<GraphIdentity> Labels = [];
    internal GraphStoreKind Stores;
    internal bool PassMembershipChanged;
    internal bool AccessMembershipChanged;
    internal bool ResourceMembershipChanged;
    internal bool ViewMembershipChanged;
    internal bool OrdersChanged;
    internal bool CompactTransientPlacements;

    internal bool IsEmpty => Stores == GraphStoreKind.None && Labels.Count == 0 && !CompactTransientPlacements;
    internal bool LabelsOnly => Stores == GraphStoreKind.None && Labels.Count != 0 && !CompactTransientPlacements;

    internal int EditedResourceCount =>
        Buffers.Count + Textures.Count + QueryPools.Count + ShaderTables.Count;

    internal int CopiedBlockCount => BitOperations.PopCount((uint)Stores);

    internal void TouchBuffer(in GraphIdentity identity, bool membership = false)
    {
        Stores |= GraphStoreKind.Buffers;
        Buffers.Add(identity);
        AffectedResources.Add(identity);
        ResourceMembershipChanged |= membership;
    }

    internal void TouchTexture(in GraphIdentity identity, bool membership = false)
    {
        Stores |= GraphStoreKind.Textures;
        Textures.Add(identity);
        AffectedResources.Add(identity);
        ResourceMembershipChanged |= membership;
    }

    internal void TouchPersistentBindings(in GraphIdentity identity, bool membership = false)
    {
        Stores |= GraphStoreKind.PersistentBindings;
        PersistentBindings.Add(identity);
        ResourceMembershipChanged |= membership;
    }

    internal void TouchQueryPool(in GraphIdentity identity, bool membership = false)
    {
        Stores |= GraphStoreKind.QueryPools;
        QueryPools.Add(identity);
        AffectedResources.Add(identity);
        ResourceMembershipChanged |= membership;
    }

    internal void TouchShaderTable(in GraphIdentity identity, bool membership = false)
    {
        Stores |= GraphStoreKind.ShaderTables;
        ShaderTables.Add(identity);
        AffectedResources.Add(identity);
        ResourceMembershipChanged |= membership;
    }

    internal void TouchView(in GraphIdentity identity, bool membership = false)
    {
        Stores |= GraphStoreKind.Views;
        Views.Add(identity);
        ViewMembershipChanged |= membership;
    }

    internal void TouchPass(in GraphIdentity identity, bool membership = false)
    {
        Stores |= GraphStoreKind.Passes;
        Passes.Add(identity);
        AffectedPasses.Add(identity);
        PassMembershipChanged |= membership;
    }

    internal void TouchAccess(
        in GraphIdentity identity,
        in GraphIdentity pass,
        in GraphIdentity resource,
        bool membership = false)
    {
        Stores |= GraphStoreKind.Accesses;
        if (membership)
        {
            Stores |= GraphStoreKind.Passes;
            Passes.Add(pass);
        }
        Accesses.Add(identity);
        AffectedPasses.Add(pass);
        if (resource.IsValid) AffectedResources.Add(resource);
        AccessMembershipChanged |= membership;
    }

    internal void TouchRegion(in GraphIdentity identity)
    {
        Stores |= GraphStoreKind.Regions;
        Regions.Add(identity);
    }

    internal void TouchOrder(in GraphIdentity predecessor, in GraphIdentity consumer)
    {
        Stores |= GraphStoreKind.Orders;
        OrdersChanged = true;
        OrderPasses.Add(predecessor);
        OrderPasses.Add(consumer);
        Orders.Add(new ExplicitPassOrder(predecessor, consumer));
    }
}

internal readonly record struct GraphIndexUpdateStatistics(
    int ResourcesReanalyzed,
    int HazardEdgesRebuilt,
    int ScheduleNodesRevisited,
    int TransientAllocationsMoved);

#if DEBUG
public readonly record struct RenderGraphEditStatistics(
    int EditedPassCount,
    int EditedResourceCount,
    int CopiedStructureBlockCount,
    int SharedStructureBlockCount,
    int ResourcesReanalyzed,
    int HazardEdgesRebuilt,
    int ScheduleNodesRevisited,
    int TransientAllocationsMoved);
#endif
