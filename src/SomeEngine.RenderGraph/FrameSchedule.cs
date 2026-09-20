namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private readonly PassOrder _executionOrder = new();
    private readonly HashSet<int> _scheduleSeeds = [];
    private readonly HashSet<int> _frontierSeeds = [];
    private readonly HashSet<(GraphAccessTargetKind Kind, int Index)> _changedLifetimeResources = [];
    private readonly HashSet<int> _scheduleVisited = [];
    private readonly HashSet<int> _scheduleComponent = [];
    private readonly List<int> _componentSchedule = [];
    private readonly Stack<int> _scheduleWork = [];
    private readonly PriorityQueue<int, int> _frontierWork = new();
    private int[] _componentFinish = [], _sameQueueSuccessor = [];
    private bool _scheduleInitialized;

    private void ResolveSchedule()
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.Schedule);
        if (_scheduleSeeds.Count == 0) return;
        _executionOrder.Changed.Clear();
        foreach (int pass in _scheduleSeeds)
        {
            if (IsLive(pass))
            {
                if (!_scheduledMembers.Contains(pass))
                {
                    _executionOrder.Add(pass, _passes[pass].DeclarationOrdinal);
                    _scheduledMembers.Add(pass);
                    _frontierSeeds.Add(pass);
                }
            }
            else if (_scheduledMembers.Remove(pass))
            {
                _executionOrder.Remove(pass);
                if (pass < _sameQueueSuccessor.Length && _sameQueueSuccessor[pass] >= 0)
                    _frontierSeeds.Add(_sameQueueSuccessor[pass]);
                if (pass < _scheduledPosition.Length) _scheduledPosition[pass] = -1;
            }
        }
        _scheduleVisited.Clear();
        foreach (int seed in _scheduleSeeds)
        {
            if (!IsLive(seed) || !_scheduleVisited.Add(seed)) continue;
            _scheduleComponent.Clear();
            _scheduleComponent.Add(seed);
            _scheduleWork.Push(seed);
            if (!_scheduleInitialized)
                foreach (int pass in _scheduledMembers)
                    if (_scheduleVisited.Add(pass))
                    {
                        _scheduleComponent.Add(pass);
                        _scheduleWork.Push(pass);
                    }
            while (_scheduleWork.TryPop(out int pass))
            {
                foreach (int predecessor in _predecessors[pass]) Add(predecessor);
                foreach (int successor in _successors[pass]) Add(successor);
            }
            ScheduleComponent(_scheduleComponent);
            _executionOrder.SetOrder(_componentSchedule);

            void Add(int pass)
            {
                if (!IsLive(pass) || !_scheduleVisited.Add(pass)) return;
                _scheduleComponent.Add(pass);
                _scheduleWork.Push(pass);
            }
        }
        _scheduleInitialized = true;
        foreach (int pass in _executionOrder.Changed)
        {
            MarkPassResources(pass, _changedLifetimeResources);
            MarkPassResources(pass, _changedSynchronizationResources);
            if (_synchronizationByPass.TryGetValue(pass, out var resources))
                _changedSynchronizationResources.UnionWith(resources);
        }
        if (_executionOrder.Changed.Count != 0 || _recordingChanged)
        {
            PrepareArray(ref _schedule, _executionOrder.Count);
            EnsureCapacity(ref _scheduledPosition, _passes.Count);
            EnsureCapacity(ref _sameQueuePredecessor, _passes.Count);
            EnsureCapacity(ref _sameQueueSuccessor, _passes.Count);
            _queueLastPass.Clear();
            int position = 0;
            foreach (int pass in _executionOrder.Nodes)
            {
                Queue queue = _passes[pass].Queue!;
                int previous = _queueLastPass.GetValueOrDefault(queue, -1);
                if (_sameQueuePredecessor[pass] != previous) _frontierSeeds.Add(pass);
                _sameQueuePredecessor[pass] = previous;
                _sameQueueSuccessor[pass] = -1;
                if (previous >= 0) _sameQueueSuccessor[previous] = pass;
                _queueLastPass[queue] = pass;
                _scheduledPosition[pass] = position;
                CollectionsMarshal.AsSpan(_passes)[pass].ScheduledOrdinal = position;
                _schedule[position++] = pass;
            }
            _recordingChanged = true;
        }
        _scheduleSeeds.Clear();
    }

    private readonly HashSet<int> _scheduledMembers = [];

    private void ScheduleComponent(HashSet<int> component)
    {
        int[] criticalRanks = ResolveCriticalRanks(component);
        EnsureCapacity(ref _scheduleIndegrees, _passes.Count);
        EnsureCapacity(ref _componentFinish, _passes.Count);
        _scheduleReady.Clear();
        foreach (int pass in component)
        {
            if (!_live[pass]) continue;
            int count = 0;
            foreach (int predecessor in _predecessors[pass])
                if (_live[predecessor]) count++;
            _scheduleIndegrees[pass] = count;
            if (count == 0) _scheduleReady.Add(pass);
        }

        _componentSchedule.Clear();
        _queueTime.Clear();
        for (int position = 0; position < component.Count; position++)
        {
            int chosenReady = -1;
            int chosenPass = -1;
            Queue? chosenQueue = null;
            long chosenStart = long.MaxValue;
            int chosenRank = int.MinValue;
            long chosenDeclaration = long.MaxValue;

            for (int readyIndex = 0; readyIndex < _scheduleReady.Count; readyIndex++)
            {
                int pass = _scheduleReady[readyIndex];
                _frame.Graph.ResolveQueueCandidates(
                    _passes[pass].QueuePolicy,
                    _passes[pass].Kind,
                    _queueCandidateScratch);
                foreach (Queue queue in _queueCandidateScratch)
                {
                    _queueTime.TryGetValue(queue, out int queueAvailable);
                    int predecessorFinish = 0;
                    foreach (int predecessor in _predecessors[pass])
                    {
                        if (!_live[predecessor]) continue;
                        int finish = _componentFinish[predecessor];
                        if (finish > predecessorFinish) predecessorFinish = finish;
                    }
                    long start = Math.Max(queueAvailable, predecessorFinish);
                    int rank = criticalRanks[pass];
                    long declaration = _passes[pass].DeclarationOrdinal;
                    bool declarationMode =
                        (_frame.Options.Debug & RenderGraphDebugOptions.DeclarationOrderScheduling) != 0;
                    bool better = declarationMode
                        ? declaration < chosenDeclaration
                        : start < chosenStart ||
                          start == chosenStart && rank > chosenRank ||
                          start == chosenStart && rank == chosenRank && declaration < chosenDeclaration ||
                          start == chosenStart && rank == chosenRank && declaration == chosenDeclaration &&
                          QueueBefore(queue, chosenQueue);
                    if (!better) continue;
                    chosenReady = readyIndex;
                    chosenPass = pass;
                    chosenQueue = queue;
                    chosenStart = start;
                    chosenRank = rank;
                    chosenDeclaration = declaration;
                }
            }

            if (chosenPass < 0 || chosenQueue is null)
                throw new InvalidOperationException("RG9002: The Pass scheduler could not select a ready Pass.");
            _scheduleReady.RemoveAt(chosenReady);
            FramePass row = _passes[chosenPass];
            if (!ReferenceEquals(row.Queue, chosenQueue))
            {
                _frontierSeeds.Add(chosenPass);
                MarkPassResources(chosenPass, _changedSynchronizationResources);
                _recordingChanged = true;
            }
            row.Queue = chosenQueue;
            _passes[chosenPass] = row;
            _componentSchedule.Add(chosenPass);
            _queueTime[chosenQueue] = checked((int)chosenStart +
                checked((int)Math.Max(row.Options.EstimatedExecutionCost, 1)));

            _componentFinish[chosenPass] = _queueTime[chosenQueue];

            foreach (int successor in _successors[chosenPass])
            {
                if (!_live[successor]) continue;
                if (--_scheduleIndegrees[successor] == 0) _scheduleReady.Add(successor);
            }
        }
    }

    private int[] ResolveCriticalRanks(HashSet<int> component)
    {
        EnsureCapacity(ref _criticalRanks, _passes.Count);
        int liveCount = 0;
        EnsureCapacity(ref _scheduleIndegrees, _passes.Count);
        _criticalReady.Clear();
        foreach (int pass in component)
        {
            if (!_live[pass]) continue;
            liveCount++;
            int count = 0;
            foreach (int predecessor in _predecessors[pass])
                if (_live[predecessor]) count++;
            _scheduleIndegrees[pass] = count;
            if (count == 0) _criticalReady.Enqueue(pass, _passes[pass].DeclarationOrdinal);
        }
        PrepareArray(ref _criticalTopological, liveCount);
        int index = 0;
        while (_criticalReady.TryDequeue(out int pass, out _))
        {
            _criticalTopological[index++] = pass;
            foreach (int successor in _successors[pass])
                if (_live[successor] && --_scheduleIndegrees[successor] == 0)
                    _criticalReady.Enqueue(successor, _passes[successor].DeclarationOrdinal);
        }
        if (index != liveCount) throw new InvalidOperationException("RG5001: The live render graph contains a dependency cycle.");
        for (int i = liveCount - 1; i >= 0; i--)
        {
            int pass = _criticalTopological[i];
            int successorRank = 0;
            foreach (int successor in _successors[pass])
                if (_live[successor] && _criticalRanks[successor] > successorRank)
                    successorRank = _criticalRanks[successor];
            _criticalRanks[pass] = checked(
                (int)Math.Max(_passes[pass].Options.EstimatedExecutionCost, 1) + successorRank);
        }
        return _criticalRanks;
    }

    private static bool QueueBefore(Queue candidate, Queue? current)
    {
        if (current is null) return true;
        int type = candidate.Type.CompareTo(current.Type);
        return type < 0 || type == 0 && candidate.Index < current.Index;
    }

    private readonly HashSet<int> _frontierQueued = [];
    private int[] _frontierScratch = [];

    private void ResolveFrontiersAndLifetimes()
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.Frontiers);
        if (_queueLanes.Count == 0)
        {
            foreach (Queue queue in _frame.Graph.Queues) _queueLanes.Add(queue, _queueLanes.Count);
            _queueLaneCount = _queueLanes.Count;
        }
        EnsureCapacity(ref _startFrontiers, _passes.Count);
        EnsureCapacity(ref _endFrontiers, _passes.Count);
        EnsureCapacity(ref _frontierScratch, _queueLaneCount);
        _frontierWork.Clear();
        _frontierQueued.Clear();
        foreach (int pass in _frontierSeeds) Enqueue(pass);
        while (_frontierWork.TryDequeue(out int pass, out _))
        {
            _frontierQueued.Remove(pass);
            Array.Clear(_frontierScratch, 0, _queueLaneCount);
            int sameQueue = _sameQueuePredecessor[pass];
            if (sameQueue >= 0) Max(_frontierScratch, _endFrontiers[sameQueue]);
            foreach (int predecessor in _predecessors[pass])
                if (IsLive(predecessor)) Max(_frontierScratch, _endFrontiers[predecessor]);
            int[] start = PrepareFrontier(_startFrontiers[pass], _queueLaneCount);
            _startFrontiers[pass] = start;
            Array.Copy(_frontierScratch, start, _queueLaneCount);
            _frontierScratch[_queueLanes[_passes[pass].Queue!]]++;
            int[] end = PrepareFrontier(_endFrontiers[pass], _queueLaneCount);
            bool changed = !_frontierScratch.AsSpan(0, _queueLaneCount).SequenceEqual(end);
            _endFrontiers[pass] = end;
            if (!changed) continue;
            Array.Copy(_frontierScratch, end, _queueLaneCount);
            MarkPassResources(pass, _changedLifetimeResources);
            foreach (int successor in _successors[pass]) Enqueue(successor);
            if (_sameQueueSuccessor[pass] >= 0) Enqueue(_sameQueueSuccessor[pass]);
        }
        foreach (var key in _changedLifetimeResources)
        {
            if (!ResourceExists(key)) continue;
            _placementResources.Add(key);
            int first = -1, last = -1;
            List<int> accesses = CollectResourceAccesses(key.Kind, key.Index, enabledOnly: false);
            foreach (int index in accesses)
            {
                int pass = _accesses[index].PassIndex;
                if (!IsLive(pass)) continue;
                if (first < 0 || _scheduledPosition[pass] < _scheduledPosition[first]) first = pass;
                if (last < 0 || _scheduledPosition[pass] > _scheduledPosition[last]) last = pass;
            }
            if (key.Kind == GraphAccessTargetKind.Buffer)
            {
                ref FrameBuffer row = ref CollectionsMarshal.AsSpan(_buffers)[key.Index];
                row.FirstPass = first; row.LastPass = last;
            }
            else if (key.Kind == GraphAccessTargetKind.Texture)
            {
                ref FrameTexture row = ref CollectionsMarshal.AsSpan(_textures)[key.Index];
                row.FirstPass = first; row.LastPass = last;
            }
        }
        _frontierSeeds.Clear();
        _changedLifetimeResources.Clear();

        void Enqueue(int pass)
        {
            if (IsLive(pass) && _frontierQueued.Add(pass)) _frontierWork.Enqueue(pass, _scheduledPosition[pass]);
        }
    }

    private static void Max(int[] destination, int[] source)
    {
        for (int lane = 0; lane < source.Length; lane++)
            if (source[lane] > destination[lane]) destination[lane] = source[lane];
    }

    private static int[] PrepareFrontier(int[]? frontier, int laneCount)
    {
        if (frontier is null || frontier.Length != laneCount)
            frontier = laneCount == 0 ? Array.Empty<int>() : new int[laneCount];
        return frontier;
    }

    internal bool HappensBefore(int resourceA, bool textureA, int resourceB, bool textureB)
    {
        int lastA = textureA ? _textures[resourceA].LastPass : _buffers[resourceA].LastPass;
        int firstB = textureB ? _textures[resourceB].FirstPass : _buffers[resourceB].FirstPass;
        if (lastA < 0 || firstB < 0) return false;
        int passA = lastA;
        int passB = firstB;
        int[] end = _endFrontiers[passA];
        int[] start = _startFrontiers[passB];
        for (int lane = 0; lane < end.Length; lane++)
            if (end[lane] > start[lane]) return false;
        return true;
    }

    private readonly record struct PassEdge(int Predecessor, int Consumer);
    private readonly Dictionary<PassEdge, int> _orderEdgeSources = [];
    private readonly Dictionary<int, HashSet<PassEdge>> _orderEdgesByPass = [];
    private readonly HashSet<int> _changedValuePasses = [];
    private readonly HashSet<int> _changedOrderPasses = [];
    private readonly HashSet<GraphIdentity> _changedOrderIdentities = [];
    private readonly HashSet<int> _fixedValuePasses = [];
    private HashSet<PassEdge> _dynamicValueEdges = [];
    private HashSet<PassEdge> _nextDynamicValueEdges = [];

    private void SetPassRow(int index, in FramePass row)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.RowsSetPassRow);
        _passAccesses[index] ??= [];
        InvalidateRasterRendering(index);
        FramePass previous = _passes[index];
        bool identityChanged = previous.Identity != row.Identity &&
            (previous.Definition is not null || row.Definition is not null);
        // Retained callback values and enablement do not change shader bindings.
        // Immediate declarations can reuse mutable arrays, so reconnect those rows.
        if (row.Definition is null || previous.Definition is null || identityChanged ||
            !ReferenceEquals(previous.Pipeline, row.Pipeline) ||
            previous.ParameterLayout != row.ParameterLayout ||
            !ReferenceEquals(previous.ParameterBindings, row.ParameterBindings) ||
            !ReferenceEquals(previous.ParameterOrdinaryData, row.ParameterOrdinaryData))
            _parameterPassChanges.Add(index);
        if (identityChanged || previous.DeclarationOrdinal != row.DeclarationOrdinal)
            RemoveValuePass(index);
        if (!previous.Identity.IsValid || identityChanged || previous.DeclarationOrdinal != row.DeclarationOrdinal ||
            previous.Enabled != row.Enabled || previous.Options.Scheduling != row.Options.Scheduling)
            _changedValuePasses.Add(index);
        ObservePass(index, row);
        FramePass retained = row;
        retained.Queue = _passes[index].Queue;
        retained.ScheduledOrdinal = _passes[index].ScheduledOrdinal;
        _passes[index] = retained;
        if (row.Definition is not null)
            _rowChanges.Accesses.UnionWith(row.Definition.Accesses);
    }

    private void UpdateOrderEdges()
    {
        try
        {
            foreach (int index in _changedValuePasses)
            {
                if (index >= _passes.Count)
                {
                    RemoveValuePass(index);
                    continue;
                }
                FramePass pass = _passes[index];
                if (!_orderEdgesByPass.ContainsKey(index))
                {
                    _orderEdgesByPass.Add(index, []);
                }
                if (pass.Options.Scheduling == PassSchedulingMode.PreserveDeclarationPosition)
                    _fixedValuePasses.Add(index);
                else
                    _fixedValuePasses.Remove(index);
                _changedOrderIdentities.Add(pass.Identity);
            }

            GraphStructureIndex structure = _frame.StructureIndex;
            foreach (GraphIdentity identity in _changedOrderIdentities)
            {
                if (!TryResolvePass(identity, out int index) || !_orderEdgesByPass.ContainsKey(index)) continue;
                foreach (PassEdge edge in _orderEdgesByPass[index].ToArray())
                {
                    if ((_orderEdgeSources[edge] & 1) == 0) continue;
                    bool exists = structure.ExplicitEdges.Contains(new GraphDependencyEdge(
                        _passes[edge.Predecessor].Identity, _passes[edge.Consumer].Identity));
                    SetValueOrderEdge(edge, 1, exists);
                }
                foreach (GraphIdentity predecessor in structure.ExplicitPredecessorIdentities.GetValueOrDefault(identity, []))
                    SetValueOrderEdge(new(ResolvePass(predecessor), index), 1, true);
                foreach (GraphIdentity consumer in structure.ExplicitSuccessorIdentities.GetValueOrDefault(identity, []))
                    SetValueOrderEdge(new(index, ResolvePass(consumer)), 1, true);
            }

            _nextDynamicValueEdges.Clear();
            foreach (ExplicitPassOrder order in _frame.DynamicOrders)
                _nextDynamicValueEdges.Add(new(ResolvePass(order.Predecessor), ResolvePass(order.Consumer)));
            foreach (PassEdge edge in _dynamicValueEdges)
                if (!_nextDynamicValueEdges.Contains(edge)) SetValueOrderEdge(edge, 2, false);
            foreach (PassEdge edge in _nextDynamicValueEdges) SetValueOrderEdge(edge, 2, true);
            (_dynamicValueEdges, _nextDynamicValueEdges) = (_nextDynamicValueEdges, _dynamicValueEdges);

            foreach (int index in _changedValuePasses)
            {
                if (index >= _passes.Count) continue;
                foreach (PassEdge edge in _orderEdgesByPass[index].ToArray())
                {
                    if ((_orderEdgeSources[edge] & 4) != 0)
                        SetValueOrderEdge(edge, 4, (_fixedValuePasses.Contains(edge.Predecessor) ||
                            _fixedValuePasses.Contains(edge.Consumer)) &&
                            _passes[edge.Predecessor].DeclarationOrdinal < _passes[edge.Consumer].DeclarationOrdinal);
                }
                if (_fixedValuePasses.Contains(index))
                    for (int other = 0; other < _passes.Count; other++) AddFixedOrder(index, other);
                else
                    foreach (int other in _fixedValuePasses) AddFixedOrder(index, other);
            }
            InvalidateOrderedResources();
            _changedValuePasses.Clear();
            _changedOrderIdentities.Clear();
            _changedOrderPasses.Clear();
        }
        catch
        {
            _orderEdgeSources.Clear();
            _orderEdgesByPass.Clear();
            _fixedValuePasses.Clear();
            _dynamicValueEdges.Clear();
            for (int index = 0; index < _passes.Count; index++) _changedValuePasses.Add(index);
            throw;
        }
    }

    private void AddFixedOrder(int left, int right)
    {
        if (left == right) return;
        PassEdge edge = _passes[left].DeclarationOrdinal < _passes[right].DeclarationOrdinal
            ? new(left, right) : new(right, left);
        SetValueOrderEdge(edge, 4, true);
    }

    private void SetValueOrderEdge(PassEdge edge, int source, bool exists)
    {
        _orderEdgeSources.TryGetValue(edge, out int previous);
        int current = exists ? previous | source : previous & ~source;
        if (current == previous) return;
        _changedExecutionEdges.Add(edge);
        _changedOrderPasses.Add(edge.Predecessor);
        _changedOrderPasses.Add(edge.Consumer);
        if (current == 0)
        {
            _orderEdgeSources.Remove(edge);
            if (_orderEdgesByPass.TryGetValue(edge.Predecessor, out HashSet<PassEdge>? predecessors)) predecessors.Remove(edge);
            if (_orderEdgesByPass.TryGetValue(edge.Consumer, out HashSet<PassEdge>? consumers)) consumers.Remove(edge);
        }
        else
        {
            _orderEdgeSources[edge] = current;
            _orderEdgesByPass[edge.Predecessor].Add(edge);
            _orderEdgesByPass[edge.Consumer].Add(edge);
        }
    }

    private void RemoveValuePass(int index)
    {
        if (!_orderEdgesByPass.TryGetValue(index, out HashSet<PassEdge>? removedEdges)) return;
        _changedExecutionEdges.UnionWith(removedEdges);
        foreach (PassEdge edge in removedEdges.ToArray())
            SetValueOrderEdge(edge, 7, false);
        _orderEdgesByPass.Remove(index);
        _fixedValuePasses.Remove(index);
    }

    private void InvalidateOrderedResources()
    {
        _changedOrderPasses.UnionWith(_changedValuePasses);
        _orderSearchVisited.Clear();
        _orderSearchWork.Clear();
        foreach (int seed in _changedOrderPasses)
            if (seed < _passes.Count) _orderSearchWork.Push(seed);
        while (_orderSearchWork.TryPop(out int pass))
        {
            if (!_orderSearchVisited.Add(pass)) continue;
            MarkPassResources(pass, _changedAnalysisResources);
            _scheduleSeeds.Add(pass);
            if (!_orderEdgesByPass.TryGetValue(pass, out HashSet<PassEdge>? edges)) continue;
            foreach (PassEdge edge in edges)
            {
                _orderSearchWork.Push(edge.Predecessor);
                _orderSearchWork.Push(edge.Consumer);
            }
        }
    }
}
