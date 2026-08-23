namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private void ResolveSchedule()
    {
        int liveCount = 0;
        for (int pass = 0; pass < _passes.Length; pass++)
            if (_live[pass]) liveCount++;
        PrepareArray(ref _schedule, liveCount);
        PrepareArray(ref _scheduledPosition, _passes.Length);
        Array.Fill(_scheduledPosition, -1);
        PrepareArray(ref _sameQueuePredecessor, _passes.Length);
        Array.Fill(_sameQueuePredecessor, -1);
        if (liveCount == 0) return;

        int[] criticalRanks = ResolveCriticalRanks();
        PrepareArray(ref _scheduleIndegrees, _passes.Length);
        Array.Clear(_scheduleIndegrees);
        _scheduleReady.Clear();
        for (int pass = 0; pass < _passes.Length; pass++)
        {
            if (!_live[pass]) continue;
            int count = 0;
            foreach (int predecessor in _predecessors[pass])
                if (_live[predecessor]) count++;
            _scheduleIndegrees[pass] = count;
            if (count == 0) _scheduleReady.Add(pass);
        }

        _queueTime.Clear();
        _queueLastPass.Clear();
        for (int position = 0; position < liveCount; position++)
        {
            int chosenReady = -1;
            int chosenPass = -1;
            Queue? chosenQueue = null;
            long chosenStart = long.MaxValue;
            int chosenRank = int.MinValue;
            int chosenDeclaration = int.MaxValue;

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
                        int finish = _scheduledPosition[predecessor] < 0
                            ? 0
                            : _scheduledPosition[predecessor] +
                              checked((int)Math.Max(_passes[predecessor].Options.EstimatedExecutionCost, 1));
                        if (finish > predecessorFinish) predecessorFinish = finish;
                    }
                    long start = Math.Max(queueAvailable, predecessorFinish);
                    int rank = criticalRanks[pass];
                    int declaration = _passes[pass].DeclarationOrdinal;
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
            row.Queue = chosenQueue;
            row.ScheduledOrdinal = position;
            _passes[chosenPass] = row;
            _schedule[position] = chosenPass;
            _scheduledPosition[chosenPass] = position;
            if (_queueLastPass.TryGetValue(chosenQueue, out int previous))
                _sameQueuePredecessor[chosenPass] = previous;
            _queueLastPass[chosenQueue] = chosenPass;
            _queueTime[chosenQueue] = checked((int)chosenStart +
                checked((int)Math.Max(row.Options.EstimatedExecutionCost, 1)));

            foreach (int successor in _successors[chosenPass])
            {
                if (!_live[successor]) continue;
                if (--_scheduleIndegrees[successor] == 0) _scheduleReady.Add(successor);
            }
        }
    }

    private int[] ResolveCriticalRanks()
    {
        PrepareArray(ref _criticalRanks, _passes.Length);
        Array.Clear(_criticalRanks);
        int liveCount = 0;
        PrepareArray(ref _scheduleIndegrees, _passes.Length);
        Array.Clear(_scheduleIndegrees);
        _criticalReady.Clear();
        for (int pass = 0; pass < _passes.Length; pass++)
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

    private void ResolveFrontiersAndLifetimes()
    {
        _queueLanes.Clear();
        foreach (int pass in _schedule)
        {
            Queue queue = _passes[pass].Queue!;
            if (!_queueLanes.ContainsKey(queue))
                _queueLanes.Add(queue, _queueLanes.Count);
        }
        _queueLaneCount = _queueLanes.Count;
        PrepareArray(ref _startFrontiers, _passes.Length);
        PrepareArray(ref _endFrontiers, _passes.Length);

        foreach (int pass in _schedule)
        {
            int[] start = PrepareFrontier(_startFrontiers[pass], _queueLaneCount);
            _startFrontiers[pass] = start;
            Array.Clear(start);
            int sameQueue = _sameQueuePredecessor[pass];
            if (sameQueue >= 0) Max(start, _endFrontiers[sameQueue]);
            foreach (int predecessor in _predecessors[pass])
                if (_live[predecessor]) Max(start, _endFrontiers[predecessor]);
            int[] end = PrepareFrontier(_endFrontiers[pass], _queueLaneCount);
            _endFrontiers[pass] = end;
            Array.Copy(start, end, _queueLaneCount);
            end[_queueLanes[_passes[pass].Queue!]]++;
        }

        for (int accessIndex = 0; accessIndex < _accesses.Length; accessIndex++)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            if (!_live[access.PassIndex]) continue;
            int position = _scheduledPosition[access.PassIndex];
            if (access.TargetKind == GraphAccessTargetKind.Buffer)
            {
                FrameBuffer row = _buffers[access.ResourceIndex];
                if (position < row.FirstUse) row.FirstUse = position;
                if (position > row.LastUse) row.LastUse = position;
                _buffers[access.ResourceIndex] = row;
            }
            else if (access.TargetKind == GraphAccessTargetKind.Texture)
            {
                FrameTexture row = _textures[access.ResourceIndex];
                if (position < row.FirstUse) row.FirstUse = position;
                if (position > row.LastUse) row.LastUse = position;
                _textures[access.ResourceIndex] = row;
            }
        }
    }

    private static void Max(int[] destination, int[] source)
    {
        for (int lane = 0; lane < destination.Length; lane++)
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
        int lastA = textureA ? _textures[resourceA].LastUse : _buffers[resourceA].LastUse;
        int firstB = textureB ? _textures[resourceB].FirstUse : _buffers[resourceB].FirstUse;
        if (lastA < 0 || firstB == int.MaxValue) return false;
        int passA = _schedule[lastA];
        int passB = _schedule[firstB];
        int[] end = _endFrontiers[passA];
        int[] start = _startFrontiers[passB];
        for (int lane = 0; lane < end.Length; lane++)
            if (end[lane] > start[lane]) return false;
        return true;
    }
}

