using FrameConnectionData = SomeEngine.RenderGraph.FrameRecording.FrameConnectionData;

namespace SomeEngine.RenderGraph;

internal sealed partial class FrameConnections
{
    private FrameConnectionData _frameData;
    private RenderGraphFrameState _frame => _frameData.Frame;
    private IReadOnlyList<FramePass> _passes => _frameData.Passes;
    private IReadOnlyList<FrameBuffer> _buffers => _frameData.Buffers;
    private IReadOnlyList<FrameTexture> _textures => _frameData.Textures;
    private IReadOnlyList<FrameResourceAccess> _accesses => _frameData.Accesses;
    private IReadOnlyList<List<int>> _passAccesses => _frameData.PassAccesses;
    private IReadOnlyDictionary<(GraphAccessTargetKind Kind, int Index), List<int>> _resourceAccessLists => _frameData.ResourceAccesses;
    private IReadOnlyList<int> _dynamicPassRows => _frameData.DynamicPassRows;
    private ulong[] _bufferCoordinateScratch => _frameData.BufferCoordinates;
    private readonly HashSet<FramePassEdge> _changedExecutionEdges = [];
    private readonly HashSet<(GraphAccessTargetKind Kind, int Index)> _resourceLifetimeChanges = [];

    internal void Update(FrameConnectionData frameData)
    {
        _frameData = frameData;
        ChangedPasses.Clear();
        AnalyzedPasses.Clear();
        try { UpdateConnections(); }
        finally { _frameData = default; }
    }

    private int ResolvePass(GraphIdentity identity) => _frameData.ResolvePass(identity);
    private int BuildBufferCoordinates(in FrameBuffer buffer, List<int> accesses) => _frameData.BuildBufferCoordinates(buffer, accesses);
    private int BuildTextureCoordinates(in FrameTexture texture, List<int> accesses) => _frameData.BuildTextureCoordinates(texture, accesses);
    private FrameRecording.TextureRegionEnumerable TextureRegions(in FrameTexture texture, in TextureSubresourceRange range) => _frameData.TextureRegions(texture, range);
    private static void EnsureCapacity<T>(ref T[] values, int count)
    {
        if (values.Length >= count) return;
        int capacity = values.Length == 0 ? 4 : values.Length;
        while (capacity < count) capacity = checked(capacity * 2);
        Array.Resize(ref values, capacity);
    }
    private void ChangeConnection(int pass) => ChangedPasses.Add(pass);
    private void SetConnectionQueue(int pass, Queue queue)
    {
        if (ReferenceEquals(Queues[pass], queue)) return;
        Queues[pass] = queue;
        ChangeConnection(pass);
    }
    private void IncludePassResources(int pass, HashSet<(GraphAccessTargetKind Kind, int Index)> resources)
    {
        if (pass >= _passAccesses.Count || _passAccesses[pass] is not { } accesses) return;
        foreach (int index in accesses)
        {
            FrameResourceAccess access = _accesses[index];
            resources.Add((access.TargetKind, access.ResourceIndex));
        }
    }

    private void UpdateConnections()
    {
        GraphSchedule schedule = _frame.Graph.Schedule;
        Reused = ConnectionsMatch(schedule);
        if (Reused)
        {
            return;
        }
        int count = _passes.Count;
        EnsureCapacity(ref Live, count);
        EnsureCapacity(ref Predecessors, count);
        EnsureCapacity(ref Successors, count);
        EnsureCapacity(ref ValuePredecessors, count);
        EnsureCapacity(ref SameQueuePredecessor, count);
        EnsureCapacity(ref SameQueueSuccessor, count);
        EnsureCapacity(ref StartFrontiers, count);
        EnsureCapacity(ref EndFrontiers, count);
        EnsureCapacity(ref Queues, count);
        for (int pass = 0; pass < count; pass++)
        {
            Predecessors[pass] ??= [];
            Successors[pass] ??= [];
            ValuePredecessors[pass] ??= [];
        }
        bool[] previousLive = Live.AsSpan(0, count).ToArray();
        Queue?[] previousQueues = Queues.ToArray();
        var previousPositions = Order.Nodes.ToDictionary(pass => pass, Order.Position);
        var previousAdditions = PredecessorAdditions;
        PredecessorAdditions = [];
        ValueAdditions.Clear(); Roots.Clear();
        CollectResourceLifetimeChanges(schedule);
        bool baseChanged = schedule.ApplyEnabled(id => _passes[schedule.GetPassRow(id)].Enabled);
        var combined = new List<int>();
        foreach (GraphIdentity id in schedule.Order)
        {
            int pass = schedule.GetPassRow(id);
            combined.Add(pass);
            SetConnectionQueue(pass, schedule.Passes[id].Queue);
            if (!schedule.Reused) AnalyzedPasses.Add(pass);
        }
        BuildFrameSections(combined, schedule);
        ConnectInputValueOrigins(schedule);
        Order.Changed.Clear();
        SetExecutionOrder(combined.Where(pass => _passes[pass].Enabled).ToArray());
        var enabledPredecessors = new Dictionary<int, IReadOnlyList<int>>();
        var frameSuccessors = BuildFrameSuccessors(previousAdditions, PredecessorAdditions);
        var orderConsumers = new HashSet<int>();
        foreach (int consumer in PredecessorAdditions.Keys) IncludeConsumers(consumer, orderConsumers, enabledOnly: true);
        Order.Repair(EnabledPredecessors, pass => _passes[pass].Label, orderConsumers);
        combined.Clear(); combined.AddRange(Order.Nodes);
        ConnectFrameAccesses(combined);
        frameSuccessors = BuildFrameSuccessors(previousAdditions, PredecessorAdditions);

        Array.Clear(Live, 0, count);
        foreach (GraphIdentity id in schedule.Order)
            if (schedule.Passes[id].Live) Live[schedule.GetPassRow(id)] = true;
        var work = new Stack<int>(Roots);
        foreach (var (consumer, producers) in ValueAdditions)
            if (Live[consumer]) foreach (int producer in producers) work.Push(producer);
        while (work.TryPop(out int pass))
        {
            if (!_passes[pass].Enabled || Live[pass]) continue;
            Live[pass] = true;
            if (schedule.GetPassAtRow(pass) is { } persistent)
                foreach (GraphScheduleValueUse use in schedule.GetValueUses(persistent.Identity))
                    foreach (int producer in SelectValueSources(use)) work.Push(producer);
            if (ValueAdditions.TryGetValue(pass, out HashSet<int>? producers))
                foreach (int producer in producers) work.Push(producer);
        }
        var consumers = new HashSet<int>();
        if (baseChanged)
            for (int pass = 0; pass < count; pass++) consumers.Add(pass);
        foreach (int consumer in previousAdditions.Keys.Concat(PredecessorAdditions.Keys).Distinct())
        {
            HashSet<int>? before = previousAdditions.GetValueOrDefault(consumer);
            HashSet<int>? after = PredecessorAdditions.GetValueOrDefault(consumer);
            if (before is not null && after is not null && before.SetEquals(after)) continue;
            IncludeConsumers(consumer, consumers, enabledOnly: false);
        }
        for (int pass = 0; pass < count; pass++)
        {
            if (previousLive[pass] != Live[pass])
            {
                ChangeConnection(pass);
                IncludeConsumers(pass, consumers, enabledOnly: false);
                IncludePassResources(pass, _resourceLifetimeChanges);
            }
            if (!Live[pass]) { ValuePredecessors[pass] = []; continue; }
            GraphSchedulePass? persistent = schedule.GetPassAtRow(pass);
            IReadOnlyList<int> values = persistent is { Live: true } ? persistent.ValuePredecessorRows :
                persistent is not null ? schedule.GetValueUses(persistent.Identity).SelectMany(SelectValueSources).Distinct().ToArray() : [];
            ValuePredecessors[pass] = ValueAdditions.TryGetValue(pass, out HashSet<int>? additions)
                ? values.Concat(additions).Distinct().ToArray() : values;
        }
        _changedExecutionEdges.Clear();
        var successorChanges = new Dictionary<int, HashSet<int>>();
        foreach (int pass in consumers)
        {
            if (_passes[pass].Identity.IsValid && !_passes[pass].SectionBoundary) AnalyzedPasses.Add(pass);
            IReadOnlyList<int> next = [];
            if (Live[pass])
            {
                var selected = new HashSet<int>();
                var visited = new HashSet<int>();
                var before = new Stack<int>(DeclaredPredecessors(pass));
                while (before.TryPop(out int predecessor))
                {
                    if (predecessor == pass) throw new InvalidOperationException("A frame section introduces a dependency cycle.");
                    if (!visited.Add(predecessor)) continue;
                    if (Live[predecessor]) selected.Add(predecessor);
                    else foreach (int previous in DeclaredPredecessors(predecessor)) before.Push(previous);
                }
                GraphSchedulePass? persistent = schedule.GetPassAtRow(pass);
                next = persistent is { Live: true } && selected.SetEquals(persistent.PredecessorRows)
                    ? persistent.PredecessorRows : selected.ToArray();
            }
            foreach (int predecessor in Predecessors[pass])
                if (!next.Contains(predecessor))
                {
                    GetSuccessorChanges(predecessor).Remove(pass);
                    _changedExecutionEdges.Add(new(predecessor, pass));
                }
            foreach (int predecessor in next)
                if (!Predecessors[pass].Contains(predecessor))
                {
                    GetSuccessorChanges(predecessor).Add(pass);
                    _changedExecutionEdges.Add(new(predecessor, pass));
                }
            Predecessors[pass] = next;
        }
        foreach (var (pass, next) in successorChanges)
        {
            GraphSchedulePass? persistent = schedule.GetPassAtRow(pass);
            Successors[pass] = persistent is { Live: true } && next.SetEquals(persistent.SuccessorRows)
                ? persistent.SuccessorRows : next.ToArray();
        }
        foreach (FramePassEdge edge in _changedExecutionEdges)
        {
            ChangeConnection(edge.Predecessor); ChangeConnection(edge.Consumer);


        }
        SetExecutionOrder(combined.Where(pass => Live[pass]).ToArray());
        Order.Changed.RemoveWhere(pass => Order.Contains(pass) &&
            previousPositions.TryGetValue(pass, out long position) && Order.Position(pass) == position);
        foreach (int pass in Order.Changed)
        {
            ChangeConnection(pass);

            IncludePassResources(pass, _resourceLifetimeChanges);
        }
        var frontierWork = new HashSet<int>(consumers);
        foreach (int pass in UpdateQueueConnections(previousLive, previousQueues, previousPositions)) frontierWork.Add(pass);
        foreach (int pass in UpdateFrontiers(schedule, frontierWork)) IncludePassResources(pass, _resourceLifetimeChanges);
        foreach (var resource in _resourceLifetimeChanges)
        {
            int first = -1, last = -1;
            foreach (int access in _resourceAccessLists.GetValueOrDefault(resource, []))
            {
                int pass = _accesses[access].PassIndex;
                if (!Live[pass]) continue;
                if (first < 0 || Order.Position(pass) < Order.Position(first)) first = pass;
                if (last < 0 || Order.Position(pass) > Order.Position(last)) last = pass;
            }
            UpdateResourceLifetimes(resource, first, last);
        }
        CaptureConnections(schedule);
        return;

        HashSet<int> GetSuccessorChanges(int pass)
        {
            if (!successorChanges.TryGetValue(pass, out HashSet<int>? values))
                successorChanges.Add(pass, values = Successors[pass].ToHashSet());
            return values;
        }
        IEnumerable<int> SelectValueSources(GraphScheduleValueUse use)
        {
            for (int index = use.Sources.Length - 1; index >= 0; index--)
            {
                GraphScheduleValueSource source = use.Sources[index];
                int producer = schedule.GetPassRow(source.Pass);
                if (!_passes[producer].Enabled) continue;
                yield return producer;
                if (!source.Shared) break;
            }
        }
        void IncludeConsumers(int first, HashSet<int> destination, bool enabledOnly)
        {
            var visited = new HashSet<int>();
            var pending = new Stack<int>(); pending.Push(first);
            while (pending.TryPop(out int pass))
            {
                if (!visited.Add(pass)) continue;
                bool active = enabledOnly ? _passes[pass].Enabled : Live[pass];
                destination.Add(pass);
                if (pass != first && active) continue;
                if (schedule.GetPassAtRow(pass) is { } persistent)
                    foreach (GraphIdentity consumer in persistent.Successors) pending.Push(schedule.GetPassRow(consumer));
                foreach (int consumer in frameSuccessors.GetValueOrDefault(pass, [])) pending.Push(consumer);
            }
        }
        IReadOnlyList<int> EnabledPredecessors(int pass)
        {
            if (enabledPredecessors.TryGetValue(pass, out IReadOnlyList<int>? result)) return result;
            var selected = new HashSet<int>();
            var visited = new HashSet<int>();
            var before = new Stack<int>(DeclaredPredecessors(pass));
            while (before.TryPop(out int predecessor))
            {
                if (!visited.Add(predecessor)) continue;
                if (_passes[predecessor].Enabled) selected.Add(predecessor);
                else foreach (int previous in DeclaredPredecessors(predecessor)) before.Push(previous);
            }
            result = selected.ToArray();
            enabledPredecessors.Add(pass, result);
            return result;
        }
        IEnumerable<int> DeclaredPredecessors(int pass)
        {
            if (schedule.GetPassAtRow(pass) is { } persistent)
                foreach (GraphIdentity predecessor in persistent.Predecessors) yield return schedule.GetPassRow(predecessor);
            foreach (int predecessor in PredecessorAdditions.GetValueOrDefault(pass, [])) yield return predecessor;
        }
    }

    private static Dictionary<int, HashSet<int>> BuildFrameSuccessors(
        Dictionary<int, HashSet<int>> previous, Dictionary<int, HashSet<int>> current)
    {
        var result = new Dictionary<int, HashSet<int>>();
        foreach (var additions in new[] { previous, current })
            foreach (var (consumer, producers) in additions)
                foreach (int producer in producers)
                {
                    if (!result.TryGetValue(producer, out HashSet<int>? consumers)) result.Add(producer, consumers = []);
                    consumers.Add(consumer);
                }
        return result;
    }

    private void CollectResourceLifetimeChanges(GraphSchedule schedule)
    {
        _resourceLifetimeChanges.Clear();
        if (ScheduleVersion != schedule.Version)
        {
            _resourceLifetimeChanges.UnionWith(_resourceAccessLists.Keys);
            _resourceLifetimeChanges.UnionWith(ResourceLifetimes.Keys);
            return;
        }
        var current = new HashSet<FrameAccessKey>();
        foreach (FrameResourceAccess access in _accesses)
        {
            if (!access.Connected) continue;
            FrameAccessKey key = GetAccessKey(access);
            current.Add(key);
            if (AccessInputs.TryGetValue(key, out FrameAccessInputs? before) && before.Matches(access)) continue;
            _resourceLifetimeChanges.Add((access.TargetKind, access.ResourceIndex));
            if (before is not null) _resourceLifetimeChanges.Add(before.Resource);
        }
        foreach (var (key, before) in AccessInputs)
            if (!current.Contains(key)) _resourceLifetimeChanges.Add(before.Resource);
    }

    private HashSet<int> UpdateQueueConnections(bool[] previousLive, Queue?[] previousQueues,
        Dictionary<int, long> previousPositions)
    {
        if (QueueLanes.Count == 0)
            foreach (Queue queue in _frame.Graph.Queues) QueueLanes.Add(queue, QueueLanes.Count);
        QueueLaneCount = QueueLanes.Count;
        var changes = new HashSet<int>();
        for (int pass = 0; pass < previousLive.Length; pass++)
        {
            if (!previousLive[pass]) continue;
            Queue queue = previousQueues[pass]!;
            long position = previousPositions[pass];
            if (Live[pass] && ReferenceEquals(queue, Queues[pass]) && Order.Position(pass) == position) continue;
            SortedSet<(long Position, int Pass)> members = QueuePasses[queue];
            IncludeNeighbours(members, position, pass);
            members.Remove((position, pass));
            changes.Add(pass);
        }
        foreach (int pass in Order.Nodes)
        {
            Queue queue = Queues[pass]!;
            long position = Order.Position(pass);
            if (previousLive[pass] && ReferenceEquals(previousQueues[pass], queue) && previousPositions[pass] == position) continue;
            if (!QueuePasses.TryGetValue(queue, out var members)) QueuePasses.Add(queue, members = []);
            members.Add((position, pass));
            IncludeNeighbours(members, position, pass);
            changes.Add(pass);
        }
        var frontier = new HashSet<int>();
        foreach (int pass in changes)
        {
            int before = -1, after = -1;
            if (Live[pass])
            {
                var members = QueuePasses[Queues[pass]!];
                long position = Order.Position(pass);
                before = FindQueueNeighbour(members, position, pass, previous: true);
                after = FindQueueNeighbour(members, position, pass, previous: false);
            }
            if (SameQueuePredecessor[pass] != before) frontier.Add(pass);
            if (SameQueuePredecessor[pass] != before || SameQueueSuccessor[pass] != after)
            {
                ChangeConnection(pass);

            }
            SameQueuePredecessor[pass] = before;
            SameQueueSuccessor[pass] = after;
        }
        return frontier;

        void IncludeNeighbours(SortedSet<(long Position, int Pass)> members, long position, int pass)
        {
            int before = FindQueueNeighbour(members, position, pass, previous: true);
            int after = FindQueueNeighbour(members, position, pass, previous: false);
            if (before >= 0) changes.Add(before);
            if (after >= 0) changes.Add(after);
        }
    }

    private static int FindQueueNeighbour(SortedSet<(long Position, int Pass)> members, long position, int pass, bool previous)
    {
        var range = previous
            ? members.GetViewBetween((long.MinValue, int.MinValue), (position, int.MaxValue))
            : members.GetViewBetween((position, int.MinValue), (long.MaxValue, int.MaxValue));
        IEnumerable<(long Position, int Pass)> values = previous ? range.Reverse() : range;
        foreach (var value in values) if (value.Pass != pass) return value.Pass;
        return -1;
    }

    private HashSet<int> UpdateFrontiers(GraphSchedule schedule, IEnumerable<int> changes)
    {
        var work = new SortedSet<(long Position, int Pass)>();
        foreach (int pass in changes) Include(pass);
        var updated = new HashSet<int>();
        while (work.Count != 0)
        {
            int pass = work.Min.Pass;
            work.Remove(work.Min);
            GraphSchedulePass? persistent = schedule.GetPassAtRow(pass);
            long[] begin, end;
            int previous = SameQueuePredecessor[pass];
            bool baseFrontier = persistent is { Live: true } &&
                ReferenceEquals(Predecessors[pass], persistent.PredecessorRows) && previous == persistent.PreviousOnQueue &&
                (previous < 0 || ReferenceEquals(EndFrontiers[previous], schedule.GetPassAtRow(previous)?.End)) &&
                Predecessors[pass].All(before => ReferenceEquals(EndFrontiers[before], schedule.GetPassAtRow(before)?.End));
            if (baseFrontier)
            {
                begin = persistent!.Begin;
                end = persistent.End;
            }
            else
            {
                begin = new long[QueueLaneCount];
                if (previous >= 0) Max(begin, EndFrontiers[previous]);
                foreach (int predecessor in Predecessors[pass]) Max(begin, EndFrontiers[predecessor]);
                end = begin.ToArray();
                int lane = QueueLanes[Queues[pass]!];
                end[lane] = Math.Max(persistent?.QueuePosition ?? 0L, end[lane] + 1L);
                if (persistent is { Live: true } && persistent.Begin.AsSpan().SequenceEqual(begin) && persistent.End.AsSpan().SequenceEqual(end))
                {
                    begin = persistent.Begin;
                    end = persistent.End;
                }
            }
            if (persistent is { Live: true })
            {
                if (persistent.Begin.AsSpan().SequenceEqual(begin)) begin = persistent.Begin;
                if (persistent.End.AsSpan().SequenceEqual(end)) end = persistent.End;
            }
            bool endChanged = !EndFrontiers[pass].AsSpan().SequenceEqual(end);
            bool changed = !StartFrontiers[pass].AsSpan().SequenceEqual(begin) || endChanged;
            StartFrontiers[pass] = begin; EndFrontiers[pass] = end;
            if (!changed) continue;
            updated.Add(pass);
            if (!endChanged) continue;
            foreach (int consumer in Successors[pass]) Include(consumer);
            Include(SameQueueSuccessor[pass]);
        }
        return updated;

        void Include(int pass)
        {
            if (pass >= 0 && Live[pass]) work.Add((Order.Position(pass), pass));
        }
    }

    private FrameAccessKey GetAccessKey(in FrameResourceAccess access) =>
        new(access.Identity.ScopeId == _frame.Graph.Identity, access.Identity.Index, access.Identity.Generation);

    private bool ConnectionsMatch(GraphSchedule schedule)
    {
        if (ScheduleVersion != schedule.Version || PassInputs.Length != _passes.Count) return false;
        for (int pass = 0; pass < _passes.Count; pass++)
            if (PassInputs[pass] != FramePassInputs.Read(_passes[pass])) return false;
        int count = 0;
        foreach (FrameResourceAccess access in _accesses)
        {
            if (!access.Connected) continue;
            count++;
            if (!AccessInputs.TryGetValue(GetAccessKey(access), out FrameAccessInputs? input) || !input.Matches(access)) return false;
        }
        if (count != AccessInputs.Count || ExplicitOrders.Length != _frame.DynamicOrders.Count) return false;
        for (int index = 0; index < _frame.DynamicOrders.Count; index++)
        {
            ExplicitPassOrder order = _frame.DynamicOrders[index];
            if (ExplicitOrders[index] != new FramePassEdge(ResolvePass(order.Predecessor), ResolvePass(order.Consumer))) return false;
        }
        return true;
    }

    private void CaptureConnections(GraphSchedule schedule)
    {
        ScheduleVersion = schedule.Version;
        PassInputs = _passes.Select(pass => FramePassInputs.Read(pass)).ToArray();
        AccessInputs.Clear();
        foreach (FrameResourceAccess access in _accesses)
            if (access.Connected) AccessInputs.Add(GetAccessKey(access), new FrameAccessInputs(access));
        ExplicitOrders = _frame.DynamicOrders.Select(order =>
            new FramePassEdge(ResolvePass(order.Predecessor), ResolvePass(order.Consumer))).ToArray();
    }

    private void UpdateResourceLifetimes((GraphAccessTargetKind Kind, int Index) key, int first, int last)
    {
        if (!ResourceLifetimes.TryGetValue(key, out FrameResourceLifetime? uses))
            ResourceLifetimes.Add(key, uses = new());
        long[] begin = new long[QueueLaneCount], end = new long[QueueLaneCount];
        Array.Fill(begin, long.MaxValue);
        foreach (int index in _resourceAccessLists.GetValueOrDefault(key, []))
        {
            int pass = _accesses[index].PassIndex;
            if (!Live[pass]) continue;
            for (int lane = 0; lane < QueueLaneCount; lane++)
            {
                begin[lane] = Math.Min(begin[lane], StartFrontiers[pass][lane]);
                end[lane] = Math.Max(end[lane], EndFrontiers[pass][lane]);
            }
        }
        for (int lane = 0; lane < begin.Length; lane++) if (begin[lane] == long.MaxValue) begin[lane] = 0;
        if (uses.Version != 0 && uses.First == first && uses.Last == last &&
            uses.Begin.AsSpan().SequenceEqual(begin) && uses.End.AsSpan().SequenceEqual(end)) return;
        uses.First = first; uses.Last = last; uses.Begin = begin; uses.End = end; uses.Version++;
    }

    private void SetExecutionOrder(IReadOnlyList<int> order)
    {
        var current = order.ToHashSet();
        foreach (int previous in Order.Nodes.ToArray())
            if (!current.Contains(previous)) Order.Remove(previous);
        var nextExisting = new int[order.Count];
        int next = -1;
        for (int index = order.Count - 1; index >= 0; index--)
        {
            nextExisting[index] = next;
            if (Order.Contains(order[index])) next = order[index];
        }
        for (int index = 0; index < order.Count; index++)
        {
            int pass = order[index];
            if (Order.Contains(pass)) continue;
            long position = index == 0
                ? nextExisting[index] >= 0 ? Order.Position(nextExisting[index]) - PassOrder.PositionGap : 0L
                : Order.Position(order[index - 1]) + (nextExisting[index] >= 0 ? 1L : PassOrder.PositionGap);
            Order.Add(pass, position);
        }
        Order.SetOrder(order);
    }

    private void BuildFrameSections(List<int> combined, GraphSchedule schedule)
    {
        var groups = Enumerable.Range(0, _frame.DynamicPasses.Count).Select(index => _dynamicPassRows[index])
            .GroupBy(pass => (_passes[pass].Region, _passes[pass].SectionPosition));
        foreach (var group in groups.OrderBy(group => _frame.StructureIndex.Structure.GetRegionInterval(group.Key.Region).Start)
                     .ThenBy(group => group.Key.SectionPosition))
        {
            int[] nodes = group.OrderBy(pass => _passes[pass].DeclarationOrdinal).ToArray();
            var nodeSet = nodes.ToHashSet();
            var localBefore = nodes.ToDictionary(pass => pass, _ => new HashSet<int>());
            foreach (ExplicitPassOrder order in _frame.DynamicOrders)
            {
                int predecessor = ResolvePass(order.Predecessor), consumer = ResolvePass(order.Consumer);
                if (nodeSet.Contains(predecessor) && nodeSet.Contains(consumer)) localBefore[consumer].Add(predecessor);
            }
            nodes = SortSection(nodes, localBefore);
            var resources = new Dictionary<(GraphAccessTargetKind Kind, int Index), List<int>>();
            foreach (int pass in nodes)
            {
                FramePass row = _passes[pass];
                SetConnectionQueue(pass, _frame.Graph.ResolveQueueCandidates(row.QueuePolicy, row.Kind).First());
                if (!row.SectionBoundary) AnalyzedPasses.Add(pass);
                if (row.Enabled && row.Options.Culling == PassCullingMode.NeverCull) Roots.Add(pass);
                foreach (int access in _passAccesses[pass])
                {
                    FrameResourceAccess value = _accesses[access];
                    var key = (value.TargetKind, value.ResourceIndex);
                    if (!resources.TryGetValue(key, out var users)) resources.Add(key, users = []);
                    users.Add(access);
                }
            }
            foreach (var resource in resources)
            {
                var writers = new List<int>();
                var readers = new List<int>();
                foreach (int accessIndex in resource.Value)
                {
                    FrameResourceAccess access = _accesses[accessIndex];
                    int pass = access.PassIndex;
                    if (access.Mode != GraphAccessMode.Write || access.Coverage == WriteCoverage.Partial)
                        foreach (int writer in writers) FrameEdge(writer, pass, value: true);
                    if (access.Mode == GraphAccessMode.Read) { readers.Add(pass); continue; }
                    foreach (int writer in writers) FrameEdge(writer, pass, value: false);
                    foreach (int reader in readers) FrameEdge(reader, pass, value: false);
                    readers.Clear();
                    if (access.UnorderedAccessHazards == UnorderedAccessHazardMode.Ordered) writers.Clear();
                    writers.Add(pass);
                }
                bool retained = resource.Key.Kind switch
                {
                    GraphAccessTargetKind.Buffer => _buffers[resource.Key.Index].Lifetime == RenderGraphResourceLifetime.Persistent ||
                        _buffers[resource.Key.Index].Source == RenderGraphResourceSource.Registered,
                    GraphAccessTargetKind.Texture => _textures[resource.Key.Index].Lifetime == RenderGraphResourceLifetime.Persistent ||
                        _textures[resource.Key.Index].Source == RenderGraphResourceSource.Registered,
                    _ => true,
                };
                if (retained) Roots.AddRange(writers);
                var keys = _resourceAccessLists[resource.Key].Select(index => _accesses[index].Identity)
                    .Where(schedule.AccessResources.ContainsKey).Select(id => schedule.AccessResources[id]).Distinct().ToHashSet();
                int[] users = resource.Value.Select(index => _accesses[index].PassIndex).Distinct().ToArray();
                foreach (GraphScheduleResourceState boundary in schedule.Boundary(group.Key.Region, group.Key.SectionPosition,
                             keys.Contains, id => _passes[schedule.GetPassRow(id)].Enabled))
                {
                    foreach (GraphIdentity before in boundary.Before)
                        foreach (int pass in users) FrameEdge(schedule.GetPassRow(before), pass, value: true);
                    foreach (GraphIdentity after in boundary.After)
                        foreach (int pass in users) FrameEdge(pass, schedule.GetPassRow(after), value: true);
                }
            }
            foreach (var pair in localBefore) foreach (int predecessor in pair.Value) FrameEdge(predecessor, pair.Key, value: false);
            int insertion;
            if (!group.Key.Region.IsValid) insertion = group.Key.SectionPosition == FrameSectionPosition.Start ? 0 : combined.Count;
            else
            {
                GraphRegionEntry interval = _frame.StructureIndex.Structure.GetRegionInterval(group.Key.Region);
                int[] positions = combined.Select((pass, index) => (pass, index)).Where(item =>
                    InRegion(_passes[item.pass].Region, group.Key.Region))
                    .Select(item => item.index).ToArray();
                insertion = positions.Length == 0
                    ? combined.FindLastIndex(pass => _passes[pass].Definition is not null
                        ? _passes[pass].DeclarationOrdinal < interval.Start
                        : _frame.StructureIndex.Structure.GetRegionInterval(_passes[pass].Region).End <= interval.Start) + 1
                    : group.Key.SectionPosition == FrameSectionPosition.Start ? positions.Min() : positions.Max() + 1;
            }
            combined.InsertRange(insertion, nodes);
        }
        foreach (ExplicitPassOrder order in _frame.DynamicOrders) FrameEdge(ResolvePass(order.Predecessor), ResolvePass(order.Consumer), value: false);

        bool InRegion(GraphIdentity region, GraphIdentity ancestor)
        {
            while (region.IsValid)
            {
                if (region == ancestor) return true;
                region = _frame.StructureIndex.Structure.GetRegion(region).Parent;
            }
            return !ancestor.IsValid;
        }
    }

    private int[] SortSection(int[] nodes, Dictionary<int, HashSet<int>> predecessors)
    {
        var ready = new PriorityQueue<int, long>();
        var counts = nodes.ToDictionary(pass => pass, pass => predecessors[pass].Count);
        var order = new List<int>(nodes.Length);
        foreach (int pass in nodes) if (counts[pass] == 0) ready.Enqueue(pass, _passes[pass].DeclarationOrdinal);
        while (ready.TryDequeue(out int pass, out _))
        {
            order.Add(pass);
            foreach (int next in nodes) if (predecessors[next].Contains(pass) && --counts[next] == 0)
                ready.Enqueue(next, _passes[next].DeclarationOrdinal);
        }
        if (order.Count != nodes.Length) throw new InvalidOperationException("A frame section contains a dependency cycle.");
        return order.ToArray();
    }

    private void FrameEdge(int predecessor, int consumer, bool value)
    {
        if (predecessor == consumer) return;
        if (!PredecessorAdditions.TryGetValue(consumer, out var before)) PredecessorAdditions.Add(consumer, before = []);
        before.Add(predecessor);
        if (value)
        {
            if (!ValueAdditions.TryGetValue(consumer, out var values)) ValueAdditions.Add(consumer, values = []);
            values.Add(predecessor);
        }
    }

    private static void Max(long[] destination, long[] source)
    { for (int lane = 0; lane < source.Length; lane++) destination[lane] = Math.Max(destination[lane], source[lane]); }
}
