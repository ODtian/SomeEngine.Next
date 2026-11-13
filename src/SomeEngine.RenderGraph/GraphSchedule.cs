namespace SomeEngine.RenderGraph;

internal readonly record struct GraphScheduleResource(GraphAccessTargetKind Kind, GraphIdentity Resource, object? Input, bool Secondary = false);
internal readonly record struct GraphScheduleBoundary(GraphIdentity Region, FrameSectionPosition Position);
internal readonly record struct FramePassPosition(GraphIdentity Region, FrameSectionPosition Position, int Ordinal);
internal readonly record struct GraphScheduleResourceState(GraphScheduleResource Resource, GraphIdentity Pass,
    PipelineSync Sync, ResourceAccess Access, TextureLayout Layout, Queue Queue)
{
    internal GraphIdentity[] Before { get; init; } = [];
    internal GraphIdentity[] After { get; init; } = [];
    internal BufferContentScope ContentScope { get; init; }
    internal GraphAccessMode Mode { get; init; }
}

internal readonly record struct GraphScheduleValueSource(GraphIdentity Pass, bool Shared);
internal sealed record GraphScheduleValueUse(GraphIdentity Consumer, GraphScheduleValueSource[] Sources);

internal sealed class GraphSchedulePass(GraphIdentity identity, GraphPass definition, int row)
{
    internal GraphIdentity Identity { get; } = identity;
    internal GraphPass Definition { get; } = definition;
    internal int Row { get; } = row;
    internal readonly HashSet<GraphIdentity> Predecessors = [];
    internal readonly HashSet<GraphIdentity> ValuePredecessors = [];
    internal readonly HashSet<GraphIdentity> Successors = [];
    internal Queue Queue = null!;
    internal bool Root;
    internal bool Live;
    internal int[] PredecessorRows = [], SuccessorRows = [], ValuePredecessorRows = [];
    internal int PreviousOnQueue = -1, NextOnQueue = -1;
    internal long[] Begin = [], End = [];
    internal long QueuePosition;
}

/// <summary>The graph's persistent dependency, queue and region-boundary data.</summary>
internal sealed class GraphSchedule(RenderGraph graph)
{
    private readonly List<GraphEditChanges> _edits = [];
    private RenderGraphDebugOptions _debug;
    private FrameSubmissionMode _submissionMode;
    private readonly Dictionary<GraphIdentity, GraphSchedulePass> _passes = [];
    private readonly Dictionary<GraphScheduleResource, List<(GraphIdentity Identity, PassResourceAccess Access)>> _resources = [];
    private readonly Dictionary<GraphScheduleBoundary, GraphScheduleResourceState[]> _boundaries = [];
    private readonly Dictionary<GraphIdentity, GraphScheduleResource> _accessResources = [];
    private GraphIdentity[] _order = [];
    private readonly List<GraphScheduleValueUse> _valueUses = [];
    private readonly List<GraphScheduleValueUse> _resourceRoots = [];
    private readonly Dictionary<GraphIdentity, GraphScheduleValueUse[]> _valueUsesByConsumer = [];
    private readonly Dictionary<int, GraphSchedulePass> _passesByRow = [];
    private bool[] _enabled = [];
    private ulong _enabledScheduleVersion;
    private readonly Dictionary<GraphIdentity, int> _passRows = [];
    private readonly Dictionary<FramePassPosition, int> _framePassRows = [];
    private readonly Stack<int> _freePassRows = new();
    private int _passRowCount;

    internal ulong Version { get; private set; }
    internal IReadOnlyDictionary<GraphIdentity, GraphSchedulePass> Passes => _passes;
    internal IReadOnlyDictionary<GraphIdentity, GraphScheduleResource> AccessResources => _accessResources;
    internal IReadOnlyList<GraphIdentity> Order => _order;
    internal IReadOnlyList<GraphScheduleValueUse> ValueUses => _valueUses;
    internal IReadOnlyList<GraphScheduleValueUse> ResourceRoots => _resourceRoots;
    internal bool Reused { get; private set; }
    internal FrameConnections FrameConnections { get; } = new();
    internal int PassRowCount => _passRowCount;
    internal int GetPassRow(GraphIdentity identity) => _passRows[identity];
    internal GraphSchedulePass? GetPassAtRow(int row) => _passesByRow.GetValueOrDefault(row);
    internal IReadOnlyList<GraphScheduleValueUse> GetValueUses(GraphIdentity consumer) =>
        _valueUsesByConsumer.GetValueOrDefault(consumer, []);

    internal int GetFramePassRow(FramePassPosition position)
    {
        if (!_framePassRows.TryGetValue(position, out int row))
        {
            row = AddPassRow();
            _framePassRows.Add(position, row);
        }
        return row;
    }

    private int AddPassRow() => _freePassRows.TryPop(out int row) ? row : _passRowCount++;

    private void UpdatePassRows(GraphStructureIndex source)
    {
        foreach (GraphIdentity identity in _passRows.Keys.Where(id => !source.Structure.Passes.Contains(id)).ToArray())
        {
            _freePassRows.Push(_passRows[identity]);
            _passRows.Remove(identity);
        }
        foreach (FramePassPosition position in _framePassRows.Keys.Where(position => position.Region.IsValid &&
                     !source.Structure.Regions.Contains(position.Region)).ToArray())
        {
            _freePassRows.Push(_framePassRows[position]);
            _framePassRows.Remove(position);
        }
        foreach (GraphIdentity identity in source.PassIds)
            if (!_passRows.ContainsKey(identity)) _passRows.Add(identity, AddPassRow());
    }

    internal void ApplyEdit(GraphEditChanges changes) => _edits.Add(changes);

    internal void Update(GraphStructureIndex source, in RenderGraphFrameOptions options)
    {
        RenderGraphDebugOptions debug = options.Debug & (RenderGraphDebugOptions.DisableCulling |
            RenderGraphDebugOptions.DeclarationOrderScheduling);
        Reused = Version != 0 && _edits.Count == 0 && _debug == debug && _submissionMode == options.SubmissionMode;
        if (Reused) return;
        _debug = debug;
        _submissionMode = options.SubmissionMode;
        _passes.Clear();
        _resources.Clear();
        _boundaries.Clear();
        _accessResources.Clear();
        _valueUses.Clear();
        _resourceRoots.Clear();
        UpdatePassRows(source);
        GraphStructure structure = source.Structure;
        _passesByRow.Clear();
        foreach (GraphIdentity id in source.PassIds)
        {
            var pass = new GraphSchedulePass(id, structure.Passes.Get(id), _passRows[id]);
            _passes.Add(id, pass);
            _passesByRow.Add(pass.Row, pass);
        }
        foreach (GraphDependencyEdge edge in source.ExplicitEdges) AddEdge(edge.Predecessor, edge.Consumer, value: false);
        GraphIdentity[] declaration = source.PassIds.OrderBy(id => _passes[id].Definition.DeclarationOrdinal).ToArray();
        for (int position = 0; position < declaration.Length; position++)
        {
            GraphSchedulePass pass = _passes[declaration[position]];
            pass.Root = pass.Definition.Options.Culling == PassCullingMode.NeverCull ||
                (_debug & RenderGraphDebugOptions.DisableCulling) != 0;
            if (pass.Definition.Options.Scheduling == PassSchedulingMode.PreserveDeclarationPosition ||
                (_debug & RenderGraphDebugOptions.DeclarationOrderScheduling) != 0)
            {
                for (int before = 0; before < position; before++) AddEdge(declaration[before], pass.Identity, value: false);
                for (int after = position + 1; after < declaration.Length; after++) AddEdge(pass.Identity, declaration[after], value: false);
            }
        }
        foreach (GraphIdentity id in source.AccessIds)
        {
            PassResourceAccess access = structure.Accesses.Get(id);
            GraphScheduleResource resource = Resource(access);
            _accessResources.Add(id, resource);
            if (!_resources.TryGetValue(resource, out var accesses)) _resources.Add(resource, accesses = []);
            accesses.Add((id, access));
        }
        foreach (var resource in _resources)
        {
            List<(GraphIdentity Identity, PassResourceAccess Access)> accesses = OrderUsers(resource.Value, source);
            resource.Value.Clear(); resource.Value.AddRange(accesses);
            if (resource.Key.Kind == GraphAccessTargetKind.Texture) BuildTextureDependencies(resource.Key, accesses, structure);
            else BuildBufferDependencies(resource.Key, accesses, structure);
        }
        _order = Sort(_passes.Keys, id => _passes[id].Predecessors);
        SelectQueues();
        _valueUsesByConsumer.Clear();
        foreach (var group in _valueUses.GroupBy(use => use.Consumer)) _valueUsesByConsumer.Add(group.Key, group.ToArray());
        BuildBoundaries(structure);
        _edits.Clear();
        Version++;
    }

    internal bool ApplyEnabled(Func<GraphIdentity, bool> enabled)
    {
        bool changed = _enabledScheduleVersion != Version;
        if (!changed)
            foreach (GraphIdentity identity in _order)
                if (_enabled[_passRows[identity]] != enabled(identity)) { changed = true; break; }
        if (!changed) return false;
        _enabledScheduleVersion = Version;
        _enabled = new bool[_passRowCount];
        foreach (GraphSchedulePass pass in _passes.Values)
        {
            _enabled[pass.Row] = enabled(pass.Identity);
            pass.Live = false;
            pass.PredecessorRows = pass.SuccessorRows = pass.ValuePredecessorRows = [];
            pass.Begin = pass.End = [];
            pass.PreviousOnQueue = pass.NextOnQueue = -1;
        }
        var work = new Stack<GraphIdentity>(_order.Where(id => _passes[id].Root && _enabled[_passes[id].Row]));
        foreach (GraphScheduleValueUse root in _resourceRoots) SelectSources(root);
        while (work.TryPop(out GraphIdentity identity))
        {
            GraphSchedulePass pass = _passes[identity];
            if (pass.Live || !_enabled[pass.Row]) continue;
            pass.Live = true;
            foreach (GraphScheduleValueUse use in GetValueUses(identity)) SelectSources(use);
        }
        var successors = new Dictionary<int, List<int>>();
        foreach (GraphSchedulePass pass in _passes.Values) successors.Add(pass.Row, []);
        foreach (GraphIdentity identity in _order)
        {
            GraphSchedulePass pass = _passes[identity];
            if (!pass.Live) continue;
            var predecessors = new HashSet<int>();
            var visited = new HashSet<GraphIdentity>();
            var before = new Stack<GraphIdentity>(pass.Predecessors);
            while (before.TryPop(out GraphIdentity predecessor))
            {
                if (!visited.Add(predecessor)) continue;
                GraphSchedulePass source = _passes[predecessor];
                if (source.Live) predecessors.Add(source.Row);
                else foreach (GraphIdentity previous in source.Predecessors) before.Push(previous);
            }
            pass.PredecessorRows = predecessors.ToArray();
            foreach (int predecessor in pass.PredecessorRows) successors[predecessor].Add(pass.Row);
        }
        foreach (GraphSchedulePass pass in _passes.Values) pass.SuccessorRows = successors[pass.Row].ToArray();
        foreach (GraphSchedulePass pass in _passes.Values)
            if (pass.Live) pass.ValuePredecessorRows = GetValueUses(pass.Identity).SelectMany(use => SelectedSources(use)).Distinct().ToArray();
        BuildFrontiers();
        return true;

        void SelectSources(GraphScheduleValueUse use)
        {
            foreach (int row in SelectedSources(use)) work.Push(_passesByRow[row].Identity);
        }

        IEnumerable<int> SelectedSources(GraphScheduleValueUse use)
        {
            for (int index = use.Sources.Length - 1; index >= 0; index--)
            {
                GraphScheduleValueSource source = use.Sources[index];
                if (!_enabled[_passes[source.Pass].Row]) continue;
                yield return _passes[source.Pass].Row;
                if (!source.Shared) break;
            }
        }
    }

    internal GraphScheduleResourceState[] Boundary(GraphIdentity region, FrameSectionPosition position) =>
        _boundaries.GetValueOrDefault(new(region, position), []);

    internal IEnumerable<GraphScheduleResourceState> Boundary(GraphIdentity region, FrameSectionPosition position,
        Func<GraphScheduleResource, bool> touched, Func<GraphIdentity, bool> enabled)
    {
        long ordinal = position == FrameSectionPosition.Start
            ? graph.StructureIndex.Structure.GetRegionInterval(region).Start
            : graph.StructureIndex.Structure.GetRegionInterval(region).End;
        foreach (GraphScheduleResourceState boundary in Boundary(region, position))
        {
            if (!touched(boundary.Resource)) continue;
            var accesses = _resources[boundary.Resource].Where(item => enabled(item.Access.Pass)).ToArray();
            PassResourceAccess? selected = accesses.LastOrDefault(item =>
                _passes[item.Access.Pass].Definition.DeclarationOrdinal < ordinal).Access;
            selected ??= accesses.FirstOrDefault(item =>
                _passes[item.Access.Pass].Definition.DeclarationOrdinal >= ordinal).Access;
            if (selected is null) continue;
            yield return new(boundary.Resource, selected.Pass, selected.Sync, selected.Access,
                selected.TextureLayout, _passes[selected.Pass].Queue)
            {
                Before = accesses.Where(item => _passes[item.Access.Pass].Definition.DeclarationOrdinal < ordinal)
                    .GroupBy(item => _passes[item.Access.Pass].Queue).Select(group => group.Last().Access.Pass).Distinct().ToArray(),
                After = accesses.Where(item => _passes[item.Access.Pass].Definition.DeclarationOrdinal >= ordinal)
                    .GroupBy(item => _passes[item.Access.Pass].Queue).Select(group => group.First().Access.Pass).Distinct().ToArray(),
                ContentScope = selected.BufferContentScope,
                Mode = selected.Mode,
            };
        }
    }

    private static GraphScheduleResource Resource(PassResourceAccess access) => new(access.TargetKind, access.Target,
        (object?)access.BufferInput ?? access.TextureInput ?? (object?)access.ParameterInput, access.ParameterSecondary);

    private List<(GraphIdentity Identity, PassResourceAccess Access)> OrderUsers(
        List<(GraphIdentity Identity, PassResourceAccess Access)> accesses, GraphStructureIndex source)
    {
        var users = accesses.Select(item => item.Access.Pass).ToHashSet();
        var predecessors = users.ToDictionary(id => id, _ => new HashSet<GraphIdentity>());
        foreach (GraphIdentity consumer in users)
        {
            var visited = new HashSet<GraphIdentity>();
            var work = new Stack<GraphIdentity>(source.ExplicitPredecessorIdentities.GetValueOrDefault(consumer, []));
            while (work.TryPop(out GraphIdentity pass))
            {
                if (!visited.Add(pass)) continue;
                if (users.Contains(pass)) predecessors[consumer].Add(pass);
                foreach (GraphIdentity before in source.ExplicitPredecessorIdentities.GetValueOrDefault(pass, [])) work.Push(before);
            }
        }
        GraphIdentity[] order = Sort(users, id => predecessors[id]);
        var rank = order.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        return accesses.OrderBy(item => rank[item.Access.Pass]).ThenBy(item => source.Structure.Accesses.DenseIndex(item.Identity)).ToList();
    }

    private void BuildBufferDependencies(GraphScheduleResource resource,
        List<(GraphIdentity Identity, PassResourceAccess Access)> accesses, GraphStructure structure)
    {
        var coordinates = new SortedSet<ulong> { 0 };
        ulong length = resource.Kind == GraphAccessTargetKind.Buffer && resource.Resource.IsValid
            ? structure.Buffers.Get(resource.Resource).Description.Size : ulong.MaxValue;
        coordinates.Add(length);
        foreach (var item in accesses)
        {
            (ulong first, ulong end) = Range(resource.Kind, item.Access, length);
            coordinates.Add(first); coordinates.Add(end);
        }
        ulong[] points = coordinates.ToArray();
        for (int segment = 0; segment + 1 < points.Length; segment++)
            ConnectUsers(resource, accesses.Where(item =>
            {
                (ulong first, ulong end) = Range(resource.Kind, item.Access, length);
                return first <= points[segment] && end >= points[segment + 1];
            }).Select(item => item.Access), structure);
    }

    private static (ulong First, ulong End) Range(GraphAccessTargetKind kind, PassResourceAccess access, ulong length)
    {
        if (kind == GraphAccessTargetKind.QueryPool)
            return (access.QueryRange.FirstQuery, (ulong)access.QueryRange.FirstQuery + access.QueryRange.QueryCount);
        if (kind != GraphAccessTargetKind.Buffer || access.BufferRange.IsWhole) return (0, length);
        return (access.BufferRange.Offset, access.BufferRange.Offset + access.BufferRange.Size);
    }

    private void BuildTextureDependencies(GraphScheduleResource resource,
        List<(GraphIdentity Identity, PassResourceAccess Access)> accesses, GraphStructure structure)
    {
        GraphTexture? texture = resource.Resource.IsValid ? structure.Textures.Get(resource.Resource) : null;
        uint mips = texture?.MipLevelCount ?? Math.Max(1u, accesses.Max(item => item.Access.TextureRange.FirstMipLevel + item.Access.TextureRange.MipLevelCount));
        uint layers = texture?.ArrayLayerCount ?? Math.Max(1u, accesses.Max(item => item.Access.TextureRange.FirstArrayLayer + item.Access.TextureRange.ArrayLayerCount));
        foreach (TextureAspects aspect in new[] { TextureAspects.Color, TextureAspects.Depth, TextureAspects.Stencil })
            for (uint layer = 0; layer < layers; layer++)
                for (uint mip = 0; mip < mips; mip++)
                {
                    uint selectedMip = mip, selectedLayer = layer;
                    ConnectUsers(resource, accesses.Where(item =>
                    {
                        TextureSubresourceRange range = item.Access.TextureRange;
                        return (range.Aspects & aspect) != 0 && selectedMip >= range.FirstMipLevel &&
                            (range.MipLevelCount == 0 || selectedMip < range.FirstMipLevel + range.MipLevelCount) &&
                            selectedLayer >= range.FirstArrayLayer &&
                            (range.ArrayLayerCount == 0 || selectedLayer < range.FirstArrayLayer + range.ArrayLayerCount);
                    }).Select(item => item.Access), structure);
                }
    }

    private void ConnectUsers(GraphScheduleResource resource, IEnumerable<PassResourceAccess> accesses, GraphStructure structure)
    {
        GraphIdentity writer = default;
        var readers = new HashSet<GraphIdentity>();
        var sharedWriters = new HashSet<GraphIdentity>();
        TextureLayout? layout = null;
        var versions = new List<GraphScheduleValueSource>();
        foreach (PassResourceAccess access in accesses)
        {
            bool reads = access.Mode != GraphAccessMode.Write || access.Coverage == WriteCoverage.Partial;
            if (reads)
            {
                _valueUses.Add(new(access.Pass, versions.ToArray()));
                foreach (GraphScheduleValueSource version in versions) AddEdge(version.Pass, access.Pass, value: true);
            }
            if (resource.Kind == GraphAccessTargetKind.Texture && layout is { } previous && previous != access.TextureLayout)
                foreach (GraphIdentity reader in readers) AddEdge(reader, access.Pass, value: false);
            layout = access.TextureLayout;
            if (access.Mode == GraphAccessMode.Read) { readers.Add(access.Pass); continue; }
            AddEdge(writer, access.Pass, value: false);
            foreach (GraphIdentity reader in readers) AddEdge(reader, access.Pass, value: false);
            if (access.UnorderedAccessHazards != UnorderedAccessHazardMode.Ordered)
            {
                sharedWriters.Add(access.Pass);
                versions.Add(new(access.Pass, Shared: true));
                continue;
            }
            foreach (GraphIdentity shared in sharedWriters) AddEdge(shared, access.Pass, value: false);
            sharedWriters.Clear(); readers.Clear(); writer = access.Pass;
            versions.Add(new(access.Pass, Shared: false));
        }
        bool persistent = resource.Input is not null || (resource.Kind switch
        {
            GraphAccessTargetKind.Buffer => structure.Buffers.Get(resource.Resource).Lifetime == RenderGraphResourceLifetime.Persistent ||
                structure.Buffers.Get(resource.Resource).Source == RenderGraphResourceSource.Registered,
            GraphAccessTargetKind.Texture => structure.Textures.Get(resource.Resource).Lifetime == RenderGraphResourceLifetime.Persistent ||
                structure.Textures.Get(resource.Resource).Source == RenderGraphResourceSource.Registered,
            _ => true,
        });
        if (persistent) _resourceRoots.Add(new(default, versions.ToArray()));
    }

    private void AddEdge(GraphIdentity predecessor, GraphIdentity consumer, bool value)
    {
        if (!predecessor.IsValid || predecessor == consumer) return;
        _passes[consumer].Predecessors.Add(predecessor);
        _passes[predecessor].Successors.Add(consumer);
        if (value) _passes[consumer].ValuePredecessors.Add(predecessor);
    }

    private GraphIdentity[] Sort(IEnumerable<GraphIdentity> ids, Func<GraphIdentity, IEnumerable<GraphIdentity>> predecessors)
    {
        GraphIdentity[] nodes = ids.ToArray();
        var counts = nodes.ToDictionary(id => id, id => predecessors(id).Count());
        var successors = nodes.ToDictionary(id => id, _ => new List<GraphIdentity>());
        var ready = new PriorityQueue<GraphIdentity, long>();
        foreach (GraphIdentity id in nodes)
        {
            foreach (GraphIdentity before in predecessors(id)) successors[before].Add(id);
            if (counts[id] == 0) ready.Enqueue(id, _passes[id].Definition.DeclarationOrdinal);
        }
        var order = new List<GraphIdentity>(nodes.Length);
        while (ready.TryDequeue(out GraphIdentity id, out _))
        {
            order.Add(id);
            foreach (GraphIdentity next in successors[id])
                if (--counts[next] == 0) ready.Enqueue(next, _passes[next].Definition.DeclarationOrdinal);
        }
        if (order.Count != nodes.Length) throw new InvalidOperationException("The render graph contains a dependency cycle.");
        return order.ToArray();
    }

    private void SelectQueues()
    {
        var times = new Dictionary<Queue, ulong>(ReferenceEqualityComparer.Instance);
        var positions = new Dictionary<Queue, long>(ReferenceEqualityComparer.Instance);
        var finishes = new Dictionary<GraphIdentity, ulong>();
        foreach (GraphIdentity id in _order)
        {
            GraphSchedulePass pass = _passes[id];
            Queue[] candidates = graph.ResolveQueueCandidates(pass.Definition.Queue, pass.Definition.Kind);
            Queue? preferred = null;
            foreach (GraphIdentity consumer in pass.Successors)
                if (_passes[consumer].Definition.Queue.Queue is { } selected && candidates.Contains(selected))
                { preferred = selected; break; }
            preferred ??= candidates.FirstOrDefault(queue => pass.Definition.Kind switch
            {
                GraphPassKind.Compute => queue.Type == QueueType.Compute,
                GraphPassKind.Copy => queue.Type == QueueType.Copy,
                _ => queue.Type == QueueType.Graphics,
            });
            ulong best = ulong.MaxValue;
            foreach (Queue candidate in candidates)
            {
                ulong begin = times.GetValueOrDefault(candidate);
                foreach (GraphIdentity before in pass.Predecessors)
                    begin = Math.Max(begin, finishes[before] + (ReferenceEquals(_passes[before].Queue, candidate) ? 0ul : 1ul));
                ulong finish = begin + Math.Max(1u, pass.Definition.Options.EstimatedExecutionCost);
                if (finish < best || finish == best && ReferenceEquals(candidate, preferred))
                { best = finish; pass.Queue = candidate; }
            }
            times[pass.Queue] = best; finishes[id] = best;
            pass.QueuePosition = positions.GetValueOrDefault(pass.Queue) + PassOrder.PositionGap;
            positions[pass.Queue] = pass.QueuePosition;
        }
    }

    private void BuildFrontiers()
    {
        Queue[] queues = graph.Queues.ToArray();
        var previous = new Dictionary<Queue, GraphSchedulePass>(ReferenceEqualityComparer.Instance);
        foreach (GraphIdentity id in _order)
        {
            GraphSchedulePass pass = _passes[id];
            if (!pass.Live) continue;
            pass.Begin = new long[queues.Length];
            if (previous.TryGetValue(pass.Queue, out GraphSchedulePass? same))
            {
                Merge(pass.Begin, same.End);
                pass.PreviousOnQueue = same.Row;
                same.NextOnQueue = pass.Row;
            }
            foreach (int before in pass.PredecessorRows) Merge(pass.Begin, _passesByRow[before].End);
            pass.End = pass.Begin.ToArray();
            pass.End[Array.IndexOf(queues, pass.Queue)] = pass.QueuePosition;
            previous[pass.Queue] = pass;
        }
        static void Merge(long[] target, long[] source)
        { for (int index = 0; index < target.Length; index++) target[index] = Math.Max(target[index], source[index]); }
    }

    private void BuildBoundaries(GraphStructure structure)
    {
        foreach (GraphIdentity region in structure.Regions.Identities.Prepend(default))
            foreach (FrameSectionPosition position in new[] { FrameSectionPosition.Start, FrameSectionPosition.End })
            {
                GraphRegionEntry interval = structure.GetRegionInterval(region);
                long ordinal = position == FrameSectionPosition.Start ? interval.Start : interval.End;
                var states = new List<GraphScheduleResourceState>();
                foreach (var resource in _resources)
                {
                    PassResourceAccess? selected = resource.Value.LastOrDefault(item =>
                        _passes[item.Access.Pass].Definition.DeclarationOrdinal < ordinal).Access;
                    selected ??= resource.Value.FirstOrDefault(item =>
                        _passes[item.Access.Pass].Definition.DeclarationOrdinal >= ordinal).Access;
                    if (selected is not null)
                        states.Add(new(resource.Key, selected.Pass, selected.Sync, selected.Access,
                            selected.TextureLayout, _passes[selected.Pass].Queue)
                        {
                            Before = resource.Value.Where(item => _passes[item.Access.Pass].Definition.DeclarationOrdinal < ordinal)
                                .GroupBy(item => _passes[item.Access.Pass].Queue).Select(group => group.Last().Access.Pass).Distinct().ToArray(),
                            After = resource.Value.Where(item => _passes[item.Access.Pass].Definition.DeclarationOrdinal >= ordinal)
                                .GroupBy(item => _passes[item.Access.Pass].Queue).Select(group => group.First().Access.Pass).Distinct().ToArray(),
                            ContentScope = selected.BufferContentScope,
                            Mode = selected.Mode,
                        });
                }
                _boundaries.Add(new(region, position), states.ToArray());
            }
    }
}
