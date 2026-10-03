namespace SomeEngine.ECS;

internal readonly record struct WorldStructureCloneMetrics(
    int ArchetypeShells,
    int ChunkShells,
    int QueryMatches);

internal sealed class WorldStructurePublication
{
    internal WorldStructurePublication(WorldStructureRoot root, long epoch)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        if (epoch < 0)
            throw new ArgumentOutOfRangeException(nameof(epoch));
        Epoch = epoch;
    }

    internal WorldStructureRoot Root { get; }
    internal long Epoch { get; }
}

/// <summary>
/// One coherent set of ECS data. Component, buffer, bundle and copy operations are small value
/// views of these data, not another graph of services to allocate and reconnect on every commit.
/// Query execution and hook registration belong to World and are not versioned with storage.
/// </summary>
internal sealed class WorldStructureRoot
{
    private WorldStructureRoot(World world, int initialEntityCapacity, Owners.Hooks hooks)
    {
        World = world;
        Hooks = hooks;
        Clock = new Owners.Clock();
        ComponentWriteVersions = new ComponentWriteVersions();
        Queries = new Queries.QueryRegistry();
        Entities = new Owners.Entities(this, initialEntityCapacity);
        Tables = new Owners.Tables(Entities, Queries.OnNewArchetype);
        Sparse = new Owners.Sparse(this);
        Indices = new Owners.Indices();
        RelationGraph = new Owners.RelationGraph();
        Shared = new Owners.Shared(this);
        Hierarchy = new Owners.Hierarchy(this);
    }

    private WorldStructureRoot(
        World world,
        Owners.Hooks hooks,
        WorldStructureRoot source,
        bool cloneDerivedTableCaches,
        out WorldStructureCloneMetrics cloneMetrics)
    {
        if (source.Iteration.HasOwner)
            throw new InvalidOperationException("Cannot copy ECS data while query or storage access is active.");

        World = world;
        Hooks = hooks;
        Clock = new Owners.Clock();
        Clock.Write(source.Clock.Tick);
        ComponentWriteVersions = source.ComponentWriteVersions.CloneDetached();

        Archetypes.ArchetypeRegistry registry = source.Tables.Registry.CloneExact(
            out Archetypes.DetachedTableMap tableMap, cloneDerivedTableCaches);
        Entities = new Owners.Entities(this, source.Entities.Store.CloneExact(tableMap, registry));
        Queries = source.Queries.CloneExact(tableMap, out int queryMatchCount);
        Tables = new Owners.Tables(Entities, registry, Queries.OnNewArchetype);
        Sparse = source.Sparse.CloneDetached(this);
        Indices = source.Indices.CloneDetached();
        RelationGraph = source.RelationGraph.CloneDetached();
        Shared = source.Shared.CloneDetached(this);
        Hierarchy = source.Hierarchy.CloneDetached(this);
        cloneMetrics = new WorldStructureCloneMetrics(
            tableMap.ArchetypeCount, tableMap.ChunkCount, queryMatchCount);
    }

    internal World World { get; }
    internal Owners.Hooks Hooks { get; }
    internal Owners.Entities Entities { get; }
    internal Owners.Tables Tables { get; }
    internal Owners.Sparse Sparse { get; }
    internal Owners.Indices Indices { get; }
    internal Owners.RelationGraph RelationGraph { get; }
    internal Queries.QueryRegistry Queries { get; }
    internal Owners.Shared Shared { get; }
    internal Owners.Clock Clock { get; }
    internal ComponentWriteVersions ComponentWriteVersions { get; }
    internal Owners.Hierarchy Hierarchy { get; }
    internal BundleData BundleData { get; } = new();

    internal Owners.Iteration Iteration => World.QueryExecutions;
    internal Owners.Components Components => new(this);
    internal Owners.Buffers Buffers => new(this);
    internal Owners.Bundles Bundles => new(this);
    internal Owners.Copy Copy => new(this);

    internal static WorldStructureRoot Create(
        World world, int initialEntityCapacity, Owners.Hooks hooks) =>
        new(world, initialEntityCapacity, hooks);

    internal WorldStructureRoot CloneDetached(World world, Owners.Hooks hooks) =>
        CloneDetached(world, hooks, out _);

    /// <summary>
    /// Data pages and immutable relation generations remain shared until their first write.
    /// Each mutable index and bundle cache belongs to this copy; operation views do not allocate.
    /// </summary>
    internal WorldStructureRoot CloneDetached(
        World world, Owners.Hooks hooks, out WorldStructureCloneMetrics cloneMetrics) =>
        new(world, hooks, this, cloneDerivedTableCaches: true, out cloneMetrics);

    /// <summary>
    /// Serialization keeps the prior data and hands a COW successor to World. Transition caches
    /// are derivable and start empty; no component payload or second object graph is staged.
    /// </summary>
    internal WorldStructureRoot CloneSerializationSuccessor(World world, Owners.Hooks hooks) =>
        new(world, hooks, this, cloneDerivedTableCaches: false, out _);
}
