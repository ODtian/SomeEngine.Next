namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private bool _submissionsInitialized;
    private readonly List<SwapchainSubmissionInterval> _swapchainSubmissionIntervals = [];
    private readonly List<int> _swapchainMergeSubmissions = [];

    private readonly record struct SwapchainSubmissionInterval(
        Queue Queue, int QueueSlot, int FirstPass, int LastPass, long FirstPosition, long LastPosition);
    private Queue?[] _submissionQueues = [];
    private readonly HashSet<int> _submissionUpdates = [];
    private readonly HashSet<int> _submissionMergeCandidates = [];
    private readonly HashSet<int> _submissionCycleSeeds = [];
    private readonly HashSet<int> _submissionWaveChanges = [];
    private readonly HashSet<int> _emptySubmissions = [];
    private readonly HashSet<int> _dependencySet = [];
    private readonly List<int> _submissionCycle = [];
    private readonly List<int> _submissionPath = [];
    private readonly List<int> _submissionPathOffsets = [];
    private readonly Dictionary<int, int> _submissionPathPositions = [];
    private readonly HashSet<int> _submissionVisited = [];
    private readonly System.Collections.Generic.Queue<int> _submissionWaveWork = new();

    private int BuildWaves()
    {
        bool initializedNow = false;
        if (!_submissionsInitialized)
        {
            int count = BuildInitialWaves();
            EnsureCapacity(ref _submissionQueues, _submissionCount);
            foreach (int submission in _changedSubmissions)
                _submissionQueues[submission] = _passes[_submissionPasses[submission][0]].Queue;
            _recordingSubmissionChanges.UnionWith(_changedSubmissions);
            _submissionsInitialized = true;
            if (!MergeSwapchainSubmissionIntervals()) return count;
            initializedNow = true;
        }

        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.BatchConstruction);
        if (!initializedNow)
        {
            int previousCapacity = _submissionByPass.Length;
            EnsureCapacity(ref _submissionByPass, _passes.Count);
            Array.Fill(_submissionByPass, -1, previousCapacity, _submissionByPass.Length - previousCapacity);
            _changedSubmissions.Clear();
            _submissionPassChanges.Clear();
            foreach (int pass in _changedRecordingPasses)
            {
                int submission = (uint)pass < (uint)_submissionByPass.Length ? _submissionByPass[pass] : -1;
                if (submission >= 0)
                {
                    _submissionUpdates.Add(submission);
                    if (!IsLive(pass) || !ReferenceEquals(_submissionQueues[submission], _passes[pass].Queue))
                    {
                        ChangeSubmissionMembers(submission);
                        _submissionPasses[submission].Remove(pass);
                        _submissionByPass[pass] = -1;
                        QueueSubmissionConsumers(submission);
                    }
                }
                if (IsLive(pass)) _submissionPassChanges.Add((_executionOrder.Position(pass), pass));
            }
            foreach (var item in _submissionPassChanges)
            {
                int pass = item.Pass;
                if (_submissionByPass[pass] >= 0) continue;
                int predecessor = _sameQueuePredecessor[pass];
                int submission = predecessor >= 0 ? _submissionByPass[predecessor] : -1;
                if (submission < 0 || _passes[predecessor].Options.SubmitAfter ||
                    !ReferenceEquals(_submissionQueues[submission], _passes[pass].Queue))
                    submission = NewSubmission(_passes[pass].Queue!);
                ChangeSubmissionMembers(submission);
                _submissionPasses[submission].Add(pass);
                _submissionByPass[pass] = submission;
                QueueSubmissionConsumers(submission);
                foreach (int consumer in _successors[pass]) QueuePassSubmission(consumer);
                QueuePassSubmission(_sameQueueSuccessor[pass]);
            }
            MergeSwapchainSubmissionIntervals();
        }

        while (true)
        {
            while (_submissionUpdates.Count != 0)
            {
                int submission = _submissionUpdates.First();
                _submissionUpdates.Remove(submission);
                UpdateSubmissionDependencies(submission);
            }
            bool split = false;
            while (_submissionCycleSeeds.Count != 0)
            {
                int submission = _submissionCycleSeeds.First();
                _submissionCycleSeeds.Remove(submission);
                if (!FindSubmissionCycle(submission)) continue;
                SplitSubmissionCycle();
                split = true;
                break;
            }
            if (split) continue;
            if (!MergeAdjacentSubmissions()) break;
        }

        foreach (int submission in _changedSubmissions)
        {
            foreach (int pass in _submissionPasses[submission]) MarkPassResources(pass, _stateConnectionChanges);
            int wave = _submissionWaves[submission];
            if (wave >= 0) _changedRecordingWaves.Add(wave);
        }
        UpdateSubmissionWaves();
        foreach (int submission in _emptySubmissions) _freeSubmissions.Push(submission);
        _emptySubmissions.Clear();
        int waveCount = _submissionsByWave.Count == 0 ? 0 : _submissionsByWave.Last().Key + 1;
        EnsureCapacity(ref _waves, waveCount);
        foreach (int wave in _changedRecordingWaves)
        {
            if (wave >= _waves.Length) continue;
            List<int> passes = _waves[wave] ??= [];
            passes.Clear();
            if (_submissionsByWave.TryGetValue(wave, out var members))
                foreach (int submission in members) passes.AddRange(_submissionPasses[submission]);
            passes.Sort((left, right) => _executionOrder.Position(left).CompareTo(_executionOrder.Position(right)));
        }
        _changedRecordingPasses.Clear();
        return waveCount;
    }

    // The current acquisition list is frame-local: a new image constraint can arrive
    // without changing the texture or its boundary states. Check before reusing batches.
    private bool PrepareSwapchainSubmissionIntervals()
    {
        _swapchainSubmissionIntervals.Clear();
        foreach ((GraphIdentity identity, _, Queue presentQueue) in _frame.SwapchainImages)
        {
            int texture = ResolveTexture(identity);
            FrameTexture row = _textures[texture];
            if (row.FirstPass < 0) continue;
            if (TextureEntryStates(row).IsEmpty)
                throw new InvalidOperationException("A swapchain image requires explicit entry boundary states.");
            if (_resourceAccessLists.TryGetValue((GraphAccessTargetKind.Texture, texture), out List<int>? accesses))
                foreach (int access in accesses)
                {
                    int pass = _accesses[access].PassIndex;
                    if (IsLive(pass) && !ReferenceEquals(_passes[pass].Queue, presentQueue))
                        throw new InvalidOperationException("All swapchain image uses must execute on its presentation Queue.");
                }
            _swapchainSubmissionIntervals.Add(new(presentQueue, QueueSlot(presentQueue), row.FirstPass, row.LastPass,
                _executionOrder.Position(row.FirstPass), _executionOrder.Position(row.LastPass)));
        }
        if (_swapchainSubmissionIntervals.Count > 1)
            _swapchainSubmissionIntervals.Sort(static (left, right) =>
            {
                int queue = left.QueueSlot.CompareTo(right.QueueSlot);
                return queue != 0 ? queue : left.FirstPosition.CompareTo(right.FirstPosition);
            });
        int retained = 0;
        for (int index = 0; index < _swapchainSubmissionIntervals.Count; index++)
        {
            SwapchainSubmissionInterval interval = _swapchainSubmissionIntervals[index];
            if (retained != 0)
            {
                SwapchainSubmissionInterval previous = _swapchainSubmissionIntervals[retained - 1];
                if (ReferenceEquals(previous.Queue, interval.Queue) && interval.FirstPosition <= previous.LastPosition)
                {
                    if (interval.LastPosition > previous.LastPosition)
                        _swapchainSubmissionIntervals[retained - 1] = previous with
                        { LastPass = interval.LastPass, LastPosition = interval.LastPosition };
                    continue;
                }
            }
            _swapchainSubmissionIntervals[retained++] = interval;
        }
        if (retained < _swapchainSubmissionIntervals.Count)
            _swapchainSubmissionIntervals.RemoveRange(retained, _swapchainSubmissionIntervals.Count - retained);

        bool intact = true;
        foreach (SwapchainSubmissionInterval interval in _swapchainSubmissionIntervals)
        {
            int submission = (uint)interval.FirstPass < (uint)_submissionByPass.Length
                ? _submissionByPass[interval.FirstPass] : -1;
            for (int pass = interval.FirstPass; ; pass = _sameQueueSuccessor[pass])
            {
                if (pass < 0 || !ReferenceEquals(_passes[pass].Queue, interval.Queue) ||
                    _executionOrder.Position(pass) > interval.LastPosition)
                    throw new InvalidOperationException("The swapchain image uses do not form a presentation-Queue interval.");
                if ((uint)pass >= (uint)_submissionByPass.Length || submission < 0 || _submissionByPass[pass] != submission)
                    intact = false;
                if (pass == interval.LastPass) break;
                if (_passes[pass].Options.SubmitAfter)
                    throw new InvalidOperationException("SubmitAfter cannot split a swapchain image's single-submit interval.");
            }
        }
        return intact;
    }

    private void ValidateSwapchainSubmissionIntervals()
    {
        foreach (SwapchainSubmissionInterval interval in _swapchainSubmissionIntervals)
        {
            int submission = _submissionByPass[interval.FirstPass];
            for (int pass = interval.FirstPass; ; pass = _sameQueueSuccessor[pass])
            {
                if (submission < 0 || _submissionByPass[pass] != submission)
                    throw new InvalidOperationException("A swapchain image still spans multiple submissions after grouping.");
                if (pass == interval.LastPass) break;
            }
        }
    }

    // Merge only images that currently span batches. The existing dependency solver
    // may then split non-image prefixes/suffixes or other queues, but never an image.
    private bool MergeSwapchainSubmissionIntervals()
    {
        bool changed = false;
        foreach (SwapchainSubmissionInterval interval in _swapchainSubmissionIntervals)
        {
            _swapchainMergeSubmissions.Clear();
            for (int pass = interval.FirstPass; ; pass = _sameQueueSuccessor[pass])
            {
                int submission = _submissionByPass[pass];
                if (submission < 0)
                    throw new InvalidOperationException("A live swapchain image use has no submission.");
                if (!_swapchainMergeSubmissions.Contains(submission)) _swapchainMergeSubmissions.Add(submission);
                if (pass == interval.LastPass) break;
            }
            if (_swapchainMergeSubmissions.Count < 2) continue;
            int target = _swapchainMergeSubmissions[0];
            for (int index = 1; index < _swapchainMergeSubmissions.Count; index++)
                MergeSubmissionMembers(target, _swapchainMergeSubmissions[index]);
            changed = true;
        }
        return changed;
    }

    private void MergeSubmissionMembers(int target, int source)
    {
        ChangeSubmissionMembers(target);
        ChangeSubmissionMembers(source);
        QueueSubmissionConsumers(target);
        QueueSubmissionConsumers(source);
        foreach (int pass in _submissionPasses[source])
        {
            _submissionPasses[target].Add(pass);
            _submissionByPass[pass] = target;
            foreach (int consumer in _successors[pass]) QueuePassSubmission(consumer);
            QueuePassSubmission(_sameQueueSuccessor[pass]);
        }
        _submissionPasses[source].Clear();
    }

    private int SwapchainSubmissionCut(Queue queue, int entry)
    {
        long position = _executionOrder.Position(entry);
        foreach (SwapchainSubmissionInterval interval in _swapchainSubmissionIntervals)
            if (ReferenceEquals(queue, interval.Queue) &&
                interval.FirstPosition < position && position <= interval.LastPosition)
                return interval.FirstPass;
        return entry;
    }

    private int NewSubmission(Queue queue)
    {
        int submission = CreateSubmission();
        EnsureCapacity(ref _submissionQueues, _submissionCount);
        _submissionQueues[submission] = queue;
        _recordingSubmissionChanges.Add(submission);
        _submissionUpdates.Add(submission);
        _submissionWaveChanges.Add(submission);
        return submission;
    }

    private void ChangeSubmissionMembers(int submission)
    {
        _recordingSubmissionChanges.Add(submission);
        if (_changedSubmissions.Add(submission))
            foreach (int pass in _submissionPasses[submission]) MarkPassResources(pass, _stateConnectionChanges);
        _submissionUpdates.Add(submission);
    }

    private void QueuePassSubmission(int pass)
    {
        if (pass >= 0 && IsLive(pass) && _submissionByPass[pass] >= 0)
            _submissionUpdates.Add(_submissionByPass[pass]);
    }

    private void QueueSubmissionConsumers(int submission)
    {
        foreach (int consumer in _submissionConsumers[submission]) _submissionUpdates.Add(consumer);
    }

    private void UpdateSubmissionDependencies(int submission)
    {
        List<int> passes = _submissionPasses[submission];
        if (passes.Count == 0)
        {
            if (!_emptySubmissions.Add(submission)) return;
            QueueSubmissionConsumers(submission);
            foreach (int dependency in _submissionDependencies[submission])
                _submissionConsumers[dependency].Remove(submission);
            _submissionDependencies[submission].Clear();
            _submissionConsumers[submission].Clear();
            RemoveSubmissionWave(submission);
            _submissionQueues[submission] = null;
            return;
        }
        for (int index = 1; index < passes.Count; index++)
        {
            if (_executionOrder.Position(passes[index - 1]) < _executionOrder.Position(passes[index])) continue;
            ChangeSubmissionMembers(submission);
            passes.Sort((left, right) => _executionOrder.Position(left).CompareTo(_executionOrder.Position(right)));
            break;
        }
        for (int index = 0; index + 1 < passes.Count; index++)
        {
            if (!_passes[passes[index]].Options.SubmitAfter) continue;
            SplitSubmission(submission, index + 1);
            return;
        }
        if (SplitNewSubmissionWait(submission)) return;
        _submissionMergeCandidates.Add(submission);
        int nextPass = _sameQueueSuccessor[passes[^1]];
        if (nextPass >= 0 && _submissionByPass[nextPass] >= 0)
            _submissionMergeCandidates.Add(_submissionByPass[nextPass]);
        _dependencySet.Clear();
        foreach (int pass in passes)
        {
            foreach (int predecessor in _predecessors[pass]) Add(predecessor);
            foreach (int predecessor in _physicalPredecessors[pass]) Add(predecessor);
            Add(_sameQueuePredecessor[pass]);
        }
        List<int> dependencies = _submissionDependencies[submission];
        bool changed = false;
        for (int index = dependencies.Count - 1; index >= 0; index--)
        {
            int dependency = dependencies[index];
            if (_dependencySet.Contains(dependency)) continue;
            dependencies.RemoveAt(index);
            _submissionConsumers[dependency].Remove(submission);
            changed = true;
        }
        foreach (int dependency in _dependencySet)
        {
            if (dependencies.Contains(dependency)) continue;
            dependencies.Add(dependency);
            _submissionConsumers[dependency].Add(submission);
            _submissionCycleSeeds.Add(submission);
            changed = true;
        }
        if (changed) _submissionWaveChanges.Add(submission);

        void Add(int pass)
        {
            if (pass < 0 || !IsLive(pass)) return;
            int dependency = _submissionByPass[pass];
            if (dependency >= 0 && dependency != submission) _dependencySet.Add(dependency);
        }
    }

    private bool SplitNewSubmissionWait(int submission)
    {
        List<int> passes = _submissionPasses[submission];
        Queue queue = _submissionQueues[submission]!;
        _dependencySet.Clear();
        for (int index = 0; index < passes.Count; index++)
        {
            int pass = passes[index];
            bool newWait = false;
            foreach (int predecessor in _predecessors[pass])
                if (NeedsWait(predecessor)) { newWait = true; break; }
            if (!newWait)
                foreach (int predecessor in _physicalPredecessors[pass])
                    if (NeedsWait(predecessor)) { newWait = true; break; }
            if (index != 0 && newWait)
            {
                int first = passes.IndexOf(SwapchainSubmissionCut(queue, pass));
                if (first > 0)
                {
                    SplitSubmission(submission, first);
                    return true;
                }
            }
            foreach (int predecessor in _predecessors[pass]) Add(predecessor);
            foreach (int predecessor in _physicalPredecessors[pass]) Add(predecessor);
            Add(_sameQueuePredecessor[pass]);
        }
        return false;

        bool NeedsWait(int pass)
        {
            if (pass < 0 || !IsLive(pass)) return false;
            int dependency = _submissionByPass[pass];
            if (dependency < 0 || dependency == submission || ReferenceEquals(_submissionQueues[dependency], queue))
                return false;
            foreach (int covered in _dependencySet)
                if (SubmissionDependsOn(covered, dependency, _submissionCount)) return false;
            return true;
        }

        void Add(int pass)
        {
            if (pass < 0 || !IsLive(pass)) return;
            int dependency = _submissionByPass[pass];
            if (dependency >= 0 && dependency != submission) _dependencySet.Add(dependency);
        }
    }

    private int SplitSubmission(int submission, int first)
    {
        List<int> passes = _submissionPasses[submission];
        int suffix = NewSubmission(_submissionQueues[submission]!);
        ChangeSubmissionMembers(submission);
        QueueSubmissionConsumers(submission);
        for (int item = first; item < passes.Count; item++)
        {
            int pass = passes[item];
            _submissionPasses[suffix].Add(pass);
            _submissionByPass[pass] = suffix;
            foreach (int next in _successors[pass]) QueuePassSubmission(next);
            QueuePassSubmission(_sameQueueSuccessor[pass]);
        }
        passes.RemoveRange(first, passes.Count - first);
        return suffix;
    }

    private bool MergeAdjacentSubmissions()
    {
        while (_submissionMergeCandidates.Count != 0)
        {
            int later = _submissionMergeCandidates.First();
            _submissionMergeCandidates.Remove(later);
            List<int> laterPasses = _submissionPasses[later];
            if (laterPasses.Count == 0) continue;
            int previousPass = _sameQueuePredecessor[laterPasses[0]];
            if (previousPass < 0 || _passes[previousPass].Options.SubmitAfter) continue;
            int earlier = _submissionByPass[previousPass];
            if (earlier < 0 || earlier == later ||
                !ReferenceEquals(_submissionQueues[earlier], _submissionQueues[later])) continue;
            List<int> earlierPasses = _submissionPasses[earlier];
            if (earlierPasses.Count == 0 || earlierPasses[^1] != previousPass) continue;

            bool compatible = true;
            // The earlier work must not acquire a wait it did not already have.
            foreach (int dependency in _submissionDependencies[later])
                if (dependency != earlier && !SubmissionDependsOn(earlier, dependency, _submissionCount))
                {
                    compatible = false;
                    break;
                }
            if (!compatible) continue;
            // A consumer that could start after the earlier batch must already
            // require the later batch, so merging cannot postpone its release.
            foreach (int consumer in _submissionConsumers[earlier])
                if (consumer != later && !SubmissionDependsOn(consumer, later, _submissionCount))
                {
                    compatible = false;
                    break;
                }
            if (!compatible) continue;

            MergeSubmissionMembers(earlier, later);
            return true;
        }
        return false;
    }

    private bool FindSubmissionCycle(int start)
    {
        _submissionCycle.Clear();
        _submissionPath.Clear();
        _submissionPathOffsets.Clear();
        _submissionPathPositions.Clear();
        _submissionVisited.Clear();
        Push(start);
        while (_submissionPath.Count != 0)
        {
            int last = _submissionPath.Count - 1;
            int submission = _submissionPath[last];
            int offset = _submissionPathOffsets[last];
            List<int> dependencies = _submissionDependencies[submission];
            if (offset == dependencies.Count)
            {
                _submissionPath.RemoveAt(last);
                _submissionPathOffsets.RemoveAt(last);
                _submissionPathPositions.Remove(submission);
                _submissionVisited.Add(submission);
                continue;
            }
            int dependency = dependencies[offset];
            _submissionPathOffsets[last] = offset + 1;
            if (_submissionPathPositions.TryGetValue(dependency, out int first))
            {
                for (int index = first; index < _submissionPath.Count; index++)
                    _submissionCycle.Add(_submissionPath[index]);
                return true;
            }
            if (!_submissionVisited.Contains(dependency)) Push(dependency);
        }
        return false;

        void Push(int submission)
        {
            _submissionPathPositions.Add(submission, _submissionPath.Count);
            _submissionPath.Add(submission);
            _submissionPathOffsets.Add(0);
        }
    }

    private void SplitSubmissionCycle()
    {
        for (int index = 0; index < _submissionCycle.Count; index++)
        {
            int submission = _submissionCycle[index];
            int dependency = _submissionCycle[(index + 1) % _submissionCycle.Count];
            int consumer = _submissionCycle[(index + _submissionCycle.Count - 1) % _submissionCycle.Count];
            FindSubmissionEdge(dependency, submission, out _, out int entry);
            FindSubmissionEdge(submission, consumer, out int exit, out _);
            if (_swapchainSubmissionIntervals.Count != 0)
                entry = SwapchainSubmissionCut(_submissionQueues[submission]!, entry);
            if (_executionOrder.Position(exit) >= _executionOrder.Position(entry)) continue;
            List<int> passes = _submissionPasses[submission];
            int first = passes.IndexOf(entry);
            if (first <= 0) continue;
            int suffix = SplitSubmission(submission, first);
            _submissionCycleSeeds.Add(submission);
            _submissionCycleSeeds.Add(suffix);
            return;
        }
        throw new InvalidOperationException(_swapchainSubmissionIntervals.Count == 0
            ? "Queue submission dependencies contain a pass dependency cycle."
            : "Queue submission dependencies cannot preserve the swapchain image single-submit intervals.");
    }

    private void FindSubmissionEdge(int source, int destination, out int predecessor, out int consumer)
    {
        foreach (int pass in _submissionPasses[destination])
        {
            foreach (int candidate in _predecessors[pass])
                if (Matches(candidate)) { predecessor = candidate; consumer = pass; return; }
            foreach (int candidate in _physicalPredecessors[pass])
                if (Matches(candidate)) { predecessor = candidate; consumer = pass; return; }
            int previous = _sameQueuePredecessor[pass];
            if (Matches(previous)) { predecessor = previous; consumer = pass; return; }
        }
        throw new InvalidOperationException("A submission dependency has no pass dependency.");

        bool Matches(int pass) => pass >= 0 && IsLive(pass) && _submissionByPass[pass] == source;
    }

    private void RemoveSubmissionWave(int submission)
    {
        int wave = _submissionWaves[submission];
        if (wave < 0) return;
        _changedRecordingWaves.Add(wave);
        HashSet<int> members = _submissionsByWave[wave];
        members.Remove(submission);
        if (members.Count == 0) _submissionsByWave.Remove(wave);
        _submissionWaves[submission] = -1;
    }

    private void UpdateSubmissionWaves()
    {
        foreach (int submission in _submissionWaveChanges) _submissionWaveWork.Enqueue(submission);
        while (_submissionWaveWork.TryDequeue(out int submission))
        {
            _submissionWaveChanges.Remove(submission);
            if (_submissionPasses[submission].Count == 0) continue;
            int wave = 0;
            foreach (int dependency in _submissionDependencies[submission])
            {
                int dependencyWave = _submissionWaves[dependency];
                if (dependencyWave < 0) { wave = -1; break; }
                wave = Math.Max(wave, dependencyWave + 1);
            }
            if (wave < 0 || wave == _submissionWaves[submission]) continue;
            RemoveSubmissionWave(submission);
            _submissionWaves[submission] = wave;
            if (!_submissionsByWave.TryGetValue(wave, out var members))
                _submissionsByWave.Add(wave, members = []);
            members.Add(submission);
            _changedRecordingWaves.Add(wave);
            foreach (int consumer in _submissionConsumers[submission])
                if (_submissionWaveChanges.Add(consumer)) _submissionWaveWork.Enqueue(consumer);
        }
    }
}
