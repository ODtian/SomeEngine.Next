namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private bool _submissionsInitialized;
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
        if (!_submissionsInitialized)
        {
            int count = BuildInitialWaves();
            EnsureCapacity(ref _submissionQueues, _submissionCount);
            foreach (int submission in _changedSubmissions)
                _submissionQueues[submission] = _passes[_submissionPasses[submission][0]].Queue;
            _recordingSubmissionChanges.UnionWith(_changedSubmissions);
            _submissionsInitialized = true;
            return count;
        }

        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.BatchConstruction);
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
            if (submission < 0 || !ReferenceEquals(_submissionQueues[submission], _passes[pass].Queue))
                submission = NewSubmission(_passes[pass].Queue!);
            ChangeSubmissionMembers(submission);
            _submissionPasses[submission].Add(pass);
            _submissionByPass[pass] = submission;
            QueueSubmissionConsumers(submission);
            foreach (int consumer in _successors[pass]) QueuePassSubmission(consumer);
            QueuePassSubmission(_sameQueueSuccessor[pass]);
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

    private bool MergeAdjacentSubmissions()
    {
        while (_submissionMergeCandidates.Count != 0)
        {
            int later = _submissionMergeCandidates.First();
            _submissionMergeCandidates.Remove(later);
            List<int> laterPasses = _submissionPasses[later];
            if (laterPasses.Count == 0) continue;
            int previousPass = _sameQueuePredecessor[laterPasses[0]];
            if (previousPass < 0) continue;
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

            ChangeSubmissionMembers(earlier);
            ChangeSubmissionMembers(later);
            QueueSubmissionConsumers(earlier);
            QueueSubmissionConsumers(later);
            foreach (int pass in laterPasses)
            {
                earlierPasses.Add(pass);
                _submissionByPass[pass] = earlier;
                foreach (int consumer in _successors[pass]) QueuePassSubmission(consumer);
                QueuePassSubmission(_sameQueueSuccessor[pass]);
            }
            laterPasses.Clear();
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
            if (_executionOrder.Position(exit) >= _executionOrder.Position(entry)) continue;
            List<int> passes = _submissionPasses[submission];
            int first = passes.IndexOf(entry);
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
            _submissionCycleSeeds.Add(submission);
            _submissionCycleSeeds.Add(suffix);
            return;
        }
        throw new InvalidOperationException("Queue submission dependencies contain a pass dependency cycle.");
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
                if (_submissionWaves[dependency] < 0) { wave = -1; break; }
                wave = Math.Max(wave, _submissionWaves[dependency] + 1);
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
