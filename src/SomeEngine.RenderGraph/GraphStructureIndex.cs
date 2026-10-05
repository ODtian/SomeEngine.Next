using GraphAdjacency = System.Collections.Immutable.ImmutableDictionary<SomeEngine.RenderGraph.GraphIdentity, SomeEngine.RenderGraph.GraphIdentity[]>.Builder;

namespace SomeEngine.RenderGraph;

internal readonly record struct GraphDependencyEdge(GraphIdentity Predecessor, GraphIdentity Consumer);
internal sealed class GraphStructureIndex
{
    internal static readonly GraphStructureIndex Empty = new(new GraphStructure());
    private GraphStructureIndex(GraphStructure structure) => Structure = structure;
    internal GraphStructure Structure { get; private set; }
    internal GraphTable<GraphIdentity> BufferIds => Structure.Buffers.Identities;
    internal GraphTable<GraphIdentity> TextureIds => Structure.Textures.Identities;
    internal GraphTable<GraphIdentity> QueryPoolIds => Structure.QueryPools.Identities;
    internal GraphTable<GraphIdentity> ShaderTableIds => Structure.ShaderTables.Identities;
    internal GraphTable<GraphIdentity> ViewIds => Structure.Views.Identities;
    internal ImmutableDictionary<GraphIdentity, GraphIdentity[]> PassCopies { get; private init; } = ImmutableDictionary<GraphIdentity, GraphIdentity[]>.Empty;
    internal GraphTable<GraphIdentity> PassIds => Structure.Passes.Identities;
    internal GraphTable<GraphIdentity> AccessIds => Structure.Accesses.Identities;
    internal ImmutableDictionary<Resource, GraphIdentity> ResourceIdentities { get; private set; } =
        ImmutableDictionary.Create<Resource, GraphIdentity>(ReferenceEqualityComparer.Instance);
    internal ImmutableDictionary<GraphIdentity, GraphIdentity[]> ResourceAccesses { get; private init; } = ImmutableDictionary<GraphIdentity, GraphIdentity[]>.Empty;
    internal ImmutableDictionary<GraphIdentity, GraphIdentity[]> PassAccessIdentities { get; private init; } = ImmutableDictionary<GraphIdentity, GraphIdentity[]>.Empty;
    internal ImmutableDictionary<GraphIdentity, GraphIdentity[]> ExplicitPredecessorIdentities { get; private init; } = ImmutableDictionary<GraphIdentity, GraphIdentity[]>.Empty;
    internal ImmutableDictionary<GraphIdentity, GraphIdentity[]> ExplicitSuccessorIdentities { get; private init; } = ImmutableDictionary<GraphIdentity, GraphIdentity[]>.Empty;
    internal ImmutableDictionary<GraphIdentity, GraphIdentity[]> ViewResources { get; private init; } = ImmutableDictionary<GraphIdentity, GraphIdentity[]>.Empty;
    internal ImmutableDictionary<GraphParameterInput, GraphIdentity[]> ParameterInputAccesses { get; private init; } = ImmutableDictionary<GraphParameterInput, GraphIdentity[]>.Empty;
    internal ImmutableDictionary<GraphTextureInput, GraphIdentity[]> TextureInputViews { get; private init; } = ImmutableDictionary<GraphTextureInput, GraphIdentity[]>.Empty;
    internal ImmutableDictionary<GraphTextureInput, GraphIdentity[]> TextureInputAccesses { get; private init; } = ImmutableDictionary<GraphTextureInput, GraphIdentity[]>.Empty;
    internal ImmutableDictionary<GraphBufferInput, GraphIdentity[]> BufferInputViews { get; private init; } = ImmutableDictionary<GraphBufferInput, GraphIdentity[]>.Empty;
    internal ImmutableDictionary<GraphBufferInput, GraphIdentity[]> BufferInputAccesses { get; private init; } = ImmutableDictionary<GraphBufferInput, GraphIdentity[]>.Empty;
    internal ImmutableDictionary<GraphIdentity, GraphIdentity[]> ResourceViews { get; private init; } = ImmutableDictionary<GraphIdentity, GraphIdentity[]>.Empty;
    internal ImmutableDictionary<GraphIdentity, GraphIdentity[]> BindingReferences { get; private init; } = ImmutableDictionary<GraphIdentity, GraphIdentity[]>.Empty;
    internal ImmutableDictionary<GraphIdentity, GraphIdentity[]> BindingDependents { get; private init; } = ImmutableDictionary<GraphIdentity, GraphIdentity[]>.Empty;
    internal ImmutableHashSet<GraphDependencyEdge> ExplicitEdges { get; private init; } = ImmutableHashSet<GraphDependencyEdge>.Empty;
    internal ImmutableSortedDictionary<long, GraphIdentity> OrderedPasses { get; private init; } = ImmutableSortedDictionary<long, GraphIdentity>.Empty;
    internal ImmutableDictionary<GraphIdentity, long> OrderPositions { get; private init; } = ImmutableDictionary<GraphIdentity, long>.Empty;
    internal ImmutableHashSet<GraphIdentity> TransientResources { get; private init; } = ImmutableHashSet<GraphIdentity>.Empty;

    internal GraphStructureIndex WithStructure(GraphStructure structure)
    {
        var result = (GraphStructureIndex)MemberwiseClone();
        result.Structure = structure;
        return result;
    }

    internal void UpdateResourceIdentities(GraphStructureIndex previous, GraphEditChanges changes)
    {
        var identities = previous.ResourceIdentities.ToBuilder();
        foreach (GraphIdentity identity in changes.Buffers)
        {
            GraphBuffer? before = previous.Structure.Buffers.Contains(identity) ? previous.Structure.Buffers.Get(identity) : null;
            GraphBuffer? after = Structure.Buffers.Contains(identity) ? Structure.Buffers.Get(identity) : null;
            Update(identity, before?.PersistentResource ?? before?.RegisteredResource,
                after?.PersistentResource ?? after?.RegisteredResource);
        }
        foreach (GraphIdentity identity in changes.Textures)
        {
            GraphTexture? before = previous.Structure.Textures.Contains(identity) ? previous.Structure.Textures.Get(identity) : null;
            GraphTexture? after = Structure.Textures.Contains(identity) ? Structure.Textures.Get(identity) : null;
            Update(identity, before?.PersistentResource ?? before?.RegisteredResource,
                after?.PersistentResource ?? after?.RegisteredResource);
        }
        ResourceIdentities = identities.ToImmutable();
        return;

        void Update(GraphIdentity identity, Resource? before, Resource? after)
        {
            if (before is not null && identities.TryGetValue(before, out GraphIdentity found) && found == identity)
                identities.Remove(before);
            if (after is not null) identities[after] = identity;
        }
    }

    internal static GraphStructureIndex Build(RenderGraph graph, GraphStructure structure)
    {
        var changes = new GraphEditChanges();
        TouchEverything(graph.Identity, structure, changes);
        return Update(graph, Empty, structure, changes, out _);
    }

    internal static GraphStructureIndex Update(RenderGraph graph, GraphStructureIndex previous,
        GraphStructure structure, GraphEditChanges changes, out GraphIndexUpdateStatistics statistics)
    {
        GraphAdjacency resourceAccesses = previous.ResourceAccesses.ToBuilder();
        GraphAdjacency passAccesses = previous.PassAccessIdentities.ToBuilder();
        UpdateAccessAdjacency(previous.Structure, structure, changes, resourceAccesses, passAccesses);
        GraphAdjacency viewResources = previous.ViewResources.ToBuilder();
        GraphAdjacency resourceViews = previous.ResourceViews.ToBuilder();
        GraphAdjacency bindingReferences = previous.BindingReferences.ToBuilder();
        GraphAdjacency bindingDependents = previous.BindingDependents.ToBuilder();
        UpdateBindingAdjacency(structure, changes, bindingReferences, bindingDependents);
        foreach (GraphIdentity view in changes.ReleasedViews)
        {
            if (!structure.Views.Contains(view) || bindingDependents.GetValueOrDefault(view, []).Length != 0) continue;
            structure.Views.Remove(view);
            changes.TouchView(view, membership: true);
        }
        UpdateViewAdjacency(previous.Structure, structure, changes, viewResources, resourceViews);

        // Resource edits invalidate only their dependent views. Those row copies also
        // isolate newly created GPU views from readers of the preceding structure.
        foreach (GraphIdentity resource in changes.Buffers.Concat(changes.Textures))
            foreach (GraphIdentity view in resourceViews.GetValueOrDefault(resource, []))
                if (structure.Views.Contains(view))
                {
                    structure.Views.GetMutable(view).PersistentView = null;
                    changes.TouchView(view);
                }
        ValidateTouched(graph, structure, changes, resourceAccesses, resourceViews);
        foreach (GraphIdentity target in changes.AffectedResources.Concat(changes.Views).Concat(changes.PersistentBindings))
            foreach (GraphIdentity dependent in bindingDependents.GetValueOrDefault(target, []))
                ValidateBindingReferences(structure, graph.Device, dependent);

        GraphAdjacency explicitPredecessors = previous.ExplicitPredecessorIdentities.ToBuilder();
        GraphAdjacency explicitSuccessors = previous.ExplicitSuccessorIdentities.ToBuilder();
        var explicitEdges = previous.ExplicitEdges.ToBuilder();
        var addedExplicitEdges = new List<GraphDependencyEdge>();
        foreach (ExplicitPassOrder order in changes.Orders)
        {
            GraphDependencyEdge edge = new(order.Predecessor, order.Consumer);
            if (structure.Orders.Contains(order))
            {
                _ = structure.Passes.Get(order.Predecessor);
                _ = structure.Passes.Get(order.Consumer);
                if (explicitEdges.Add(edge))
                {
                    AddAdjacency(explicitPredecessors, order.Consumer, order.Predecessor);
                    AddAdjacency(explicitSuccessors, order.Predecessor, order.Consumer);
                    addedExplicitEdges.Add(edge);
                }
            }
            else if (explicitEdges.Remove(edge))
            {
                RemoveAdjacency(explicitPredecessors, order.Consumer, order.Predecessor);
                RemoveAdjacency(explicitSuccessors, order.Predecessor, order.Consumer);
            }
        }

        var positions = previous.OrderPositions.ToBuilder();
        var schedule = previous.OrderedPasses.ToBuilder();
        var reordered = new HashSet<GraphIdentity>();
        foreach (GraphIdentity pass in changes.Passes)
        {
            if (!structure.Passes.Contains(pass))
            {
                if (positions.TryGetValue(pass, out long oldPosition))
                {
                    positions.Remove(pass);
                    schedule.Remove(oldPosition);
                }
            }
            else if (!positions.ContainsKey(pass))
            {
                long position = structure.Passes.Get(pass).DeclarationOrdinal;
                while (schedule.ContainsKey(position)) position = checked(position + 1);
                positions.Add(pass, position);
                schedule.Add(position, pass);
                reordered.Add(pass);
            }
        }
        foreach (GraphDependencyEdge edge in addedExplicitEdges)
            RepairOrder(edge, explicitPredecessors, explicitSuccessors, positions, schedule, reordered);

        var transientResources = previous.TransientResources.ToBuilder();
        foreach (GraphIdentity resource in changes.Buffers.Concat(changes.Textures))
        {
            if (IsTransient(structure, resource)) transientResources.Add(resource);
            else transientResources.Remove(resource);
        }
        statistics = new GraphIndexUpdateStatistics(0, 0, reordered.Count, 0);
        return new GraphStructureIndex(structure)
        {
            PassCopies = UpdatePassCopies(previous, structure, changes),
            ParameterInputAccesses = UpdateInputs(previous.ParameterInputAccesses, previous.Structure.Accesses, structure.Accesses, changes.Accesses, static row => row.ParameterInput),
            TextureInputViews = UpdateInputs(previous.TextureInputViews, previous.Structure.Views, structure.Views, changes.Views, static row => row.TextureInput),
            TextureInputAccesses = UpdateInputs(previous.TextureInputAccesses, previous.Structure.Accesses, structure.Accesses, changes.Accesses, static row => row.TextureInput),
            BufferInputViews = UpdateInputs(previous.BufferInputViews, previous.Structure.Views, structure.Views, changes.Views, static row => row.BufferInput),
            BufferInputAccesses = UpdateInputs(previous.BufferInputAccesses, previous.Structure.Accesses, structure.Accesses, changes.Accesses, static row => row.BufferInput),
            ResourceAccesses = resourceAccesses.ToImmutable(),
            PassAccessIdentities = passAccesses.ToImmutable(),
            ExplicitPredecessorIdentities = explicitPredecessors.ToImmutable(),
            ExplicitSuccessorIdentities = explicitSuccessors.ToImmutable(),
            ViewResources = viewResources.ToImmutable(),
            ResourceViews = resourceViews.ToImmutable(),
            BindingReferences = bindingReferences.ToImmutable(),
            BindingDependents = bindingDependents.ToImmutable(),
            ExplicitEdges = explicitEdges.ToImmutable(),
            OrderedPasses = schedule.ToImmutable(),
            OrderPositions = positions.ToImmutable(),
            TransientResources = transientResources.ToImmutable(),
        };
    }

    private static ImmutableDictionary<GraphIdentity, GraphIdentity[]> UpdatePassCopies(
        GraphStructureIndex previous, GraphStructure structure, GraphEditChanges changes)
    {
        GraphAdjacency copies = previous.PassCopies.ToBuilder();
        foreach (GraphIdentity pass in changes.Passes)
        {
            GraphIdentity before = previous.Structure.Passes.Contains(pass)
                ? previous.Structure.Passes.Get(pass).CommandSource : default;
            GraphIdentity after = structure.Passes.Contains(pass)
                ? structure.Passes.Get(pass).CommandSource : default;
            if (before == after) continue;
            if (before.IsValid) RemoveAdjacency(copies, before, pass);
            if (after.IsValid) AddAdjacency(copies, after, pass);
        }
        return copies.ToImmutable();
    }

    private static ImmutableDictionary<TInput, GraphIdentity[]> UpdateInputs<TRow, TInput>(
        ImmutableDictionary<TInput, GraphIdentity[]> previous,
        GraphRows<TRow> before, GraphRows<TRow> after,
        IEnumerable<GraphIdentity> changes, Func<TRow, TInput?> readInput) where TInput : class
    {
        var values = previous.ToBuilder();
        foreach (GraphIdentity identity in changes)
        {
            TInput? oldInput = before.Contains(identity) ? readInput(before.Get(identity)) : null;
            TInput? newInput = after.Contains(identity) ? readInput(after.Get(identity)) : null;
            if (ReferenceEquals(oldInput, newInput)) continue;
            if (oldInput is not null)
            {
                GraphIdentity[] references = values[oldInput];
                if (references.Length == 1) values.Remove(oldInput);
                else
                {
                    int index = Array.IndexOf(references, identity);
                    var remaining = new GraphIdentity[references.Length - 1];
                    references.AsSpan(0, index).CopyTo(remaining);
                    references.AsSpan(index + 1).CopyTo(remaining.AsSpan(index));
                    values[oldInput] = remaining;
                }
            }
            if (newInput is not null)
            {
                GraphIdentity[] references = values.GetValueOrDefault(newInput, []);
                var added = new GraphIdentity[references.Length + 1];
                references.CopyTo(added, 0);
                added[^1] = identity;
                values[newInput] = added;
            }
        }
        return values.ToImmutable();
    }

    private static void RepairOrder(GraphDependencyEdge edge,
        IReadOnlyDictionary<GraphIdentity, GraphIdentity[]> predecessors,
        IReadOnlyDictionary<GraphIdentity, GraphIdentity[]> successors,
        ImmutableDictionary<GraphIdentity, long>.Builder positions,
        ImmutableSortedDictionary<long, GraphIdentity>.Builder schedule,
        HashSet<GraphIdentity> reordered)
    {
        if (!positions.TryGetValue(edge.Predecessor, out long upper) ||
            !positions.TryGetValue(edge.Consumer, out long lower) || upper < lower) return;
        var forward = new HashSet<GraphIdentity>();
        var backward = new HashSet<GraphIdentity>();
        var work = new Stack<GraphIdentity>();
        work.Push(edge.Consumer);
        while (work.TryPop(out GraphIdentity pass))
        {
            if (!positions.TryGetValue(pass, out long rank) || rank > upper || !forward.Add(pass)) continue;
            if (pass == edge.Predecessor)
                throw new InvalidOperationException("RG5001: The render graph contains a structural dependency cycle.");
            foreach (GraphIdentity next in successors.GetValueOrDefault(pass, [])) work.Push(next);
        }
        work.Push(edge.Predecessor);
        while (work.TryPop(out GraphIdentity pass))
        {
            if (!positions.TryGetValue(pass, out long rank) || rank < lower || !backward.Add(pass)) continue;
            if (forward.Contains(pass))
                throw new InvalidOperationException("RG5001: The render graph contains a structural dependency cycle.");
            foreach (GraphIdentity next in predecessors.GetValueOrDefault(pass, [])) work.Push(next);
        }
        GraphIdentity[] before = backward.OrderBy(pass => positions[pass]).ToArray();
        GraphIdentity[] after = forward.OrderBy(pass => positions[pass]).ToArray();
        long[] ranks = before.Concat(after).Select(pass => positions[pass]).Order().ToArray();
        int index = 0;
        foreach (GraphIdentity pass in before.Concat(after))
        {
            long rank = ranks[index++];
            if (positions[pass] == rank) continue;
            positions[pass] = rank;
            schedule[rank] = pass;
            reordered.Add(pass);
        }
    }

    private static void UpdateBindingAdjacency(GraphStructure structure, GraphEditChanges changes,
        GraphAdjacency references, GraphAdjacency dependents)
    {
        foreach (GraphIdentity holder in changes.Passes.Concat(changes.PersistentBindings).Concat(changes.ShaderTables))
        {
            if (references.TryGetValue(holder, out GraphIdentity[]? old))
            {
                references.Remove(holder);
                foreach (GraphIdentity target in old) RemoveAdjacency(dependents, target, holder);
            }
            var targets = new HashSet<GraphIdentity>();
            foreach (GraphParameterResourceBinding binding in EnumerateBindings(structure, holder))
            {
                if (binding.Value.IsValid) targets.Add(binding.Value);
                if (binding.SecondaryValue.IsValid) targets.Add(binding.SecondaryValue);
            }
            if (structure.Passes.Contains(holder))
            {
                targets.UnionWith(structure.Passes.Get(holder).PersistentBindings);
                targets.UnionWith(structure.Passes.Get(holder).ViewReferences);
            }
            if (targets.Count == 0) continue;
            references.Add(holder, targets.ToArray());
            foreach (GraphIdentity target in targets) AddAdjacency(dependents, target, holder);
        }
    }

    private static IEnumerable<GraphParameterResourceBinding> EnumerateBindings(GraphStructure structure, GraphIdentity holder)
    {
        if (structure.Passes.Contains(holder)) return structure.Passes.Get(holder).ParameterBindings;
        if (structure.PersistentBindings.Contains(holder)) return structure.PersistentBindings.Get(holder).Inventory;
        if (structure.ShaderTables.Contains(holder)) return structure.ShaderTables.Get(holder).Inventory;
        return [];
    }

    private static void ValidateBindingReferences(GraphStructure structure, Device device, GraphIdentity holder)
    {
        foreach (GraphParameterResourceBinding binding in EnumerateBindings(structure, holder))
            binding.ValidateStatic(structure, device);
        if (structure.Passes.Contains(holder))
        {
            GraphPass pass = structure.Passes.Get(holder);
            foreach (GraphIdentity view in pass.ViewReferences)
                if (!structure.Views.Contains(view)) throw new ArgumentException("A Pass references a removed view.");
            foreach (GraphIdentity bindings in pass.PersistentBindings)
                if (!structure.PersistentBindings.Contains(bindings))
                    throw new ArgumentException("A Pass references removed parameter bindings.");
        }
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
        foreach (ExplicitPassOrder order in structure.Orders)
            changes.TouchOrder(order.Predecessor, order.Consumer);
    }

    private static void UpdateAccessAdjacency(
        GraphStructure previous,
        GraphStructure next,
        GraphEditChanges changes,
        GraphAdjacency resourceAccesses,
        GraphAdjacency passAccesses)
    {
        foreach (GraphIdentity accessId in changes.Accesses)
        {
            if (previous.Accesses.Contains(accessId))
            {
                PassResourceAccess old = previous.Accesses.Get(accessId);
                if (old.BufferInput is null && old.TextureInput is null && old.ParameterInput is null) RemoveAdjacency(resourceAccesses, old.Target, accessId);
                RemoveAdjacency(passAccesses, old.Pass, accessId);
            }
            if (!next.Accesses.Contains(accessId)) continue;
            PassResourceAccess current = next.Accesses.Get(accessId);
            if (current.BufferInput is null && current.TextureInput is null && current.ParameterInput is null) AddAdjacency(resourceAccesses, current.Target, accessId);
            AddAdjacency(passAccesses, current.Pass, accessId);
        }
        foreach (GraphIdentity pass in changes.Passes)
            if (!next.Passes.Contains(pass)) passAccesses.Remove(pass);
    }

    private static void UpdateViewAdjacency(
        GraphStructure previous,
        GraphStructure next,
        GraphEditChanges changes,
        GraphAdjacency viewResources,
        GraphAdjacency resourceViews)
    {
        foreach (GraphIdentity viewId in changes.Views)
        {
            if (viewResources.TryGetValue(viewId, out GraphIdentity[]? oldResources))
                foreach (GraphIdentity resource in oldResources)
                    RemoveAdjacency(resourceViews, resource, viewId);
            else if (previous.Views.Contains(viewId))
                foreach (GraphIdentity resource in ResolveViewResources(previous.Views.Get(viewId)))
                    RemoveAdjacency(resourceViews, resource, viewId);
            viewResources.Remove(viewId);
            if (!next.Views.Contains(viewId)) continue;
            GraphIdentity[] resources = ResolveViewResources(next.Views.Get(viewId));
            viewResources.Add(viewId, resources);
            foreach (GraphIdentity resource in resources)
                AddAdjacency(resourceViews, resource, viewId);
        }
    }

    private static GraphIdentity[] ResolveViewResources(GraphView view)
    {
        if (view.BufferInput is not null || view.TextureInput is not null) return [];
        if (view.Kind is GraphViewKind.BufferCbv or GraphViewKind.BufferSrv or GraphViewKind.BufferUav)
            return view.AdditionalBuffer.IsValid ? [view.Buffer, view.AdditionalBuffer] : [view.Buffer];
        return [view.Texture];
    }

    private static void ValidateTouched(
        RenderGraph graph,
        GraphStructure structure,
        GraphEditChanges changes,
        GraphAdjacency resourceAccesses,
        GraphAdjacency resourceViews)
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


    }

    private static void ValidateResourceDependents(
        GraphStructure structure,
        in GraphIdentity resource,
        GraphAdjacency resourceAccesses,
        GraphAdjacency resourceViews,
        RenderGraph graph)
    {
        foreach (GraphIdentity access in resourceAccesses.GetValueOrDefault(resource, []))
            ValidateAccess(structure, structure.Accesses.Get(access));
        foreach (GraphIdentity view in resourceViews.GetValueOrDefault(resource, []))
            ValidateView(structure, structure.Views.Get(view));
    }

    private static void RejectReferencedRemoval(
        in GraphIdentity resource,
        GraphAdjacency resourceAccesses,
        GraphAdjacency resourceViews)
    {
        if (resourceAccesses.GetValueOrDefault(resource, []).Length != 0 ||
            resourceViews.GetValueOrDefault(resource, []).Length != 0)
            throw new InvalidOperationException(
                "A graph resource cannot be removed while accesses or views reference it.");
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

    private static void AddAdjacency(
        GraphAdjacency values,
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
        GraphAdjacency values,
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
                if (view.BufferInput is not null)
                {
                    BufferUsages required = view.Kind switch
                    {
                        GraphViewKind.BufferCbv => BufferUsages.Constant,
                        GraphViewKind.BufferUav => BufferUsages.ShaderWrite,
                        _ => BufferUsages.ShaderRead,
                    };
                    DevelopmentCheck.Require((view.BufferInput.Usages & required) == required,
                        "A buffer input view requires its corresponding usage.");
                    break;
                }
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
                if (view.TextureInput is null)
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
            !Enum.IsDefined(pass.Options.Recording))
            throw new ArgumentOutOfRangeException(nameof(pass.Options));
        _ = graph.ResolveQueueCandidates(pass.Queue, pass.Kind);
    }

    private static void ValidateAccess(GraphStructure structure, PassResourceAccess access)
    {
        if (!structure.Passes.Contains(access.Pass))
            throw new ArgumentException("A resource access references a stale Pass.");
        if (!Enum.IsDefined(access.Mode) || !Enum.IsDefined(access.Coverage) ||
            !Enum.IsDefined(access.UnorderedAccessHazards))
            throw new ArgumentOutOfRangeException(nameof(access));
        UnorderedAccessHazardRules.Validate(
            access.UnorderedAccessHazards,
            access.Mode,
            access.Coverage,
            access.Access,
            access.TextureLayout,
            access.ResultContents);
        bool physicalResource = access.TargetKind is
            GraphAccessTargetKind.Buffer or GraphAccessTargetKind.Texture;
        if (physicalResource &&
            (access.Sync == PipelineSync.None || access.Access == ResourceAccess.NoAccess))
            throw new ArgumentException("Pass access must name a real synchronization and access scope.");
        if (access.ParameterInput is not null)
        {
            DevelopmentCheck.Require(access.ParameterInput.Graph == access.Pass.Owner,
                "A parameter input belongs to another graph.");
            ValidateMode(structure, access);
            return;
        }
        switch (access.TargetKind)
        {
            case GraphAccessTargetKind.Buffer:
                if (access.BufferInput is not null)
                {
                    DevelopmentCheck.Require(access.BufferInput.Graph == access.Pass.Owner,
                        "A buffer input belongs to another graph.");
                    ValidateMode(structure, access);
                    break;
                }
                GraphBuffer buffer = structure.Buffers.Get(access.Target);
                _ = ResolveRange(access.BufferRange, buffer.Description.Size);
                ValidateMode(structure, access);
                break;
            case GraphAccessTargetKind.Texture:
                if (access.TextureInput is not null)
                {
                    DevelopmentCheck.Require(access.TextureInput.Graph == access.Pass.Owner,
                        "A texture input belongs to another graph.");
                    ValidateMode(structure, access);
                    break;
                }
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

    internal static BufferRange ResolveRange(in BufferRange range, ulong size, string? label = null)
    {
        if (range.IsWhole) return new BufferRange(0, size);
        DevelopmentCheck.Require(range.Size != 0 && range.Offset <= size && range.Size <= size - range.Offset,
            $"The buffer range must be nonempty and contained in its resource: '{label}', offset={range.Offset}, size={range.Size}, bufferSize={size}.");
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
