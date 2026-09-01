namespace SomeEngine.RenderGraph;

internal readonly record struct GraphDependencyEdge(
    GraphIdentity Predecessor,
    GraphIdentity Consumer);

internal readonly record struct GraphResourceScheduleLifetime(int FirstUse, int LastUse);

internal readonly record struct GraphTransientPlacementState(
    ulong Token,
    GraphResourceScheduleLifetime Lifetime);

internal sealed class GraphHazardTimeline
{
    internal GraphHazardTimeline(GraphDependencyEdge[] edges) => Edges = edges;

    internal GraphDependencyEdge[] Edges { get; }
}

internal sealed class GraphStructureIndex
{
    private static readonly Dictionary<GraphIdentity, GraphIdentity[]> s_emptyAdjacency = [];

    internal static readonly GraphStructureIndex Empty = new(
        new GraphStructure(),
        [], [], [], [], [], [], [], [], [],
        s_emptyAdjacency, s_emptyAdjacency, s_emptyAdjacency, s_emptyAdjacency,
        s_emptyAdjacency, s_emptyAdjacency, s_emptyAdjacency, s_emptyAdjacency,
        [], [], [], [], [], [], 1);

    private GraphStructureIndex(
        GraphStructure structure,
        GraphIdentity[] bufferIds,
        GraphIdentity[] textureIds,
        GraphIdentity[] queryPoolIds,
        GraphIdentity[] shaderTableIds,
        GraphIdentity[] viewIds,
        GraphIdentity[] passIds,
        GraphIdentity[] accessIds,
        int[][] passAccesses,
        int[][] explicitPredecessors,
        Dictionary<GraphIdentity, GraphIdentity[]> resourceAccesses,
        Dictionary<GraphIdentity, GraphIdentity[]> passAccessIdentities,
        Dictionary<GraphIdentity, GraphIdentity[]> explicitPredecessorIdentities,
        Dictionary<GraphIdentity, GraphIdentity[]> explicitSuccessorIdentities,
        Dictionary<GraphIdentity, GraphIdentity[]> dependencyPredecessors,
        Dictionary<GraphIdentity, GraphIdentity[]> dependencySuccessors,
        Dictionary<GraphIdentity, GraphIdentity[]> viewResources,
        Dictionary<GraphIdentity, GraphIdentity[]> resourceViews,
        Dictionary<GraphIdentity, GraphHazardTimeline> hazardTimelines,
        Dictionary<GraphDependencyEdge, int> hazardEdgeCounts,
        HashSet<GraphDependencyEdge> explicitEdges,
        GraphIdentity[] topologicalSchedule,
        Dictionary<GraphIdentity, GraphResourceScheduleLifetime> resourceLifetimes,
        Dictionary<GraphIdentity, GraphTransientPlacementState> transientPlacements,
        ulong nextPlacementToken)
    {
        Structure = structure;
        BufferIds = bufferIds;
        TextureIds = textureIds;
        QueryPoolIds = queryPoolIds;
        ShaderTableIds = shaderTableIds;
        ViewIds = viewIds;
        PassIds = passIds;
        AccessIds = accessIds;
        PassAccessIndices = passAccesses;
        ExplicitPredecessors = explicitPredecessors;
        ResourceAccesses = resourceAccesses;
        PassAccessIdentities = passAccessIdentities;
        ExplicitPredecessorIdentities = explicitPredecessorIdentities;
        ExplicitSuccessorIdentities = explicitSuccessorIdentities;
        DependencyPredecessors = dependencyPredecessors;
        DependencySuccessors = dependencySuccessors;
        ViewResources = viewResources;
        ResourceViews = resourceViews;
        HazardTimelines = hazardTimelines;
        HazardEdgeCounts = hazardEdgeCounts;
        ExplicitEdges = explicitEdges;
        TopologicalSchedule = topologicalSchedule;
        ResourceLifetimes = resourceLifetimes;
        TransientPlacements = transientPlacements;
        NextPlacementToken = nextPlacementToken;
    }

    internal GraphStructure Structure { get; }
    internal GraphIdentity[] BufferIds { get; }
    internal GraphIdentity[] TextureIds { get; }
    internal GraphIdentity[] QueryPoolIds { get; }
    internal GraphIdentity[] ShaderTableIds { get; }
    internal GraphIdentity[] ViewIds { get; }
    internal GraphIdentity[] PassIds { get; }
    internal GraphIdentity[] AccessIds { get; }
    internal int[][] PassAccessIndices { get; }
    internal int[][] ExplicitPredecessors { get; }
    internal Dictionary<GraphIdentity, GraphIdentity[]> ResourceAccesses { get; }
    internal Dictionary<GraphIdentity, GraphIdentity[]> PassAccessIdentities { get; }
    internal Dictionary<GraphIdentity, GraphIdentity[]> ExplicitPredecessorIdentities { get; }
    internal Dictionary<GraphIdentity, GraphIdentity[]> ExplicitSuccessorIdentities { get; }
    internal Dictionary<GraphIdentity, GraphIdentity[]> DependencyPredecessors { get; }
    internal Dictionary<GraphIdentity, GraphIdentity[]> DependencySuccessors { get; }
    internal Dictionary<GraphIdentity, GraphIdentity[]> ViewResources { get; }
    internal Dictionary<GraphIdentity, GraphIdentity[]> ResourceViews { get; }
    internal Dictionary<GraphIdentity, GraphHazardTimeline> HazardTimelines { get; }
    internal Dictionary<GraphDependencyEdge, int> HazardEdgeCounts { get; }
    internal HashSet<GraphDependencyEdge> ExplicitEdges { get; }
    internal GraphIdentity[] TopologicalSchedule { get; }
    internal Dictionary<GraphIdentity, GraphResourceScheduleLifetime> ResourceLifetimes { get; }
    internal Dictionary<GraphIdentity, GraphTransientPlacementState> TransientPlacements { get; }
    internal ulong NextPlacementToken { get; }

    internal static GraphStructureIndex Build(RenderGraph graph, GraphStructure structure)
    {
        var changes = new GraphEditChanges();
        TouchEverything(graph.Identity, structure, changes);
        return Update(graph, Empty, structure, changes, out _);
    }

    internal static GraphStructureIndex Update(
        RenderGraph graph,
        GraphStructureIndex previous,
        GraphStructure structure,
        GraphEditChanges changes,
        out GraphIndexUpdateStatistics statistics)
    {
        Dictionary<GraphIdentity, GraphIdentity[]> resourceAccesses = Clone(previous.ResourceAccesses);
        Dictionary<GraphIdentity, GraphIdentity[]> passAccessIdentities = Clone(previous.PassAccessIdentities);
        UpdateAccessAdjacency(previous.Structure, structure, changes, resourceAccesses, passAccessIdentities);

        Dictionary<GraphIdentity, GraphIdentity[]> viewResources = Clone(previous.ViewResources);
        Dictionary<GraphIdentity, GraphIdentity[]> resourceViews = Clone(previous.ResourceViews);
        UpdateViewAdjacency(previous.Structure, structure, changes, viewResources, resourceViews);

        Dictionary<GraphIdentity, GraphIdentity[]> explicitPredecessorIdentities =
            Clone(previous.ExplicitPredecessorIdentities);
        Dictionary<GraphIdentity, GraphIdentity[]> explicitSuccessorIdentities =
            Clone(previous.ExplicitSuccessorIdentities);
        UpdateExplicitAdjacency(
            structure,
            changes,
            explicitPredecessorIdentities,
            explicitSuccessorIdentities);

        ValidateTouched(graph, structure, changes, resourceAccesses, resourceViews);

        GraphIdentity[] passIds = changes.Passes.Count == 0 && !ReferenceEquals(previous, Empty)
            ? previous.PassIds
            : BuildIds(graph.Identity, structure.Passes);
        var hazardOrderDirty = new HashSet<GraphIdentity>(changes.AffectedPasses);
        hazardOrderDirty.UnionWith(changes.OrderPasses);
        GraphIdentity[] hazardOrder = RepairTopologicalSchedule(
            structure,
            passIds,
            previous.TopologicalSchedule,
            explicitPredecessorIdentities,
            explicitSuccessorIdentities,
            hazardOrderDirty,
            out _);
        Dictionary<GraphIdentity, int> hazardOrderPositions = BuildPositions(hazardOrder);

        var hazardTimelines = new Dictionary<GraphIdentity, GraphHazardTimeline>(previous.HazardTimelines);
        var hazardEdgeCounts = new Dictionary<GraphDependencyEdge, int>(previous.HazardEdgeCounts);
        var changedEdges = new HashSet<GraphDependencyEdge>();
        int resourcesReanalyzed = 0;
        int hazardEdgesRebuilt = 0;
        foreach (GraphIdentity resource in changes.AffectedResources)
        {
            resourcesReanalyzed++;
            if (hazardTimelines.Remove(resource, out GraphHazardTimeline? oldTimeline))
            {
                foreach (GraphDependencyEdge edge in oldTimeline.Edges)
                {
                    changedEdges.Add(edge);
                    Decrement(hazardEdgeCounts, edge);
                }
            }

            if (!ContainsResource(structure, resource)) continue;
            GraphHazardTimeline timeline = BuildHazardTimeline(
                structure,
                resourceAccesses.GetValueOrDefault(resource, []),
                hazardOrderPositions);
            hazardTimelines.Add(resource, timeline);
            hazardEdgesRebuilt += timeline.Edges.Length;
            foreach (GraphDependencyEdge edge in timeline.Edges)
            {
                changedEdges.Add(edge);
                Increment(hazardEdgeCounts, edge);
            }
        }

        HashSet<GraphDependencyEdge> explicitEdges = changes.OrdersChanged ||
            changes.PassMembershipChanged || ReferenceEquals(previous, Empty)
                ? BuildExplicitEdges(structure)
                : new HashSet<GraphDependencyEdge>(previous.ExplicitEdges);
        if (!SetEquals(previous.ExplicitEdges, explicitEdges))
        {
            foreach (GraphDependencyEdge edge in previous.ExplicitEdges) changedEdges.Add(edge);
            foreach (GraphDependencyEdge edge in explicitEdges) changedEdges.Add(edge);
        }

        Dictionary<GraphIdentity, GraphIdentity[]> dependencyPredecessors =
            Clone(previous.DependencyPredecessors);
        Dictionary<GraphIdentity, GraphIdentity[]> dependencySuccessors =
            Clone(previous.DependencySuccessors);
        var scheduleDirty = new HashSet<GraphIdentity>(changes.AffectedPasses);
        scheduleDirty.UnionWith(changes.OrderPasses);
        foreach (GraphDependencyEdge edge in changedEdges)
        {
            bool oldPresent = previous.ExplicitEdges.Contains(edge) || previous.HazardEdgeCounts.ContainsKey(edge);
            bool newPresent = explicitEdges.Contains(edge) || hazardEdgeCounts.ContainsKey(edge);
            if (oldPresent == newPresent) continue;
            scheduleDirty.Add(edge.Predecessor);
            scheduleDirty.Add(edge.Consumer);
            if (newPresent)
            {
                AddAdjacency(dependencySuccessors, edge.Predecessor, edge.Consumer);
                AddAdjacency(dependencyPredecessors, edge.Consumer, edge.Predecessor);
            }
            else
            {
                RemoveAdjacency(dependencySuccessors, edge.Predecessor, edge.Consumer);
                RemoveAdjacency(dependencyPredecessors, edge.Consumer, edge.Predecessor);
            }
        }
        foreach (GraphIdentity removed in previous.PassIds)
        {
            if (structure.Passes.Contains(removed)) continue;
            dependencyPredecessors.Remove(removed);
            dependencySuccessors.Remove(removed);
        }

        GraphIdentity[] schedule = RepairTopologicalSchedule(
            structure,
            passIds,
            previous.TopologicalSchedule,
            dependencyPredecessors,
            dependencySuccessors,
            scheduleDirty,
            out int scheduleNodesRevisited);

        var lifetimeDirty = new HashSet<GraphIdentity>(changes.AffectedResources);
        foreach (GraphIdentity pass in scheduleDirty)
        {
            if (!passAccessIdentities.TryGetValue(pass, out GraphIdentity[]? accesses)) continue;
            foreach (GraphIdentity accessId in accesses)
                if (structure.Accesses.Contains(accessId))
                    lifetimeDirty.Add(structure.Accesses.Get(accessId).Target);
        }
        var resourceLifetimes = new Dictionary<GraphIdentity, GraphResourceScheduleLifetime>(
            previous.ResourceLifetimes);
        Dictionary<GraphIdentity, int> positions = BuildPositions(schedule);
        foreach (GraphIdentity resource in lifetimeDirty)
        {
            if (!ContainsResource(structure, resource))
            {
                resourceLifetimes.Remove(resource);
                continue;
            }
            resourceLifetimes[resource] = ResolveLifetime(
                structure,
                resourceAccesses.GetValueOrDefault(resource, []),
                positions);
        }

        var transientPlacements = new Dictionary<GraphIdentity, GraphTransientPlacementState>(
            previous.TransientPlacements);
        ulong nextPlacementToken = previous.NextPlacementToken;
        int transientAllocationsMoved = 0;
        foreach (GraphIdentity resource in previous.TransientPlacements.Keys)
            if (!IsTransient(structure, resource)) transientPlacements.Remove(resource);
        foreach (GraphIdentity resource in EnumerateTransientResources(graph.Identity, structure))
        {
            GraphResourceScheduleLifetime lifetime = resourceLifetimes.GetValueOrDefault(
                resource,
                new GraphResourceScheduleLifetime(int.MaxValue, -1));
            if (!transientPlacements.TryGetValue(resource, out GraphTransientPlacementState retained))
            {
                transientPlacements.Add(resource, new GraphTransientPlacementState(
                    checked(nextPlacementToken++),
                    lifetime));
                continue;
            }

            bool replace = changes.CompactTransientPlacements ||
                changes.Buffers.Contains(resource) || changes.Textures.Contains(resource);
            if (replace)
            {
                retained = new GraphTransientPlacementState(checked(nextPlacementToken++), lifetime);
                transientAllocationsMoved++;
            }
            else if (lifetime != retained.Lifetime)
            {
                retained = retained with { Lifetime = lifetime };
            }
            transientPlacements[resource] = retained;
        }

        GraphIdentity[] bufferIds = changes.Buffers.Count == 0 && !ReferenceEquals(previous, Empty)
            ? previous.BufferIds
            : BuildIds(graph.Identity, structure.Buffers);
        GraphIdentity[] textureIds = changes.Textures.Count == 0 && !ReferenceEquals(previous, Empty)
            ? previous.TextureIds
            : BuildIds(graph.Identity, structure.Textures);
        GraphIdentity[] queryPoolIds = changes.QueryPools.Count == 0 && !ReferenceEquals(previous, Empty)
            ? previous.QueryPoolIds
            : BuildIds(graph.Identity, structure.QueryPools);
        GraphIdentity[] shaderTableIds = changes.ShaderTables.Count == 0 && !ReferenceEquals(previous, Empty)
            ? previous.ShaderTableIds
            : BuildIds(graph.Identity, structure.ShaderTables);
        GraphIdentity[] viewIds = changes.Views.Count == 0 && !ReferenceEquals(previous, Empty)
            ? previous.ViewIds
            : BuildIds(graph.Identity, structure.Views);
        GraphIdentity[] accessIds = changes.Accesses.Count == 0 && !ReferenceEquals(previous, Empty)
            ? previous.AccessIds
            : BuildIds(graph.Identity, structure.Accesses);
        int[][] passAccesses = changes.PassMembershipChanged || changes.AccessMembershipChanged ||
            ReferenceEquals(previous, Empty)
                ? BuildPassAccessIndices(structure)
                : previous.PassAccessIndices;
        int[][] explicitPredecessors = changes.OrdersChanged || changes.PassMembershipChanged ||
            ReferenceEquals(previous, Empty)
                ? BuildExplicitPredecessorIndices(structure)
                : previous.ExplicitPredecessors;

        statistics = new GraphIndexUpdateStatistics(
            resourcesReanalyzed,
            hazardEdgesRebuilt,
            scheduleNodesRevisited,
            transientAllocationsMoved);
        return new GraphStructureIndex(
            structure,
            bufferIds,
            textureIds,
            queryPoolIds,
            shaderTableIds,
            viewIds,
            passIds,
            accessIds,
            passAccesses,
            explicitPredecessors,
            resourceAccesses,
            passAccessIdentities,
            explicitPredecessorIdentities,
            explicitSuccessorIdentities,
            dependencyPredecessors,
            dependencySuccessors,
            viewResources,
            resourceViews,
            hazardTimelines,
            hazardEdgeCounts,
            explicitEdges,
            schedule,
            resourceLifetimes,
            transientPlacements,
            nextPlacementToken);
    }

    private static void TouchEverything(ulong owner, GraphStructure structure, GraphEditChanges changes)
    {
        for (int i = 0; i < structure.Buffers.Count; i++)
            changes.TouchBuffer(structure.Buffers.IdentityAt(owner, i), membership: true);
        for (int i = 0; i < structure.Textures.Count; i++)
            changes.TouchTexture(structure.Textures.IdentityAt(owner, i), membership: true);
        for (int i = 0; i < structure.PersistentBindings.Count; i++)
            changes.TouchPersistentBindings(structure.PersistentBindings.IdentityAt(owner, i), membership: true);
        for (int i = 0; i < structure.QueryPools.Count; i++)
            changes.TouchQueryPool(structure.QueryPools.IdentityAt(owner, i), membership: true);
        for (int i = 0; i < structure.ShaderTables.Count; i++)
            changes.TouchShaderTable(structure.ShaderTables.IdentityAt(owner, i), membership: true);
        for (int i = 0; i < structure.Views.Count; i++)
            changes.TouchView(structure.Views.IdentityAt(owner, i), membership: true);
        for (int i = 0; i < structure.Passes.Count; i++)
            changes.TouchPass(structure.Passes.IdentityAt(owner, i), membership: true);
        for (int i = 0; i < structure.Accesses.Count; i++)
        {
            GraphIdentity id = structure.Accesses.IdentityAt(owner, i);
            PassResourceAccess row = structure.Accesses.Rows[i];
            changes.TouchAccess(id, row.Pass, row.Target, membership: true);
        }
        for (int i = 0; i < structure.ExtensionPoints.Count; i++)
            changes.TouchExtensionPoint(structure.ExtensionPoints.IdentityAt(owner, i), membership: true);
        foreach (ExplicitPassOrder order in structure.Orders)
            changes.TouchOrder(order.Predecessor, order.Consumer);
    }

    private static void UpdateAccessAdjacency(
        GraphStructure previous,
        GraphStructure next,
        GraphEditChanges changes,
        Dictionary<GraphIdentity, GraphIdentity[]> resourceAccesses,
        Dictionary<GraphIdentity, GraphIdentity[]> passAccesses)
    {
        foreach (GraphIdentity accessId in changes.Accesses)
        {
            if (previous.Accesses.Contains(accessId))
            {
                PassResourceAccess old = previous.Accesses.Get(accessId);
                RemoveAdjacency(resourceAccesses, old.Target, accessId);
                RemoveAdjacency(passAccesses, old.Pass, accessId);
            }
            if (!next.Accesses.Contains(accessId)) continue;
            PassResourceAccess current = next.Accesses.Get(accessId);
            AddAdjacency(resourceAccesses, current.Target, accessId);
            AddAdjacency(passAccesses, current.Pass, accessId);
        }
        foreach (GraphIdentity pass in changes.Passes)
            if (!next.Passes.Contains(pass)) passAccesses.Remove(pass);
    }

    private static void UpdateViewAdjacency(
        GraphStructure previous,
        GraphStructure next,
        GraphEditChanges changes,
        Dictionary<GraphIdentity, GraphIdentity[]> viewResources,
        Dictionary<GraphIdentity, GraphIdentity[]> resourceViews)
    {
        foreach (GraphIdentity viewId in changes.Views)
        {
            if (viewResources.Remove(viewId, out GraphIdentity[]? oldResources))
                foreach (GraphIdentity resource in oldResources)
                    RemoveAdjacency(resourceViews, resource, viewId);
            else if (previous.Views.Contains(viewId))
                foreach (GraphIdentity resource in ResolveViewResources(previous.Views.Get(viewId)))
                    RemoveAdjacency(resourceViews, resource, viewId);
            if (!next.Views.Contains(viewId)) continue;
            GraphIdentity[] resources = ResolveViewResources(next.Views.Get(viewId));
            viewResources.Add(viewId, resources);
            foreach (GraphIdentity resource in resources)
                AddAdjacency(resourceViews, resource, viewId);
        }
    }

    private static GraphIdentity[] ResolveViewResources(GraphView view)
    {
        if (view.Kind is GraphViewKind.BufferCbv or GraphViewKind.BufferSrv or GraphViewKind.BufferUav)
            return view.AdditionalBuffer.IsValid ? [view.Buffer, view.AdditionalBuffer] : [view.Buffer];
        return [view.Texture];
    }

    private static void UpdateExplicitAdjacency(
        GraphStructure structure,
        GraphEditChanges changes,
        Dictionary<GraphIdentity, GraphIdentity[]> predecessors,
        Dictionary<GraphIdentity, GraphIdentity[]> successors)
    {
        if (!changes.OrdersChanged && !changes.PassMembershipChanged) return;
        var affected = new HashSet<GraphIdentity>(changes.OrderPasses);
        affected.UnionWith(changes.Passes);
        foreach (GraphIdentity pass in affected)
        {
            predecessors.Remove(pass);
            successors.Remove(pass);
        }
        foreach (ExplicitPassOrder order in structure.Orders)
        {
            if (affected.Contains(order.Consumer))
                AddAdjacency(predecessors, order.Consumer, order.Predecessor);
            if (affected.Contains(order.Predecessor))
                AddAdjacency(successors, order.Predecessor, order.Consumer);
        }
    }

    private static void ValidateTouched(
        RenderGraph graph,
        GraphStructure structure,
        GraphEditChanges changes,
        Dictionary<GraphIdentity, GraphIdentity[]> resourceAccesses,
        Dictionary<GraphIdentity, GraphIdentity[]> resourceViews)
    {
        foreach (GraphIdentity id in changes.Buffers)
        {
            if (!structure.Buffers.Contains(id))
            {
                RejectReferencedRemoval(id, resourceAccesses, resourceViews);
                continue;
            }
            GraphBuffer buffer = structure.Buffers.GetMutable(id);
            ValidateBuffer(buffer);
            buffer.Requirements = graph.Backend.GetBufferMemoryRequirements(
                graph.Device,
                buffer.Description,
                buffer.MemoryType);
            ValidateResourceDependents(structure, id, resourceAccesses, resourceViews, graph);
        }
        foreach (GraphIdentity id in changes.Textures)
        {
            if (!structure.Textures.Contains(id))
            {
                RejectReferencedRemoval(id, resourceAccesses, resourceViews);
                continue;
            }
            GraphTexture texture = structure.Textures.GetMutable(id);
            ValidateTexture(texture);
            texture.Requirements = graph.Backend.GetTextureMemoryRequirements(
                graph.Device,
                texture.BorrowDescription());
            ValidateResourceDependents(structure, id, resourceAccesses, resourceViews, graph);
        }
        foreach (GraphIdentity id in changes.QueryPools)
        {
            if (!structure.QueryPools.Contains(id))
            {
                RejectReferencedRemoval(id, resourceAccesses, resourceViews);
                continue;
            }
            GraphQueryPool pool = structure.QueryPools.Get(id);
            if (!ReferenceEquals(pool.Resource.Device, graph.Device))
                throw new ArgumentException("QueryPool belongs to another Device.");
            if (pool.Resource.Description.Count == 0)
                throw new ArgumentOutOfRangeException(nameof(pool.Resource.Description.Count));
            ValidateResourceDependents(structure, id, resourceAccesses, resourceViews, graph);
        }
        foreach (GraphIdentity id in changes.ShaderTables)
        {
            if (!structure.ShaderTables.Contains(id))
            {
                RejectReferencedRemoval(id, resourceAccesses, resourceViews);
                continue;
            }
            GraphRayTracingShaderTable table = structure.ShaderTables.Get(id);
            if (!ReferenceEquals(table.Resource.Device, graph.Device))
                throw new ArgumentException("RayTracingShaderTable belongs to another Device.");
            foreach (ref readonly GraphParameterResourceBinding binding in table.Inventory.AsSpan())
                binding.ValidateStatic(structure, graph.Device);
            ValidateResourceDependents(structure, id, resourceAccesses, resourceViews, graph);
        }
        foreach (GraphIdentity id in changes.PersistentBindings)
        {
            if (!structure.PersistentBindings.Contains(id)) continue;
            GraphPersistentParameterBindings bindings = structure.PersistentBindings.Get(id);
            if (!ReferenceEquals(bindings.Resource.Device, graph.Device))
                throw new ArgumentException("PersistentParameterBindings belong to another Device.");
            foreach (ref readonly GraphParameterResourceBinding binding in bindings.Inventory.AsSpan())
                binding.ValidateStatic(structure, graph.Device);
        }
        foreach (GraphIdentity id in changes.Views)
            if (structure.Views.Contains(id)) ValidateView(structure, structure.Views.Get(id));
        foreach (GraphIdentity id in changes.Accesses)
            if (structure.Accesses.Contains(id)) ValidateAccess(structure, structure.Accesses.Get(id));
        foreach (GraphIdentity id in changes.Passes)
        {
            if (!structure.Passes.Contains(id)) continue;
            GraphPass pass = structure.Passes.Get(id);
            ValidatePass(graph, pass);
            foreach (GraphIdentity bindings in pass.PersistentBindings)
                if (!structure.PersistentBindings.Contains(bindings))
                    throw new ArgumentException("A Pass references stale PersistentParameterBindings.");
        }

        if (changes.ResourceMembershipChanged)
        {
            foreach (GraphPersistentParameterBindings bindings in structure.PersistentBindings.Rows)
                foreach (ref readonly GraphParameterResourceBinding binding in bindings.Inventory.AsSpan())
                    binding.ValidateStatic(structure, graph.Device);
            foreach (GraphPass pass in structure.Passes.Rows)
                foreach (GraphParameterResourceBinding binding in pass.ParameterBindings)
                    binding.ValidateStatic(structure, graph.Device);
        }
    }

    private static void ValidateResourceDependents(
        GraphStructure structure,
        in GraphIdentity resource,
        Dictionary<GraphIdentity, GraphIdentity[]> resourceAccesses,
        Dictionary<GraphIdentity, GraphIdentity[]> resourceViews,
        RenderGraph graph)
    {
        foreach (GraphIdentity access in resourceAccesses.GetValueOrDefault(resource, []))
            ValidateAccess(structure, structure.Accesses.Get(access));
        foreach (GraphIdentity view in resourceViews.GetValueOrDefault(resource, []))
            ValidateView(structure, structure.Views.Get(view));
    }

    private static void RejectReferencedRemoval(
        in GraphIdentity resource,
        Dictionary<GraphIdentity, GraphIdentity[]> resourceAccesses,
        Dictionary<GraphIdentity, GraphIdentity[]> resourceViews)
    {
        if (resourceAccesses.GetValueOrDefault(resource, []).Length != 0 ||
            resourceViews.GetValueOrDefault(resource, []).Length != 0)
            throw new InvalidOperationException(
                "A graph resource cannot be removed while accesses or views reference it.");
    }

    private static GraphHazardTimeline BuildHazardTimeline(
        GraphStructure structure,
        GraphIdentity[] accessIds,
        IReadOnlyDictionary<GraphIdentity, int> orderPositions)
    {
        var accesses = new List<(GraphIdentity Id, PassResourceAccess Access)>(accessIds.Length);
        foreach (GraphIdentity accessId in accessIds)
            if (structure.Accesses.Contains(accessId))
                accesses.Add((accessId, structure.Accesses.Get(accessId)));
        accesses.Sort((left, right) =>
        {
            int leftPosition = orderPositions.GetValueOrDefault(
                left.Access.Pass,
                structure.Passes.Get(left.Access.Pass).DeclarationOrdinal);
            int rightPosition = orderPositions.GetValueOrDefault(
                right.Access.Pass,
                structure.Passes.Get(right.Access.Pass).DeclarationOrdinal);
            int ordinal = leftPosition.CompareTo(rightPosition);
            if (ordinal != 0) return ordinal;
            int slot = left.Id.Slot.CompareTo(right.Id.Slot);
            return slot != 0 ? slot : left.Id.Generation.CompareTo(right.Id.Generation);
        });

        var edges = new HashSet<GraphDependencyEdge>();
        for (int later = 0; later < accesses.Count; later++)
        {
            PassResourceAccess consumer = accesses[later].Access;
            for (int earlier = 0; earlier < later; earlier++)
            {
                PassResourceAccess producer = accesses[earlier].Access;
                if (producer.Pass == consumer.Pass || !HazardOverlaps(structure, producer, consumer))
                    continue;
                edges.Add(new GraphDependencyEdge(producer.Pass, consumer.Pass));
            }
        }
        GraphDependencyEdge[] result = edges.ToArray();
        Array.Sort(result, static (left, right) =>
        {
            int predecessor = left.Predecessor.Slot.CompareTo(right.Predecessor.Slot);
            return predecessor != 0
                ? predecessor
                : left.Consumer.Slot.CompareTo(right.Consumer.Slot);
        });
        return new GraphHazardTimeline(result);
    }

    private static bool HazardOverlaps(
        GraphStructure structure,
        PassResourceAccess earlier,
        PassResourceAccess later)
    {
        bool bothRead = earlier.Mode == GraphAccessMode.Read && later.Mode == GraphAccessMode.Read;
        return earlier.TargetKind switch
        {
            GraphAccessTargetKind.Buffer => !bothRead && Overlaps(
                ResolveRange(earlier.BufferRange, structure.Buffers.Get(earlier.Target).Description.Size),
                ResolveRange(later.BufferRange, structure.Buffers.Get(later.Target).Description.Size)),
            GraphAccessTargetKind.Texture => Overlaps(earlier.TextureRange, later.TextureRange) &&
                (!bothRead || earlier.TextureLayout != later.TextureLayout),
            GraphAccessTargetKind.QueryPool => !bothRead && Overlaps(earlier.QueryRange, later.QueryRange),
            GraphAccessTargetKind.RayTracingShaderTable => !bothRead,
            _ => false,
        };
    }

    private static bool Overlaps(in BufferRange left, in BufferRange right) =>
        left.Offset < right.Offset + right.Size && right.Offset < left.Offset + left.Size;

    private static bool Overlaps(in QueryRange left, in QueryRange right) =>
        left.FirstQuery < right.FirstQuery + right.QueryCount &&
        right.FirstQuery < left.FirstQuery + left.QueryCount;

    private static bool Overlaps(
        in TextureSubresourceRange left,
        in TextureSubresourceRange right) =>
        left.FirstMipLevel < right.FirstMipLevel + right.MipLevelCount &&
        right.FirstMipLevel < left.FirstMipLevel + left.MipLevelCount &&
        left.FirstArrayLayer < right.FirstArrayLayer + right.ArrayLayerCount &&
        right.FirstArrayLayer < left.FirstArrayLayer + left.ArrayLayerCount &&
        (left.Aspects & right.Aspects) != TextureAspects.None;

    private static GraphIdentity[] RepairTopologicalSchedule(
        GraphStructure structure,
        GraphIdentity[] passIds,
        GraphIdentity[] previousSchedule,
        Dictionary<GraphIdentity, GraphIdentity[]> predecessors,
        Dictionary<GraphIdentity, GraphIdentity[]> successors,
        HashSet<GraphIdentity> dirty,
        out int revisited)
    {
        if (passIds.Length == 0)
        {
            revisited = 0;
            return [];
        }

        var existing = new HashSet<GraphIdentity>(passIds);
        var schedule = new List<GraphIdentity>(passIds.Length);
        foreach (GraphIdentity pass in previousSchedule)
            if (existing.Remove(pass)) schedule.Add(pass);
        GraphIdentity[] added = existing.ToArray();
        Array.Sort(added, (left, right) => structure.Passes.Get(left).DeclarationOrdinal.CompareTo(
            structure.Passes.Get(right).DeclarationOrdinal));
        foreach (GraphIdentity pass in added)
        {
            int ordinal = structure.Passes.Get(pass).DeclarationOrdinal;
            int insertion = schedule.FindIndex(candidate =>
                structure.Passes.Get(candidate).DeclarationOrdinal > ordinal);
            schedule.Insert(insertion < 0 ? schedule.Count : insertion, pass);
            dirty.Add(pass);
        }

        Dictionary<GraphIdentity, int> positions = BuildPositions(schedule);
        int minimum = schedule.Count;
        int maximum = -1;
        foreach (GraphIdentity pass in dirty)
        {
            if (!positions.TryGetValue(pass, out int position)) continue;
            minimum = Math.Min(minimum, position);
            maximum = Math.Max(maximum, position);
        }
        foreach ((GraphIdentity predecessor, GraphIdentity[] consumers) in successors)
        {
            if (!positions.TryGetValue(predecessor, out int predecessorPosition)) continue;
            foreach (GraphIdentity consumer in consumers)
            {
                if (!positions.TryGetValue(consumer, out int consumerPosition) ||
                    predecessorPosition < consumerPosition)
                    continue;
                minimum = Math.Min(minimum, consumerPosition);
                maximum = Math.Max(maximum, predecessorPosition);
            }
        }

        if (maximum < minimum)
        {
            revisited = 0;
            ValidateTopological(schedule, positions, successors);
            return schedule.ToArray();
        }

        var window = schedule.GetRange(minimum, maximum - minimum + 1);
        var windowSet = new HashSet<GraphIdentity>(window);
        var indegree = new Dictionary<GraphIdentity, int>();
        var ready = new PriorityQueue<GraphIdentity, int>();
        foreach (GraphIdentity pass in window)
        {
            int count = 0;
            foreach (GraphIdentity predecessor in predecessors.GetValueOrDefault(pass, []))
                if (windowSet.Contains(predecessor)) count++;
            indegree.Add(pass, count);
            if (count == 0) ready.Enqueue(pass, positions[pass]);
        }
        int destination = minimum;
        while (ready.TryDequeue(out GraphIdentity pass, out _))
        {
            schedule[destination++] = pass;
            foreach (GraphIdentity successor in successors.GetValueOrDefault(pass, []))
            {
                if (!indegree.TryGetValue(successor, out int count)) continue;
                count--;
                indegree[successor] = count;
                if (count == 0) ready.Enqueue(successor, positions[successor]);
            }
        }
        if (destination != maximum + 1)
            throw new InvalidOperationException(
                "RG5001: The render graph contains a structural dependency cycle.");

        positions = BuildPositions(schedule);
        ValidateTopological(schedule, positions, successors);
        revisited = window.Count;
        return schedule.ToArray();
    }

    private static void ValidateTopological(
        List<GraphIdentity> schedule,
        Dictionary<GraphIdentity, int> positions,
        Dictionary<GraphIdentity, GraphIdentity[]> successors)
    {
        foreach ((GraphIdentity predecessor, GraphIdentity[] consumers) in successors)
        {
            if (!positions.TryGetValue(predecessor, out int predecessorPosition)) continue;
            foreach (GraphIdentity consumer in consumers)
                if (positions.TryGetValue(consumer, out int consumerPosition) &&
                    predecessorPosition >= consumerPosition)
                    throw new InvalidOperationException(
                        "RG5001: The render graph contains a structural dependency cycle.");
        }
        if (positions.Count != schedule.Count)
            throw new InvalidOperationException(
                "RG5001: The retained schedule contains duplicate Pass identities.");
    }

    private static GraphResourceScheduleLifetime ResolveLifetime(
        GraphStructure structure,
        GraphIdentity[] accessIds,
        Dictionary<GraphIdentity, int> positions)
    {
        int first = int.MaxValue;
        int last = -1;
        foreach (GraphIdentity accessId in accessIds)
        {
            if (!structure.Accesses.Contains(accessId)) continue;
            PassResourceAccess access = structure.Accesses.Get(accessId);
            if (!positions.TryGetValue(access.Pass, out int position)) continue;
            first = Math.Min(first, position);
            last = Math.Max(last, position);
        }
        return new GraphResourceScheduleLifetime(first, last);
    }

    private static IEnumerable<GraphIdentity> EnumerateTransientResources(
        ulong owner,
        GraphStructure structure)
    {
        for (int i = 0; i < structure.Buffers.Count; i++)
            if (structure.Buffers.Rows[i].Ownership == RenderGraphResourceOwnership.GraphOwned &&
                structure.Buffers.Rows[i].Lifetime == RenderGraphResourceLifetime.PerFrame)
                yield return structure.Buffers.IdentityAt(owner, i);
        for (int i = 0; i < structure.Textures.Count; i++)
            if (structure.Textures.Rows[i].Ownership == RenderGraphResourceOwnership.GraphOwned &&
                structure.Textures.Rows[i].Lifetime == RenderGraphResourceLifetime.PerFrame)
                yield return structure.Textures.IdentityAt(owner, i);
    }

    private static bool IsTransient(GraphStructure structure, in GraphIdentity resource)
    {
        if (structure.Buffers.Contains(resource))
        {
            GraphBuffer row = structure.Buffers.Get(resource);
            return row.Ownership == RenderGraphResourceOwnership.GraphOwned &&
                row.Lifetime == RenderGraphResourceLifetime.PerFrame;
        }
        if (!structure.Textures.Contains(resource)) return false;
        GraphTexture texture = structure.Textures.Get(resource);
        return texture.Ownership == RenderGraphResourceOwnership.GraphOwned &&
            texture.Lifetime == RenderGraphResourceLifetime.PerFrame;
    }

    private static Dictionary<GraphIdentity, int> BuildPositions(IReadOnlyList<GraphIdentity> schedule)
    {
        var result = new Dictionary<GraphIdentity, int>(schedule.Count);
        for (int i = 0; i < schedule.Count; i++) result.Add(schedule[i], i);
        return result;
    }

    private static GraphIdentity[] BuildIds<T>(ulong owner, SlotMap<T> values)
    {
        var result = new GraphIdentity[values.Count];
        for (int i = 0; i < result.Length; i++) result[i] = values.IdentityAt(owner, i);
        return result;
    }

    private static int[][] BuildPassAccessIndices(GraphStructure structure)
    {
        var result = new int[structure.Passes.Count][];
        for (int passIndex = 0; passIndex < result.Length; passIndex++)
        {
            GraphPass pass = structure.Passes.Rows[passIndex];
            int[] dense = new int[pass.Accesses.Count];
            for (int i = 0; i < dense.Length; i++)
                dense[i] = structure.Accesses.DenseIndex(pass.Accesses[i]);
            result[passIndex] = dense;
        }
        return result;
    }

    private static int[][] BuildExplicitPredecessorIndices(GraphStructure structure)
    {
        var lists = new List<int>[structure.Passes.Count];
        for (int i = 0; i < lists.Length; i++) lists[i] = [];
        foreach (ExplicitPassOrder order in structure.Orders)
        {
            int predecessor = structure.Passes.DenseIndex(order.Predecessor);
            int consumer = structure.Passes.DenseIndex(order.Consumer);
            if (!lists[consumer].Contains(predecessor)) lists[consumer].Add(predecessor);
        }
        var result = new int[lists.Length][];
        for (int i = 0; i < lists.Length; i++)
        {
            lists[i].Sort();
            result[i] = lists[i].ToArray();
        }
        return result;
    }

    private static HashSet<GraphDependencyEdge> BuildExplicitEdges(GraphStructure structure)
    {
        var result = new HashSet<GraphDependencyEdge>();
        foreach (ExplicitPassOrder order in structure.Orders)
        {
            _ = structure.Passes.Get(order.Predecessor);
            _ = structure.Passes.Get(order.Consumer);
            result.Add(new GraphDependencyEdge(order.Predecessor, order.Consumer));
        }
        return result;
    }

    private static Dictionary<GraphIdentity, GraphIdentity[]> Clone(
        Dictionary<GraphIdentity, GraphIdentity[]> source) => new(source);

    private static void AddAdjacency(
        Dictionary<GraphIdentity, GraphIdentity[]> values,
        in GraphIdentity key,
        in GraphIdentity value)
    {
        GraphIdentity[] current = values.GetValueOrDefault(key, []);
        if (current.Contains(value)) return;
        GraphIdentity[] next = new GraphIdentity[current.Length + 1];
        current.CopyTo(next, 0);
        next[^1] = value;
        values[key] = next;
    }

    private static void RemoveAdjacency(
        Dictionary<GraphIdentity, GraphIdentity[]> values,
        in GraphIdentity key,
        in GraphIdentity value)
    {
        if (!values.TryGetValue(key, out GraphIdentity[]? current)) return;
        int index = Array.IndexOf(current, value);
        if (index < 0) return;
        if (current.Length == 1)
        {
            values.Remove(key);
            return;
        }
        var next = new GraphIdentity[current.Length - 1];
        if (index != 0) Array.Copy(current, 0, next, 0, index);
        if (index != next.Length)
            Array.Copy(current, index + 1, next, index, next.Length - index);
        values[key] = next;
    }

    private static void Increment(
        Dictionary<GraphDependencyEdge, int> counts,
        in GraphDependencyEdge edge) => counts[edge] = counts.GetValueOrDefault(edge) + 1;

    private static void Decrement(
        Dictionary<GraphDependencyEdge, int> counts,
        in GraphDependencyEdge edge)
    {
        int count = counts[edge] - 1;
        if (count == 0) counts.Remove(edge);
        else counts[edge] = count;
    }

    private static bool SetEquals(
        HashSet<GraphDependencyEdge> left,
        HashSet<GraphDependencyEdge> right) => left.Count == right.Count && left.SetEquals(right);

    private static bool ContainsResource(GraphStructure structure, in GraphIdentity identity) =>
        structure.Buffers.Contains(identity) || structure.Textures.Contains(identity) ||
        structure.QueryPools.Contains(identity) || structure.ShaderTables.Contains(identity);

    private static void ValidateBuffer(GraphBuffer buffer)
    {
        if (buffer.Description.Size == 0)
            throw new ArgumentOutOfRangeException(nameof(buffer.Description.Size));
        if (buffer.Description.Usages == BufferUsages.None)
            throw new ArgumentOutOfRangeException(nameof(buffer.Description.Usages));
        if (!Enum.IsDefined(buffer.MemoryType))
            throw new ArgumentOutOfRangeException(nameof(buffer.MemoryType));
        if (buffer.Ownership == RenderGraphResourceOwnership.GraphOwned &&
            buffer.Lifetime == RenderGraphResourceLifetime.PerFrame &&
            buffer.Description.Usages.HasFlag(BufferUsages.Shareable))
            throw new NotSupportedException("Transient shareable buffers are not supported.");
    }

    private static void ValidateTexture(GraphTexture texture)
    {
        if (texture.Width == 0 || texture.Height == 0 || texture.Depth == 0 ||
            texture.MipLevelCount == 0 || texture.ArrayLayerCount == 0 ||
            texture.SampleCount == 0)
            throw new ArgumentOutOfRangeException(nameof(texture));
        if (texture.Usages == TextureUsages.None)
            throw new ArgumentOutOfRangeException(nameof(texture.Usages));
        if (texture.Ownership == RenderGraphResourceOwnership.GraphOwned &&
            texture.Lifetime == RenderGraphResourceLifetime.PerFrame &&
            texture.Usages.HasFlag(TextureUsages.Shareable))
            throw new NotSupportedException("Transient shareable textures are not supported.");
    }

    private static void ValidateView(GraphStructure structure, GraphView view)
    {
        switch (view.Kind)
        {
            case GraphViewKind.BufferCbv:
            case GraphViewKind.BufferSrv:
            case GraphViewKind.BufferUav:
                GraphBuffer buffer = structure.Buffers.Get(view.Buffer);
                _ = ResolveRange(view.BufferRange, buffer.Description.Size);
                if (view.AdditionalBuffer.IsValid)
                {
                    GraphBuffer counter = structure.Buffers.Get(view.AdditionalBuffer);
                    if (view.CounterOffset > counter.Description.Size ||
                        counter.Description.Size - view.CounterOffset < sizeof(uint))
                        throw new ArgumentOutOfRangeException(nameof(view.CounterOffset));
                }
                break;
            case GraphViewKind.TextureSrv:
            case GraphViewKind.TextureUav:
            case GraphViewKind.ColorAttachment:
            case GraphViewKind.DepthStencil:
                ValidateTextureRange(structure.Textures.Get(view.Texture), view.TextureRange);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(view.Kind));
        }
    }

    private static void ValidatePass(RenderGraph graph, GraphPass pass)
    {
        if (string.IsNullOrWhiteSpace(pass.Label))
            throw new ArgumentException("A pass label cannot be empty.", nameof(pass));
        if (!Enum.IsDefined(pass.Kind)) throw new ArgumentOutOfRangeException(nameof(pass.Kind));
        if (!Enum.IsDefined(pass.Options.Culling) ||
            !Enum.IsDefined(pass.Options.Scheduling) ||
            !Enum.IsDefined(pass.Options.Recording) ||
            !Enum.IsDefined(pass.Options.RasterMerging))
            throw new ArgumentOutOfRangeException(nameof(pass.Options));
        _ = graph.ResolveQueueCandidates(pass.Queue, pass.Kind);
    }

    private static void ValidateAccess(GraphStructure structure, PassResourceAccess access)
    {
        if (!structure.Passes.Contains(access.Pass))
            throw new ArgumentException("A resource access references a stale Pass.");
        if (!Enum.IsDefined(access.Mode) || !Enum.IsDefined(access.Coverage))
            throw new ArgumentOutOfRangeException(nameof(access));
        bool physicalResource = access.TargetKind is
            GraphAccessTargetKind.Buffer or GraphAccessTargetKind.Texture;
        if (physicalResource &&
            (access.Sync == PipelineSync.None || access.Access == ResourceAccess.NoAccess))
            throw new ArgumentException("Pass access must name a real synchronization and access scope.");
        switch (access.TargetKind)
        {
            case GraphAccessTargetKind.Buffer:
                GraphBuffer buffer = structure.Buffers.Get(access.Target);
                _ = ResolveRange(access.BufferRange, buffer.Description.Size);
                ValidateMode(structure, access);
                break;
            case GraphAccessTargetKind.Texture:
                GraphTexture texture = structure.Textures.Get(access.Target);
                ValidateTextureRange(texture, access.TextureRange);
                ValidateMode(structure, access);
                break;
            case GraphAccessTargetKind.QueryPool:
                GraphQueryPool pool = structure.QueryPools.Get(access.Target);
                if (access.QueryRange.QueryCount == 0 ||
                    access.QueryRange.FirstQuery >= pool.Resource.Description.Count ||
                    access.QueryRange.QueryCount >
                        pool.Resource.Description.Count - access.QueryRange.FirstQuery)
                    throw new ArgumentOutOfRangeException(nameof(access.QueryRange));
                break;
            case GraphAccessTargetKind.RayTracingShaderTable:
                _ = structure.ShaderTables.Get(access.Target);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(access.TargetKind));
        }
    }

    private static void ValidateMode(GraphStructure structure, PassResourceAccess access)
    {
        bool writes = ResourceAccessRules.Writes(access.Access);
        string label = structure.Passes.Get(access.Pass).Label;
        bool readOnlyUnorderedAccess =
            access.Mode == GraphAccessMode.Read &&
            access.Access == ResourceAccess.UnorderedAccess;
        if (access.Mode == GraphAccessMode.Read && writes && !readOnlyUnorderedAccess)
        {
            throw new ArgumentException(
                $"Pass '{label}' declares Read with write access '{access.Access}'.");
        }
        if (access.Mode != GraphAccessMode.Read && !writes)
        {
            throw new ArgumentException(
                $"Pass '{label}' declares {access.Mode} without a write access bit in '{access.Access}'.");
        }
    }

    internal static BufferRange ResolveRange(in BufferRange range, ulong size)
    {
        if (range.IsWhole) return new BufferRange(0, size);
        if (range.Size == 0 || range.Offset > size || range.Size > size - range.Offset)
            throw new ArgumentOutOfRangeException(nameof(range));
        return range;
    }

    internal static void ValidateTextureRange(GraphTexture texture, in TextureSubresourceRange range)
    {
        if (range.MipLevelCount == 0 || range.ArrayLayerCount == 0 ||
            range.FirstMipLevel >= texture.MipLevelCount ||
            range.MipLevelCount > texture.MipLevelCount - range.FirstMipLevel ||
            range.FirstArrayLayer >= texture.ArrayLayerCount ||
            range.ArrayLayerCount > texture.ArrayLayerCount - range.FirstArrayLayer ||
            range.Aspects == TextureAspects.None)
            throw new ArgumentOutOfRangeException(nameof(range));
    }
}
