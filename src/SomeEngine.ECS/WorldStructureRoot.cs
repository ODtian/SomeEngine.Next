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
/// The complete ECS-owned mutable structure published by one <see cref="World"/>. Owners inside
/// a root are bound only to other owners from the same root. Stable controls such as hook
/// registrations, command recording, and job admission remain on World.
/// </summary>
internal sealed class WorldStructureRoot
{
    private WorldStructureRoot(
        Owners.Entities entities,
        Owners.Tables tables,
        Owners.Sparse sparse,
        Owners.Indices indices,
        Owners.RelationGraph relationGraph,
        Owners.Components components,
        global::SomeEngine.ECS.Queries.QueryRegistry queries,
        Owners.Buffers buffers,
        Owners.Bundles bundles,
        Owners.Copy copy,
        Owners.Shared shared,
        Owners.Clock clock,
        Owners.Iteration iteration,
        Owners.Hierarchy hierarchy)
    {
        Entities = entities;
        Tables = tables;
        Sparse = sparse;
        Indices = indices;
        RelationGraph = relationGraph;
        Components = components;
        Queries = queries;
        Buffers = buffers;
        Bundles = bundles;
        Copy = copy;
        Shared = shared;
        Clock = clock;
        Iteration = iteration;
        Hierarchy = hierarchy;
    }

    internal Owners.Entities Entities { get; }
    internal Owners.Tables Tables { get; }
    internal Owners.Sparse Sparse { get; }
    internal Owners.Indices Indices { get; }
    internal Owners.RelationGraph RelationGraph { get; }
    internal Owners.Components Components { get; }
    internal global::SomeEngine.ECS.Queries.QueryRegistry Queries { get; }
    internal Owners.Buffers Buffers { get; }
    internal Owners.Bundles Bundles { get; }
    internal Owners.Copy Copy { get; }
    internal Owners.Shared Shared { get; }
    internal Owners.Clock Clock { get; }
    internal Owners.Iteration Iteration { get; }
    internal Owners.Hierarchy Hierarchy { get; }

    internal static WorldStructureRoot Create(
        World world,
        int initialEntityCapacity,
        Owners.Hooks hooks)
    {
        var entities = new Owners.Entities(initialEntityCapacity);
        var queries = new global::SomeEngine.ECS.Queries.QueryRegistry();
        var tables = new Owners.Tables(entities, queries.OnNewArchetype);
        var root = new WorldStructureRoot(
            entities,
            tables,
            new Owners.Sparse(),
            new Owners.Indices(),
            new Owners.RelationGraph(),
            new Owners.Components(),
            queries,
            new Owners.Buffers(),
            new Owners.Bundles(),
            new Owners.Copy(),
            new Owners.Shared(),
            new Owners.Clock(),
            new Owners.Iteration(),
            new Owners.Hierarchy());
        root.Bind(world, hooks);
        return root;
    }

    /// <summary>
    /// Builds a semantically exact candidate from this published root. Mutable owner shells and
    /// bounded page tables are candidate-private; entity-record pages and chunk backing remain
    /// shared read-only until the candidate's first write detaches the touched page/chunk. Immutable
    /// topology snapshot generations may also remain shared. Derivable runtime caches start cold.
    /// </summary>
    internal WorldStructureRoot CloneDetached(World world, Owners.Hooks hooks)
    {
        return CloneDetached(world, hooks, out _);
    }

    internal WorldStructureRoot CloneDetached(
        World world,
        Owners.Hooks hooks,
        out WorldStructureCloneMetrics cloneMetrics)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(hooks);
        return CloneDetached(
            world,
            hooks,
            out cloneMetrics,
            cloneDerivedTableCaches: true);
    }

    /// <summary>
    /// Forks the same semantic image for a serialization handoff. Structural and shared-chunk
    /// state remains exact, but add/remove/include/cleanup transition caches start cold because
    /// they are derivable and the encoder never consults them. The table clone also transfers its
    /// already-built identity resolvers to EntityStore instead of scanning every shell again.
    /// </summary>
    internal WorldStructureRoot CloneSerializationSuccessor(
        World world,
        Owners.Hooks hooks)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(hooks);
        return CloneDetached(
            world,
            hooks,
            out _,
            cloneDerivedTableCaches: false);
    }

    private WorldStructureRoot CloneDetached(
        World world,
        Owners.Hooks hooks,
        out WorldStructureCloneMetrics cloneMetrics,
        bool cloneDerivedTableCaches)
    {
        if (Iteration.HasOwner)
        {
            throw new InvalidOperationException(
                "Cannot clone a World structure while query or storage ownership is active.");
        }

        var registry = Tables.Registry.CloneExact(
            out var tableMap,
            cloneDerivedTableCaches);
        var entityStore = Entities.Store.CloneExact(tableMap, registry);
        var entities = new Owners.Entities(entityStore);
        global::SomeEngine.ECS.Queries.QueryRegistry queries = Queries.CloneExact(
            tableMap,
            out int queryMatchCount);
        var tables = new Owners.Tables(entities, registry, queries.OnNewArchetype);
        var clock = new Owners.Clock();
        clock.Write(Clock.Tick);

        cloneMetrics = new WorldStructureCloneMetrics(
            tableMap.ArchetypeCount,
            tableMap.ChunkCount,
            queryMatchCount);

        var root = new WorldStructureRoot(
            entities,
            tables,
            Sparse.CloneDetached(),
            Indices.CloneDetached(),
            RelationGraph.CloneDetached(),
            new Owners.Components(),
            queries,
            new Owners.Buffers(),
            new Owners.Bundles(),
            new Owners.Copy(),
            Shared.CloneDetached(),
            clock,
            new Owners.Iteration(),
            Hierarchy.CloneDetached());
        root.Bind(world, hooks);
        return root;
    }

    private void Bind(World world, Owners.Hooks hooks)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(hooks);

        Hierarchy.Bind(Entities, Tables, Components, Clock);
        Entities.Bind(
            world,
            Tables,
            RelationGraph,
            Components,
            Sparse,
            Iteration,
            Hierarchy);
        Sparse.Bind(Entities, Iteration);
        Shared.Bind(Entities, Tables, Iteration);
        Components.Bind(
            world,
            Entities,
            Tables,
            Indices,
            RelationGraph,
            hooks,
            Clock,
            Iteration,
            Hierarchy);
        Buffers.Bind(Entities, Components, Bundles, Clock, Iteration);
        Bundles.Bind(
            Entities,
            Tables,
            Components,
            Buffers,
            Shared,
            Sparse,
            Indices,
            hooks,
            Clock,
            Iteration,
            Hierarchy);
        Copy.Bind(
            Entities,
            Tables,
            Components,
            Buffers,
            Sparse,
            Indices,
            Clock,
            Iteration,
            Hierarchy);
    }
}
