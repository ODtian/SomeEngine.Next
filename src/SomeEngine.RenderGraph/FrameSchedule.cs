namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private readonly PassOrder _executionOrder = new();
    private readonly HashSet<int> _scheduleSeeds = [];
    private readonly HashSet<int> _frontierSeeds = [];
    private readonly HashSet<(GraphAccessTargetKind Kind, int Index)> _changedLifetimeResources = [];
    private readonly HashSet<int> _scheduleComponent = [];
    private readonly List<int> _componentSchedule = [];
    private readonly PriorityQueue<int, long> _frontierWork = new();
    private readonly Dictionary<Queue, SortedSet<(long Position, int Pass)>> _queuePasses = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, (Queue Queue, long Position)> _passQueuePositions = [];
    private readonly HashSet<int> _queueNeighborChanges = [];
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
                if (!_executionOrder.Contains(pass))
                {
                    _executionOrder.Add(pass, _passes[pass].DeclarationOrdinal);
                    _frontierSeeds.Add(pass);
                }
            }
            else if (_executionOrder.Remove(pass))
            {
                if (pass < _sameQueueSuccessor.Length && _sameQueueSuccessor[pass] >= 0)
                    _frontierSeeds.Add(_sameQueueSuccessor[pass]);
            }
        }
        if (!_scheduleInitialized)
        {
            _scheduleComponent.Clear();
            _scheduleComponent.UnionWith(_executionOrder.Nodes);
            ScheduleComponent(_scheduleComponent);
            _executionOrder.SetOrder(_componentSchedule);
        }
        else
        {
            long first = long.MaxValue;
            long last = long.MinValue;
            foreach (int seed in _scheduleSeeds)
            {
                if (!IsLive(seed)) continue;
                RetainQueue(seed);
                foreach (int predecessor in _predecessors[seed])
                    if (IsLive(predecessor)) IncludeConflict(predecessor, seed);
                foreach (int successor in _successors[seed])
                    if (IsLive(successor)) IncludeConflict(seed, successor);
            }
            if (first <= last) RepairOrder(first, last);

            void IncludeConflict(int predecessor, int consumer)
            {
                long before = _executionOrder.Position(predecessor);
                long after = _executionOrder.Position(consumer);
                if (before < after) return;
                first = Math.Min(first, after);
                last = Math.Max(last, before);
            }
        }
        _scheduleInitialized = true;
        foreach (int pass in _executionOrder.Changed)
        {
            MarkPassResources(pass, _changedLifetimeResources);
            ChangePassSynchronization(pass);
            if (_synchronizationByPass.TryGetValue(pass, out var resources))
                foreach (var resource in resources) ChangeSynchronization(resource);
        }
        if (_executionOrder.Changed.Count != 0)
        {
            EnsureCapacity(ref _sameQueuePredecessor, _passes.Count);
            EnsureCapacity(ref _sameQueueSuccessor, _passes.Count);
            _queueNeighborChanges.Clear();
            foreach (int pass in _executionOrder.Changed)
            {
                if (_passQueuePositions.Remove(pass, out var previous))
                {
                    SortedSet<(long Position, int Pass)> queuePasses = _queuePasses[previous.Queue];
                    AddNeighbors(queuePasses, previous.Position, pass);
                    queuePasses.Remove((previous.Position, pass));
                    if (queuePasses.Count == 0) _queuePasses.Remove(previous.Queue);
                }
                if (pass < _sameQueuePredecessor.Length)
                {
                    _sameQueuePredecessor[pass] = -1;
                    _sameQueueSuccessor[pass] = -1;
                }
            }
            foreach (int pass in _executionOrder.Changed)
            {
                if (!IsLive(pass)) continue;
                Queue queue = _passes[pass].Queue!;
                long position = _executionOrder.Position(pass);
                if (!_queuePasses.TryGetValue(queue, out var queuePasses))
                    _queuePasses.Add(queue, queuePasses = []);
                queuePasses.Add((position, pass));
                _passQueuePositions.Add(pass, (queue, position));
                _frontierSeeds.Add(pass);
                _queueNeighborChanges.Add(pass);
                AddNeighbors(queuePasses, position, pass);
            }
            foreach (int pass in _queueNeighborChanges)
            {
                if (!_passQueuePositions.TryGetValue(pass, out var current)) continue;
                var queuePasses = _queuePasses[current.Queue];
                int predecessor = Neighbor(queuePasses, current.Position, pass, before: true);
                if (_sameQueuePredecessor[pass] != predecessor) _frontierSeeds.Add(pass);
                _sameQueuePredecessor[pass] = predecessor;
                _sameQueueSuccessor[pass] = Neighbor(queuePasses, current.Position, pass, before: false);
            }
            foreach (int pass in _executionOrder.Changed) ChangeRecording(pass);
            foreach (int pass in _queueNeighborChanges) ChangeRecording(pass);
        }
        _scheduleSeeds.Clear();
    }

    private void RetainQueue(int pass)
    {
        FramePass row = _passes[pass];
        _frame.Graph.ResolveQueueCandidates(row.QueuePolicy, row.Kind, _queueCandidateScratch);
        if (row.Queue is not null && _queueCandidateScratch.Contains(row.Queue)) return;
        Queue? chosen = null;
        foreach (Queue candidate in _queueCandidateScratch)
            if (QueueBefore(candidate, chosen)) chosen = candidate;
        row.Queue = chosen ?? throw new InvalidOperationException("RG9002: No Queue satisfies the Pass declaration.");
        _passes[pass] = row;
        _executionOrder.Changed.Add(pass);
    }

    private void RepairOrder(long first, long last)
    {
        // A streaming update can reverse many edges in the same region. Sort the
        // affected interval once; unchanged positions outside it remain untouched.
        _scheduleComponent.Clear();
        foreach (int pass in _executionOrder.Nodes)
        {
            long position = _executionOrder.Position(pass);
            if (position > last) break;
            if (position >= first) _scheduleComponent.Add(pass);
        }
        EnsureCapacity(ref _scheduleIndegrees, _passes.Count);
        _criticalReady.Clear();
        foreach (int pass in _scheduleComponent)
        {
            int count = 0;
            foreach (int source in _predecessors[pass])
                if (_scheduleComponent.Contains(source)) count++;
            _scheduleIndegrees[pass] = count;
            if (count == 0) _criticalReady.Enqueue(pass, _executionOrder.Position(pass));
        }
        _componentSchedule.Clear();
        while (_criticalReady.TryDequeue(out int pass, out _))
        {
            _componentSchedule.Add(pass);
            foreach (int successor in _successors[pass])
                if (_scheduleComponent.Contains(successor) && --_scheduleIndegrees[successor] == 0)
                    _criticalReady.Enqueue(successor, _executionOrder.Position(successor));
        }
        if (_componentSchedule.Count != _scheduleComponent.Count)
            throw new InvalidOperationException("RG5001: The live render graph contains a dependency cycle.");
        _executionOrder.SetOrder(_componentSchedule);
    }

    private void AddNeighbors(SortedSet<(long Position, int Pass)> passes, long position, int pass)
    {
        int predecessor = Neighbor(passes, position, pass, before: true);
        int successor = Neighbor(passes, position, pass, before: false);
        if (predecessor >= 0) _queueNeighborChanges.Add(predecessor);
        if (successor >= 0) _queueNeighborChanges.Add(successor);
    }

    private static int Neighbor(SortedSet<(long Position, int Pass)> passes, long position, int pass, bool before)
    {
        IEnumerable<(long Position, int Pass)> candidates = before
            ? passes.GetViewBetween((long.MinValue, int.MinValue), (position, pass)).Reverse()
            : passes.GetViewBetween((position, pass), (long.MaxValue, int.MaxValue));
        foreach (var candidate in candidates)
            if (candidate.Pass != pass) return candidate.Pass;
        return -1;
    }

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
            int chosenTransfers = int.MaxValue;

            for (int readyIndex = 0; readyIndex < _scheduleReady.Count; readyIndex++)
            {
                int pass = _scheduleReady[readyIndex];
                _frame.Graph.ResolveQueueCandidates(
                    _passes[pass].QueuePolicy,
                    _passes[pass].Kind,
                    _queueCandidateScratch);
                int minimumTransfers = int.MaxValue;
                if (_passes[pass].QueuePolicy.AllowsAnyType)
                {
                    foreach (Queue candidate in _queueCandidateScratch)
                    {
                        int transfers = 0;
                        foreach (int predecessor in _predecessors[pass])
                            if (_live[predecessor] && !ReferenceEquals(_passes[predecessor].Queue, candidate)) transfers++;
                        minimumTransfers = Math.Min(minimumTransfers, transfers);
                    }
                }
                foreach (Queue queue in _queueCandidateScratch)
                {
                    _queueTime.TryGetValue(queue, out int queueAvailable);
                    int predecessorFinish = 0;
                    int transfers = 0;
                    foreach (int predecessor in _predecessors[pass])
                    {
                        if (!_live[predecessor]) continue;
                        int finish = _componentFinish[predecessor];
                        if (finish > predecessorFinish) predecessorFinish = finish;
                        if (!ReferenceEquals(_passes[predecessor].Queue, queue)) transfers++;
                    }
                    if (transfers > minimumTransfers) continue;
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
                          (transfers < chosenTransfers ||
                           transfers == chosenTransfers && QueueBefore(queue, chosenQueue));
                    if (!better) continue;
                    chosenReady = readyIndex;
                    chosenPass = pass;
                    chosenQueue = queue;
                    chosenStart = start;
                    chosenRank = rank;
                    chosenDeclaration = declaration;
                    chosenTransfers = transfers;
                }
            }

            if (chosenPass < 0 || chosenQueue is null)
                throw new InvalidOperationException("RG9002: The Pass scheduler could not select a ready Pass.");
            _scheduleReady.RemoveAt(chosenReady);
            FramePass row = _passes[chosenPass];
            if (!ReferenceEquals(row.Queue, chosenQueue))
            {
                _executionOrder.Changed.Add(chosenPass);
                _frontierSeeds.Add(chosenPass);
                ChangePassSynchronization(chosenPass);
                ChangeRecording(chosenPass);
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
        if (index != liveCount)
        {
            var path = new List<int>();
            var positions = new Dictionary<int, int>();
            int current = component.First(pass => _live[pass] && _scheduleIndegrees[pass] != 0);
            while (!positions.ContainsKey(current))
            {
                positions.Add(current, path.Count);
                path.Add(current);
                current = _predecessors[current].First(pass => _live[pass] && _scheduleIndegrees[pass] != 0);
            }
            string cycle = string.Join(" <- ", path.Skip(positions[current]).Append(current).Select(pass => _passes[pass].Label));
            throw new InvalidOperationException($"RG5001: The live render graph contains a dependency cycle: {cycle}.");
        }
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
                if (first < 0 || _executionOrder.Position(pass) < _executionOrder.Position(first)) first = pass;
                if (last < 0 || _executionOrder.Position(pass) > _executionOrder.Position(last)) last = pass;
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
            if (IsLive(pass) && _frontierQueued.Add(pass)) _frontierWork.Enqueue(pass, _executionOrder.Position(pass));
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
    private readonly Dictionary<PassEdge, bool> _orderDependencyChanges = [];
    private readonly HashSet<(GraphAccessTargetKind Kind, int Index)> _orderPredecessorResources = [];
    private readonly HashSet<(GraphAccessTargetKind Kind, int Index)> _orderConsumerResources = [];
    private readonly HashSet<GraphIdentity> _changedOrderIdentities = [];
    private readonly HashSet<int> _fixedValuePasses = [];
    private readonly Dictionary<ExplicitPassOrder, PassEdge> _dynamicValueEdges = [];
    private readonly Dictionary<ExplicitPassOrder, bool> _dynamicOrderChanges = [];
    private readonly Dictionary<GraphIdentity, HashSet<ExplicitPassOrder>> _dynamicOrdersByPass = [];

    internal void ChangeDynamicOrder(ExplicitPassOrder order, bool exists)
    {
        if (_dynamicValueEdges.ContainsKey(order) == exists)
            _dynamicOrderChanges.Remove(order);
        else
            _dynamicOrderChanges[order] = exists;
    }

    private void RefreshDynamicOrders(GraphIdentity identity)
    {
        if (!_dynamicOrdersByPass.TryGetValue(identity, out var orders)) return;
        foreach (ExplicitPassOrder order in orders)
            _dynamicOrderChanges.TryAdd(order, true);
    }

    private void SetPassRow(int index, in FramePass row)
    {
        _rebuiltRowCount++;
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.RowsSetPassRow);
        _passAccesses[index] ??= [];
        _parameterPassChanges.Add(index);
        InvalidateRasterRendering(index);
        FramePass previous = _passes[index];
        bool identityChanged = previous.Identity != row.Identity &&
            (previous.Definition is not null || row.Definition is not null);
        if (identityChanged || previous.DeclarationOrdinal != row.DeclarationOrdinal)
        {
            RefreshDynamicOrders(previous.Identity);
            RefreshDynamicOrders(row.Identity);
            RemoveValuePass(index);
        }
        if (!previous.Identity.IsValid || identityChanged || previous.DeclarationOrdinal != row.DeclarationOrdinal ||
            previous.Enabled != row.Enabled || previous.Options.Scheduling != row.Options.Scheduling)
            _changedValuePasses.Add(index);
        ObservePass(index, row);
        FramePass retained = row;
        retained.Queue = _passes[index].Queue;
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

            foreach (var change in _dynamicOrderChanges)
            {
                if (!_dynamicValueEdges.Remove(change.Key, out PassEdge previous)) continue;
                SetValueOrderEdge(previous, 2, false);
                RemoveReference(change.Key.Predecessor, change.Key);
                RemoveReference(change.Key.Consumer, change.Key);
            }
            foreach (var change in _dynamicOrderChanges)
            {
                if (!change.Value) continue;
                ExplicitPassOrder order = change.Key;
                var edge = new PassEdge(ResolvePass(order.Predecessor), ResolvePass(order.Consumer));
                _dynamicValueEdges.Add(order, edge);
                AddReference(order.Predecessor, order);
                AddReference(order.Consumer, order);
                SetValueOrderEdge(edge, 2, true);
            }
            _dynamicOrderChanges.Clear();

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
        }
        catch
        {
            _orderEdgeSources.Clear();
            _orderEdgesByPass.Clear();
            _fixedValuePasses.Clear();
            _dynamicValueEdges.Clear();
            _dynamicOrdersByPass.Clear();
            _dynamicOrderChanges.Clear();
            foreach (ExplicitPassOrder order in _frame.DynamicOrders)
                _dynamicOrderChanges[order] = true;
            for (int index = 0; index < _passes.Count; index++) _changedValuePasses.Add(index);
            throw;
        }

        void AddReference(GraphIdentity identity, ExplicitPassOrder order)
        {
            if (!_dynamicOrdersByPass.TryGetValue(identity, out var orders))
                _dynamicOrdersByPass.Add(identity, orders = []);
            orders.Add(order);
        }

        void RemoveReference(GraphIdentity identity, ExplicitPassOrder order)
        {
            if (!_dynamicOrdersByPass.TryGetValue(identity, out var orders)) return;
            orders.Remove(order);
            if (orders.Count == 0) _dynamicOrdersByPass.Remove(identity);
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
        if (previous == 0 || current == 0)
            _orderDependencyChanges.TryAdd(edge, previous != 0);
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
        foreach (var change in _orderDependencyChanges)
        {
            if (_orderEdgeSources.ContainsKey(change.Key) == change.Value) continue;
            _scheduleSeeds.Add(change.Key.Predecessor);
            _scheduleSeeds.Add(change.Key.Consumer);
        }
        foreach (int pass in _changedValuePasses)
            MarkPassResources(pass, _changedAnalysisResources);

        // Traverse each side of the entire edit once. Repeating both transitive walks
        // for every changed edge made streaming updates multiply graph size by edit size.
        // The intersection may include a resource spanning separate edits; reanalyzing
        // that resource is safe and keeps the affected-region discovery linear.
        Collect(before: true, _orderPredecessorResources);
        Collect(before: false, _orderConsumerResources);
        foreach (var resource in _orderPredecessorResources)
            if (_orderConsumerResources.Contains(resource)) _changedAnalysisResources.Add(resource);
        _orderDependencyChanges.Clear();

        void Collect(bool before, HashSet<(GraphAccessTargetKind Kind, int Index)> resources)
        {
            resources.Clear();
            _orderSearchVisited.Clear();
            _orderSearchWork.Clear();
            foreach (var change in _orderDependencyChanges)
                if (_orderEdgeSources.ContainsKey(change.Key) != change.Value)
                    _orderSearchWork.Push(before ? change.Key.Predecessor : change.Key.Consumer);
            foreach (int pass in _changedValuePasses) _orderSearchWork.Push(pass);
            while (_orderSearchWork.TryPop(out int pass))
            {
                if ((uint)pass >= (uint)_passes.Count || !_orderSearchVisited.Add(pass)) continue;
                MarkPassResources(pass, resources);
                if (!_orderEdgesByPass.TryGetValue(pass, out var edges)) continue;
                foreach (PassEdge edge in edges)
                {
                    int next = before ? edge.Predecessor : edge.Consumer;
                    if ((before ? edge.Consumer : edge.Predecessor) == pass &&
                        (uint)next < (uint)_passes.Count && _passes[next].Enabled)
                        _orderSearchWork.Push(next);
                }
            }
        }
    }
}
