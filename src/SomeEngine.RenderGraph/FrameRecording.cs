namespace SomeEngine.RenderGraph;

using System.Runtime.ExceptionServices;

internal sealed partial class FrameExecutor
{
    private static readonly Action<object?, int> RecordLatencyBatchAction = RecordLatencyBatch;
    private static readonly global::SomeEngine.Diagnostics.TraceSite RecordSetupProbe =
        new("rg-probe", "RecordSetup", detail: true);
    private static readonly global::SomeEngine.Diagnostics.TraceSite RecordTopologyProbe =
        new("rg-probe", "RecordTopology", detail: true);
    private static readonly global::SomeEngine.Diagnostics.TraceSite RecordStateProbe =
        new("rg-probe", "RecordState", detail: true);
    private static readonly global::SomeEngine.Diagnostics.TraceSite RecordExecutionProbe =
        new("rg-probe", "RecordExecution", detail: true);
    private static readonly global::SomeEngine.Diagnostics.TraceSite RecordAheadScheduleProbe =
        new("rg-probe", "RecordAheadSchedule", detail: true);
    private static readonly global::SomeEngine.Diagnostics.TraceSite RecordAheadSubmitProbe =
        new("rg-probe", "RecordAheadSubmit", detail: true);
    private static readonly global::SomeEngine.Diagnostics.TraceSite RecordAheadCleanupProbe =
        new("rg-probe", "RecordAheadCleanup", detail: true);
    private RecordedCommands[] _recorded = [];
    private bool[] _recordedValid = [];
    private QueueCompletion?[] _passCompletions = [];
    private ulong[] _passSubmissionFrames = [];
    private bool[] _submittedQueues = [];
    private readonly List<QueueCompletion> _submittedCompletions = [];
    private List<int>[] _waves = [];
    private Queue[][] _recordingQueues = [];
    private List<int>[][] _recordingPasses = [];
    private RecordingBatch[][] _callingRecordingUnits = [];
    private RecordingBatch[][] _workerRecordingUnits = [];
    private RecordingBatch[][] _submissionRecordingUnits = [];
    private bool[] _submissionWorkerEligible = [];
    private readonly HashSet<int> _recordingSubmissionChanges = [];
    private readonly HashSet<int> _recordingConfigurationPasses = [];
    private RenderGraphDebugOptions _recordingDebug;
    private bool[] _recordingParallel = [];
    private readonly Dictionary<CommandContext, CommandContextPool.CommandContextLease> _recordingContexts = [];
    private RecordingBatch[] _parallelBatches = [];
    private RecordingBatch[] _encodingBatches = [];
    private RecordingBatch[] _allCallingBatches = [];
    private RecordingBatch[] _allWorkerBatches = [];
    private readonly List<QueueCompletion> _submitWaits = [];
    private RecordedCommands[] _submitCommands = [];
    private readonly List<SwapchainImage> _submitImages = [];
    private ColorAttachmentDesc[]?[] _renderingColorDescriptions = [];
    private RasterRenderingCacheEntry[] _rasterRenderingCache = [];
    private readonly HashSet<int> _commandChanges = [];

    internal void ChangeCommandData(GraphIdentity identity)
    {
        if (TryResolvePass(identity, out int pass)) _commandChanges.Add(pass);
    }

    internal ReadOnlySpan<QueueCompletion> SubmittedCompletions =>
        CollectionsMarshal.AsSpan(_submittedCompletions);

    internal ReadOnlySpan<BufferBoundaryState> GetBufferStates(GraphBufferId buffer) =>
        _submittedCompletions.Count != 0 && TryResolveBuffer(buffer.Value, out int index) &&
        (uint)index < (uint)_buffers.Count && _buffers[index].Identity == buffer.Value
            ? BufferEntryStates(_buffers[index]) : [];

    internal ReadOnlySpan<TextureBoundaryState> GetTextureStates(GraphTextureId texture) =>
        _submittedCompletions.Count != 0 && TryResolveTexture(texture.Value, out int index) &&
        (uint)index < (uint)_textures.Count && _textures[index].Identity == texture.Value
            ? TextureEntryStates(_textures[index]) : [];

    internal bool TryGetPassCompletion(GraphPassId pass, out QueueCompletion completion)
    {
        if (TryResolvePass(pass.Value, out int index) &&
            (uint)index < (uint)_passes.Count &&
            (uint)index < (uint)_passSubmissionFrames.Length &&
            _passes[index].Identity == pass.Value &&
            _passSubmissionFrames[index] == _frame.Identity &&
            _passCompletions[index] is { } submitted)
        {
            completion = submitted;
            return true;
        }
        completion = default;
        return false;
    }

    private int RecordAndSubmit(Span<QueueCompletion> destination)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.RecordSubmit);
        int workerCount = JobSystem.WorkerCount;
        using (global::SomeEngine.Diagnostics.EngineTrace.Begin(RecordSetupProbe))
        {
            RefreshCompletionWaits();
            _reuseRecording = !_recordingChanged && _recordingWorkerCount == workerCount;
            PrepareArray(ref _recorded, _passes.Count);
            PrepareArray(ref _recordedValid, _passes.Count);
            PrepareArray(ref _passCompletions, _passes.Count);
            PrepareArray(ref _passSubmissionFrames, _passes.Count);
            PrepareArray(ref _renderingColorDescriptions, _passes.Count);
            PrepareArray(ref _rasterRenderingCache, _passes.Count);
            PrepareArray(ref _submittedQueues, _frame.Graph.MaximumQueueCompletionCount);
            _submittedCompletions.EnsureCapacity(_frame.Graph.MaximumQueueCompletionCount);
            Array.Clear(_recorded, 0, _passes.Count);
            Array.Clear(_recordedValid, 0, _passes.Count);
            Array.Clear(_passCompletions, 0, _passes.Count);
            Array.Clear(_submittedQueues, 0, _submittedQueues.Length);
        }

        try
        {
            int waveCount;
            using (global::SomeEngine.Diagnostics.EngineTrace.Begin(RecordTopologyProbe))
            {
                if (_reuseRecording)
                {
                    waveCount = _waveCount;
                }
                else
                {
                    waveCount = BuildWaves();
                    _waveCount = waveCount;
                    BuildRecordingGroups(waveCount, _recordingWorkerCount != workerCount ||
                        (_recordingDebug & RenderGraphDebugOptions.DisableParallelRecording) !=
                        (_frame.Options.Debug & RenderGraphDebugOptions.DisableParallelRecording));
                    _recordingWorkerCount = workerCount;
                    _recordingDebug = _frame.Options.Debug;
                }
            }
            using (global::SomeEngine.Diagnostics.EngineTrace.Begin(RecordStateProbe))
                PrepareStateUpdates();
            using var executionProbe =
                global::SomeEngine.Diagnostics.EngineTrace.Begin(RecordExecutionProbe);
            if (_frame.Options.SubmissionMode == FrameSubmissionMode.RecordAllThenSubmit)
            {
                _parallelBatches = _allWorkerBatches;
                if (!TryRecordCoarseBatches(includeCallingWork: true))
                {
                    foreach (RecordingBatch batch in _allCallingBatches) RecordBatch(batch);
                    foreach (RecordingBatch batch in _allWorkerBatches) RecordBatch(batch);
                }
                SubmitLateReleases(destination);
                for (int wave = 0; wave < waveCount; wave++)
                    SubmitWave(wave, destination);
            }
            else if (_frame.Options.SubmissionMode == FrameSubmissionMode.RecordAhead)
            {
                RecordAheadAndSubmit(waveCount, destination);
            }
            else
            {
                SubmitLateReleases(destination);
                for (int wave = 0; wave < waveCount; wave++)
                {
                    RecordWave(wave);
                    SubmitWave(wave, destination);
                }
            }

            _recordingChanged = false;
            _commandChanges.Clear();
            int count = 0;
            for (int queueSlot = 0; queueSlot < _submittedQueues.Length; queueSlot++)
            {
                if (!_submittedQueues[queueSlot]) continue;
                destination[count++] = destination[queueSlot];
            }
            destination.Slice(count, _submittedQueues.Length - count).Clear();
            return count;
        }
        catch
        {
            DisposeUnsubmitted();
            throw;
        }
    }

    private int _recordingWorkerCount = -1;

    private int _submissionCount;
    private List<int>[] _submissionPasses = [];
    private List<int>[] _submissionDependencies = [];
    private List<int>[] _submissionConsumers = [];
    private int[] _submissionByPass = [];
    private int[] _submissionWaves = [];
    private readonly HashSet<int> _changedRecordingPasses = [];
    private readonly HashSet<int> _changedSubmissions = [];
    private readonly HashSet<int> _changedRecordingWaves = [];
    private readonly SortedDictionary<int, HashSet<int>> _submissionsByWave = [];
    private readonly Stack<int> _freeSubmissions = [];
    private readonly SortedSet<(long Position, int Pass)> _submissionPassChanges = [];
    private readonly List<int> _incomingSubmissions = [];
    private readonly List<int> _dependencySearch = [];
    private bool[] _dependencyVisited = [];

    private void ChangeRecording(int pass)
    {
        _commandChanges.Add(pass);
        _changedRecordingPasses.Add(pass);
        _recordingChanged = true;
    }

    private int BuildInitialWaves()
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.BatchConstruction);
        int previousPassCapacity = _submissionByPass.Length;
        EnsureCapacity(ref _submissionByPass, _passes.Count);
        Array.Fill(_submissionByPass, -1, previousPassCapacity, _submissionByPass.Length - previousPassCapacity);
        _changedSubmissions.Clear();
        foreach (int pass in _executionOrder.Nodes)
        {
            _incomingSubmissions.Clear();
            AddSubmissionDependencies(_predecessors[pass]);
            AddSubmissionDependencies(_physicalPredecessors[pass]);
            int predecessor = _sameQueuePredecessor[pass];
            int previous = predecessor >= 0 ? _submissionByPass[predecessor] : -1;
            int current = previous;
            bool split = current < 0 || _passes[predecessor].Options.SubmitAfter;
            if (!split)
            {
                foreach (int dependency in _incomingSubmissions)
                    if (dependency != current && SubmissionDependsOn(dependency, current, _submissionCount))
                    {
                        split = true;
                        break;
                    }
            }
            if (split)
            {
                current = CreateSubmission();
                if (previous >= 0) _submissionDependencies[current].Add(previous);
            }
            MarkPassResources(pass, _stateConnectionChanges);
            _submissionPasses[current].Add(pass);
            _submissionByPass[pass] = current;
            foreach (int dependency in _incomingSubmissions)
                if (dependency != current && !_submissionDependencies[current].Contains(dependency))
                    _submissionDependencies[current].Add(dependency);
        }
        foreach (int submission in _changedSubmissions)
            foreach (int dependency in _submissionDependencies[submission])
                _submissionConsumers[dependency].Add(submission);

        int remaining = _changedSubmissions.Count;
        while (remaining != 0)
        {
            bool progress = false;
            foreach (int submission in _changedSubmissions)
            {
                if (_submissionWaves[submission] >= 0) continue;
                int wave = 0;
                bool ready = true;
                foreach (int dependency in _submissionDependencies[submission])
                {
                    int dependencyWave = _submissionWaves[dependency];
                    if (dependencyWave < 0) { ready = false; break; }
                    wave = Math.Max(wave, dependencyWave + 1);
                }
                if (!ready) continue;
                _submissionWaves[submission] = wave;
                if (!_submissionsByWave.TryGetValue(wave, out var members))
                    _submissionsByWave.Add(wave, members = []);
                members.Add(submission);
                _changedRecordingWaves.Add(wave);
                remaining--;
                progress = true;
            }
            if (!progress) throw new InvalidOperationException("Queue submission dependencies contain a cycle.");
        }
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

    private int CreateSubmission()
    {
        int submission = _freeSubmissions.TryPop(out int available) ? available : _submissionCount++;
        EnsureCapacity(ref _submissionPasses, _submissionCount);
        EnsureCapacity(ref _submissionDependencies, _submissionCount);
        EnsureCapacity(ref _submissionConsumers, _submissionCount);
        EnsureCapacity(ref _submissionWaves, _submissionCount);
        EnsureCapacity(ref _dependencyVisited, _submissionCount);
        _submissionPasses[submission] ??= [];
        _submissionDependencies[submission] ??= [];
        _submissionConsumers[submission] ??= [];
        _submissionWaves[submission] = -1;
        _changedSubmissions.Add(submission);
        return submission;
    }

    private void AddSubmissionDependencies(List<int> predecessors)
    {
        foreach (int predecessor in predecessors)
        {
            if (!_live[predecessor]) continue;
            int submission = _submissionByPass[predecessor];
            DevelopmentCheck.Require(submission >= 0, "A scheduled pass precedes its dependency.");
            if (!_incomingSubmissions.Contains(submission))
                _incomingSubmissions.Add(submission);
        }
    }

    private bool SubmissionDependsOn(int submission, int ancestor, int count)
    {
        Array.Clear(_dependencyVisited, 0, count);
        _dependencySearch.Clear();
        _dependencySearch.Add(submission);
        while (_dependencySearch.Count != 0)
        {
            int last = _dependencySearch.Count - 1;
            int current = _dependencySearch[last];
            _dependencySearch.RemoveAt(last);
            if (current == ancestor) return true;
            if (_dependencyVisited[current]) continue;
            _dependencyVisited[current] = true;
            foreach (int dependency in _submissionDependencies[current])
                _dependencySearch.Add(dependency);
        }
        return false;
    }

    internal void ReleaseRecordingContexts()
    {
        foreach (var storage in _recordingContexts.Values)
            storage.Dispose();
        _recordingContexts.Clear();
        foreach (PassParameters pass in _passParameters)
            foreach (ParameterBlock block in pass.Blocks)
                ReleaseParameterBlock(block);
    }

    private void BuildRecordingGroups(int waveCount, bool repartition)
    {
        if (repartition)
            foreach (var members in _submissionsByWave.Values) _recordingSubmissionChanges.UnionWith(members);
        foreach (int pass in _recordingConfigurationPasses)
            if ((uint)pass < (uint)_submissionByPass.Length && _submissionByPass[pass] >= 0)
                _recordingSubmissionChanges.Add(_submissionByPass[pass]);
        EnsureCapacity(ref _submissionRecordingUnits, _submissionCount);
        EnsureCapacity(ref _submissionWorkerEligible, _submissionCount);
        int maximumUnitCount = checked(JobSystem.WorkerCount + 1);
        foreach (int submission in _recordingSubmissionChanges)
        {
            RebuildRecordingUnits(submission, maximumUnitCount);
            if (_submissionWaves[submission] >= 0) _changedRecordingWaves.Add(_submissionWaves[submission]);
        }
        EnsureCapacity(ref _recordingParallel, waveCount);
        EnsureCapacity(ref _recordingQueues, waveCount);
        EnsureCapacity(ref _recordingPasses, waveCount);
        EnsureCapacity(ref _callingRecordingUnits, waveCount);
        EnsureCapacity(ref _workerRecordingUnits, waveCount);
        foreach (int wave in _changedRecordingWaves)
        {
            if (wave >= waveCount)
            {
                if (wave < _callingRecordingUnits.Length) _callingRecordingUnits[wave] = [];
                if (wave < _workerRecordingUnits.Length) _workerRecordingUnits[wave] = [];
                continue;
            }
            int[] submissions = _submissionsByWave.TryGetValue(wave, out var members) ? members.ToArray() : [];
            Array.Sort(submissions, (left, right) => _executionOrder.Position(_submissionPasses[left][0])
                .CompareTo(_executionOrder.Position(_submissionPasses[right][0])));
            var queues = new Queue[submissions.Length];
            var groups = new List<int>[submissions.Length];
            var callingUnits = new List<RecordingBatch>();
            var workerUnits = new List<RecordingBatch>();
            var workerPasses = new List<int>();
            for (int group = 0; group < submissions.Length; group++)
            {
                int submission = submissions[group];
                List<int> passes = _submissionPasses[submission];
                queues[group] = _passes[passes[0]].Queue!;
                groups[group] = passes;
                if (_submissionWorkerEligible[submission])
                {
                    workerUnits.AddRange(_submissionRecordingUnits[submission]);
                    workerPasses.AddRange(passes);
                }
                else callingUnits.AddRange(_submissionRecordingUnits[submission]);
            }
            _recordingQueues[wave] = queues;
            _recordingPasses[wave] = groups;
            _callingRecordingUnits[wave] = callingUnits.ToArray();
            _workerRecordingUnits[wave] = workerUnits.ToArray();
            _recordingParallel[wave] = maximumUnitCount >= 2 &&
                IsParallelRecordingWorthwhile(CollectionsMarshal.AsSpan(workerPasses));
        }
        _recordingSubmissionChanges.Clear();
        _recordingConfigurationPasses.Clear();
        _changedRecordingWaves.Clear();
        var allCalling = new List<RecordingBatch>();
        var allWorkers = new List<RecordingBatch>();
        for (int wave = 0; wave < waveCount; wave++)
        {
            allCalling.AddRange(_callingRecordingUnits[wave]);
            if (_recordingParallel[wave]) allWorkers.AddRange(_workerRecordingUnits[wave]);
            else allCalling.AddRange(_workerRecordingUnits[wave]);
        }
        _allCallingBatches = allCalling.ToArray();
        _allWorkerBatches = allWorkers.ToArray();
    }

    private void RecordWave(int wave)
    {
        foreach (RecordingBatch batch in _callingRecordingUnits[wave]) RecordBatch(batch);
        _parallelBatches = _workerRecordingUnits[wave];
        if (_recordingParallel[wave] && TryRecordCoarseBatches()) return;
        foreach (RecordingBatch batch in _workerRecordingUnits[wave])
            RecordBatch(batch);
    }

    private bool IsParallelRecordingWorthwhile(ReadOnlySpan<int> passes)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.BatchConstruction);
        if ((_frame.Options.Debug & RenderGraphDebugOptions.DisableParallelRecording) != 0)
            return false;
        int count = 0;
        ulong weight = 0;
        foreach (int pass in passes)
        {
            if (_passes[pass].Options.Recording != PassRecordingMode.WorkerEligible)
                continue;
            count++;
            weight = SaturatingAdd(weight, RecordingWeight(pass));
        }
        return count > 1 && weight >= (ulong)count * 8;
    }

    private ulong RecordingWeight(int pass)
    {
        ulong weight = Math.Max(_passes[pass].Options.EstimatedRecordingCost, 1);
        int aliasCount = _aliasBeforeResources[pass].Count;
        int beforeCount =
            _acquires[pass].Count +
            _beforeBufferBarriers[pass].Count +
            _beforeTextureBarriers[pass].Count;
        int afterCount =
            _afterBufferBarriers[pass].Count +
            _afterTextureBarriers[pass].Count +
            _releases[pass].Count;
        int barrierCalls = 0;
        if (aliasCount == 0)
        {
            if (beforeCount != 0)
                barrierCalls++;
        }
        else
        {
            if (_acquires[pass].Count != 0)
                barrierCalls++;
            barrierCalls++;
            if (_beforeBufferBarriers[pass].Count != 0 ||
                _beforeTextureBarriers[pass].Count != 0)
                barrierCalls++;
        }
        if (afterCount != 0)
            barrierCalls++;
        ulong recordedWork = (ulong)(aliasCount + beforeCount + afterCount) +
            (ulong)barrierCalls * 4;
        return SaturatingAdd(weight, recordedWork);
    }

    private void RecordBatch(
        List<int> passes,
        int start,
        int count,
        int preparedAccessCount,
        CommandContext context)
    {
        if (count == 0) return;
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.CommandLists);
        int end = checked(start + count);
        if (TryRepeatBatch(new RecordingBatch(passes, start, count, preparedAccessCount, context))) return;

        bool begun = false;
        try
        {
            ulong resourceDescriptors = 0, samplerDescriptors = 0;
            if (context.QueueType != QueueType.Copy)
            {
                for (int item = start; item < end; item++)
                {
                    foreach (ParameterBlock block in _passParameters[passes[item]].Blocks)
                    {
                        // The resolved binding list comes from the user's shader.
                        // Ordinary data may also need a constant-buffer descriptor.
                        if (block.Data is { Length: > 0 }) resourceDescriptors++;
                        for (int binding = 0; binding < block.Count; binding++)
                        {
                            if (block.Resources[binding].Type == ResourceBindingType.Sampler) samplerDescriptors++;
                            else resourceDescriptors++;
                        }
                    }
                }
            }
            DeviceLimits limits = _frame.Graph.Device.Capabilities.Limits;
            _frame.Backend.Begin(context, new CommandRecordingDesc(
                InitialResourceDescriptorCapacity: (uint)Math.Min(resourceDescriptors, limits.ResourceDescriptorCapacity),
                InitialSamplerDescriptorCapacity: (uint)Math.Min(samplerDescriptors, limits.SamplerDescriptorCapacity),
                Label: _passes[passes[start]].Label));
            begun = true;
            for (int item = start; item < end; item++)
            {
                int pass = passes[item];
                RecordPassCommands(pass, _passes[pass].Kind, context);
            }
            RecordedCommands recorded = _frame.Backend.End(context);
            begun = false;
            int owner = passes[start];
            _recorded[owner] = recorded;
            _recordedValid[owner] = true;
        }
        catch
        {
            if (begun)
            {
                try { _frame.Backend.Discard(context); }
                catch { }
            }
            throw;
        }
    }

    private bool TryRecordCoarseBatches(bool includeCallingWork = false)
    {
        EnsureCapacity(ref _encodingBatches, _parallelBatches.Length);
        int batchCount = 0;
        foreach (RecordingBatch batch in _parallelBatches)
            if (!TryRepeatBatch(batch)) _encodingBatches[batchCount++] = batch;
        _parallelBatches = _encodingBatches;
        if (batchCount < 2)
        {
            if (includeCallingWork)
                foreach (RecordingBatch batch in _allCallingBatches) RecordBatch(batch);
            if (batchCount == 1) RecordBatch(_parallelBatches[0]);
            return true;
        }

        if (batchCount == 2 && JobSystem.TryHandoffLatencyWork(
            this,
            RecordLatencyBatchAction,
            1,
            JobPriority.High,
            out long latencySequence))
            return RecordCallingBatchAndJoinLatencyWorker(latencySequence, includeCallingWork);

        var job = new RecordBatchJob(this, _parallelBatches, 1);
        JobHandle handle = JobSystem.ScheduleParallel(job, batchCount - 1, 1);
        Exception? callingThreadFailure = null;
        try
        {
            if (includeCallingWork)
                foreach (RecordingBatch batch in _allCallingBatches) RecordBatch(batch);
            RecordBatch(_parallelBatches[0]);
        }
        catch (Exception exception)
        {
            callingThreadFailure = exception;
        }

        try
        {
            handle.Complete();
        }
        catch when (callingThreadFailure is not null)
        {
        }
        if (callingThreadFailure is not null)
            ExceptionDispatchInfo.Capture(callingThreadFailure).Throw();
        return true;
    }

    private bool RecordCallingBatchAndJoinLatencyWorker(long latencySequence, bool includeCallingWork)
    {
        Exception? callingThreadFailure = null;
        try
        {
            if (includeCallingWork)
                foreach (RecordingBatch batch in _allCallingBatches) RecordBatch(batch);
            RecordBatch(_parallelBatches[0]);
        }
        catch (Exception exception)
        {
            callingThreadFailure = exception;
        }
        if (JobSystem.TryReclaimLatencyWork(latencySequence))
        {
            if (callingThreadFailure is not null)
                ExceptionDispatchInfo.Capture(callingThreadFailure).Throw();
            RecordBatch(_parallelBatches[1]);
            return true;
        }
        try
        {
            JobSystem.JoinLatencyWork(latencySequence);
        }
        catch when (callingThreadFailure is not null)
        {
        }
        if (callingThreadFailure is not null)
            ExceptionDispatchInfo.Capture(callingThreadFailure).Throw();
        return true;
    }

    private bool CanRepeatCommands(int pass)
    {
        ref readonly FramePass row = ref CollectionsMarshal.AsSpan(_passes)[pass];
        return row.Options.RepeatCommands && row.PersistentCallbacks is not null;
    }

    private void RebuildRecordingUnits(int submission, int maximumUnitCount)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.BatchConstruction);
        List<int> passes = _submissionPasses[submission];
        _commandChanges.UnionWith(passes);
        RecordingBatch[] previous = _submissionRecordingUnits[submission] ?? [];
        bool workerEligible = (_frame.Options.Debug & RenderGraphDebugOptions.DisableParallelRecording) == 0;
        foreach (int pass in passes)
            workerEligible &= _passes[pass].Options.Recording == PassRecordingMode.WorkerEligible;
        var units = new List<RecordingBatch>(Math.Max(previous.Length, 1));
        try
        {
            int start = 0;
            while (start < passes.Count)
            {
                bool repeatable = CanRepeatCommands(passes[start]);
                int end = start + 1;
                while (end < passes.Count && CanRepeatCommands(passes[end]) == repeatable) end++;
                // Keep adjacent passes that allow command reuse in one native list.
                // Split the remaining callbacks for parallel recording.
                int unitCount = repeatable || !workerEligible ? 1 :
                    Math.Min(maximumUnitCount, Math.Max(1, (end - start + 15) / 16));
                ulong remainingWeight = 0;
                for (int index = start; index < end; index++)
                    remainingWeight = SaturatingAdd(remainingWeight, RecordingWeight(passes[index]));
                for (int unit = 0; start < end; unit++)
                {
                    int remainingUnits = Math.Max(1, unitCount - unit);
                    int maximumEnd = end - (remainingUnits - 1);
                    ulong divisor = (ulong)remainingUnits;
                    ulong targetWeight = remainingWeight / divisor +
                        (remainingWeight % divisor == 0 ? 0UL : 1UL);
                    int count = 0;
                    ulong weight = 0;
                    while (start + count < maximumEnd)
                    {
                        ulong nextWeight = RecordingWeight(passes[start + count]);
                        ulong combinedWeight = SaturatingAdd(weight, nextWeight);
                        if (count != 0 && weight < targetWeight &&
                            combinedWeight > targetWeight &&
                            targetWeight - weight <= combinedWeight - targetWeight)
                            break;
                        weight = combinedWeight;
                        count++;
                        if (weight >= targetWeight) break;
                    }
                    if (count == 0)
                    {
                        weight = RecordingWeight(passes[start]);
                        count = 1;
                    }
                    int index = units.Count;
                    CommandContext? context = index < previous.Length ? previous[index].Context : null;
                    Queue queue = _passes[passes[start]].Queue!;
                    if (context?.QueueType != queue.Type || context?.QueueIndex != queue.Index) context = null;
                    units.Add(CreateRecordingBatch(passes, start, count, -1, context));
                    start += count;
                    remainingWeight = remainingWeight > weight ? remainingWeight - weight : 0;
                }
            }
        }
        catch
        {
            foreach (RecordingBatch unit in units)
                if (unit.Context is { } context && !previous.Any(old => ReferenceEquals(old.Context, context)))
                    ReturnContext(context);
            throw;
        }
        _submissionRecordingUnits[submission] = units.ToArray();
        _submissionWorkerEligible[submission] = workerEligible;
        foreach (RecordingBatch old in previous)
            if (!units.Any(unit => ReferenceEquals(unit.Context, old.Context))) ReturnContext(old.Context);

        void ReturnContext(CommandContext context)
        {
            // Batch membership borrows command storage. The graph's existing
            // pool remains its only holder when an immediate batch disappears.
            if (_recordingContexts.Remove(context, out var storage)) storage.Dispose();
        }
    }

    private RecordingBatch CreateRecordingBatch(List<int> passes, int start, int count, int accessCount,
        CommandContext? reusable = null)
    {
        if (reusable is not null) return new RecordingBatch(passes, start, count, accessCount, reusable);
        Queue queue = _passes[passes[start]].Queue!;
        var storage = _frame.Graph.CommandContexts.Acquire(queue, false, _passes[passes[start]].Label, initialSlotCount: 1);
        CommandContext context = storage.Context;
        try
        {
            _recordingContexts.Add(context, storage);
            return new RecordingBatch(passes, start, count, accessCount, context);
        }
        catch
        {
            storage.Dispose();
            throw;
        }
    }

    private void RecordBatch(in RecordingBatch batch) =>
        RecordBatch(batch.Passes, batch.Start, batch.Count, batch.AccessCount, batch.Context);

    private bool TryRepeatBatch(in RecordingBatch batch)
    {
        for (int item = batch.Start; item < batch.Start + batch.Count; item++)
        {
            int pass = batch.Passes[item];
            if (!CanRepeatCommands(pass) || _commandChanges.Contains(pass)) return false;
        }
        if (!_frame.Backend.TryRepeatCommands(batch.Context, out RecordedCommands commands)) return false;
        int first = batch.Passes[batch.Start];
        _recorded[first] = commands;
        _recordedValid[first] = true;
        return true;
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static void RecordLatencyBatch(object? state, int index)
    {
        FrameExecutor executor = (FrameExecutor)state!;
        executor.RecordBatch(executor._parallelBatches[index]);
    }

    private void RecordPassCommands(int pass, GraphPassKind kind, CommandContext context)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.PassDispatch);
        RecordBefore(pass, context);
        foreach (int accessIndex in _passAccesses[pass])
        {
            FrameResourceAccess access = _accesses[accessIndex];
            if (access.TargetKind == GraphAccessTargetKind.QueryPool &&
                access.Mode == GraphAccessMode.Write && access.Coverage == WriteCoverage.Complete)
                _frame.Backend.ResetQueries(context, _queryPools[access.ResourceIndex].Resource,
                    access.QueryRange.FirstQuery, access.QueryRange.QueryCount);
        }
        if (kind == GraphPassKind.Raster)
        {
            if (_shadingRateImages)
                _frame.Backend.SetShadingRateImage(context, FindShadingRateImage(pass));
            RenderingDesc description = BuildRasterRendering(pass);
            _frame.Backend.BeginRendering(context, description);
            InvokePass(pass, context);
            _frame.Backend.EndRendering(context);
        }
        else InvokePass(pass, context);
        RecordAfter(pass, context);
    }

    private Texture? FindShadingRateImage(int pass)
    {
        Texture? texture = null;
        foreach (int index in _passAccesses[pass])
        {
            FrameResourceAccess access = _accesses[index];
            if (access.TargetKind != GraphAccessTargetKind.Texture ||
                access.TextureLayout != TextureLayout.ShadingRateSource) continue;
            Texture current = _textures[access.ResourceIndex].Resource!;
            if (texture is not null && !ReferenceEquals(texture, current))
                throw new InvalidOperationException("A raster pass can use only one shading-rate image.");
            texture = current;
        }
        return texture;
    }

    internal void ValidateShadingRateImage(int pass, Texture? texture)
    {
        if (!ReferenceEquals(texture, FindShadingRateImage(pass)))
            throw new InvalidOperationException("The shading-rate image must match the pass declaration before rendering begins.");
    }

    private void InvokePass(int pass, CommandContext context)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.Callbacks);
        ref readonly FramePass row = ref CollectionsMarshal.AsSpan(_passes)[pass];
        if (row.Pipeline is not null)
        {
            _frame.Backend.SetPipeline(context, row.Pipeline);
            if (row.ParameterLayout != ShaderVariableLayout.Null)
                SetParameterBindings(pass, context);
        }
        if (row.PersistentCallbacks is not null)
            row.PersistentCallbacks.Record(_frame, pass, context);
        else
        {
            DevelopmentCheck.Require(row.FrameCallbacks is not null, "The Pass has no command callback.");
            row.FrameCallbacks.Record(row.FrameCallbackIndex, _frame, pass, context);
        }
    }

    private void RecordBefore(int pass, CommandContext context)
    {
        List<AliasingResource> aliasBefore = _aliasBeforeResources[pass];
        List<AliasingResource> aliasAfter = _aliasAfterResources[pass];
        if (aliasBefore.Count == 0 && aliasAfter.Count == 0)
        {
            if (_acquires[pass].Count == 0 &&
                _beforeBufferBarriers[pass].Count == 0 &&
                _beforeTextureBarriers[pass].Count == 0)
                return;
            RecordBarrierBatch(
                context,
                CollectionsMarshal.AsSpan(_acquires[pass]),
                CollectionsMarshal.AsSpan(_beforeBufferBarriers[pass]),
                CollectionsMarshal.AsSpan(_beforeTextureBarriers[pass]),
                []);
            return;
        }

        RecordBarrierBatch(
            context,
            CollectionsMarshal.AsSpan(_acquires[pass]),
            [],
            [],
            []);
        _frame.Backend.Barrier(context, new AliasingBarrier(
            CollectionsMarshal.AsSpan(aliasBefore),
            CollectionsMarshal.AsSpan(aliasAfter)));
        RecordBarrierBatch(
            context,
            [],
            CollectionsMarshal.AsSpan(_beforeBufferBarriers[pass]),
            CollectionsMarshal.AsSpan(_beforeTextureBarriers[pass]),
            []);
    }

    private void RecordAfter(int pass, CommandContext context)
    {
        if (_afterBufferBarriers[pass].Count == 0 &&
            _afterTextureBarriers[pass].Count == 0 &&
            _releases[pass].Count == 0)
            return;
        RecordBarrierBatch(
            context,
            [],
            CollectionsMarshal.AsSpan(_afterBufferBarriers[pass]),
            CollectionsMarshal.AsSpan(_afterTextureBarriers[pass]),
            CollectionsMarshal.AsSpan(_releases[pass]));
    }

    private void RecordBarrierBatch(
        CommandContext context,
        ReadOnlySpan<QueueAcquire> acquires,
        ReadOnlySpan<BufferBarrier> buffers,
        ReadOnlySpan<TextureBarrier> textures,
        ReadOnlySpan<QueueRelease> releases)
    {
        BarrierBatch batch = new([], acquires, buffers, textures, releases);
        if (!batch.IsEmpty)
            _frame.Backend.Barrier(context, batch);
    }

    private readonly Dictionary<Queue, List<PendingQueueRelease>> _lateReleaseBatches = [];
    private readonly List<Queue> _lateReleaseQueues = [];
    private readonly List<QueueRelease> _lateReleaseBarriers = [];
    private readonly List<QueueAcquire> _lateAcquireBarriers = [];

    private void SubmitLateReleases(Span<QueueCompletion> destination)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.LateReleases);
        foreach (Queue queue in _lateReleaseQueues) _lateReleaseBatches[queue].Clear();
        _lateReleaseQueues.Clear();
        foreach (PendingQueueRelease release in _lateReleases)
        {
            if (!_lateReleaseBatches.TryGetValue(release.SourceQueue, out var releases))
            {
                releases = [];
                _lateReleaseBatches.Add(release.SourceQueue, releases);
            }
            if (releases.Count == 0) _lateReleaseQueues.Add(release.SourceQueue);
            releases.Add(release);
        }
        _submittedCompletions.EnsureCapacity(_frame.Graph.MaximumQueueCompletionCount + _lateReleaseQueues.Count);
        foreach (Queue queue in _lateReleaseQueues)
        {
            List<PendingQueueRelease> releases = _lateReleaseBatches[queue];
            _lateReleaseBarriers.Clear();
            _lateAcquireBarriers.Clear();
            foreach (PendingQueueRelease release in releases)
            {
                if (release.AcquireFrom is { } source)
                    _lateAcquireBarriers.Add(new QueueAcquire(release.Resource, release.TextureRange,
                        source, release.Sync, release.Access, release.Layout));
                if (release.Release)
                    _lateReleaseBarriers.Add(new QueueRelease(release.Resource, release.TextureRange,
                        release.Sync, release.Access, release.Layout, release.DestinationQueue.Type));
            }
            using CommandContextPool.CommandContextLease lease =
                _frame.Graph.CommandContexts.Acquire(queue, false, "RenderGraph late releases");
            CommandContext context = lease.Context;
            bool begun = false;
            RecordedCommands commands;
            try
            {
                _frame.Backend.Begin(context, new CommandRecordingDesc(Label: "RenderGraph late releases"));
                begun = true;
                RecordBarrierBatch(context, CollectionsMarshal.AsSpan(_lateAcquireBarriers), [], [], []);
                RecordBarrierBatch(context, [], [], [], CollectionsMarshal.AsSpan(_lateReleaseBarriers));
                commands = _frame.Backend.End(context);
                begun = false;
            }
            catch
            {
                if (begun)
                {
                    try { _frame.Backend.Discard(context); }
                    catch { }
                }
                throw;
            }
            if (_submitCommands.Length == 0) Array.Resize(ref _submitCommands, 1);
            _submitCommands[0] = commands;
            try
            {
                PrepareLateReleaseState(queue, releases);
                _submitWaits.Clear();
                foreach (var key in _lateStateResources)
                {
                    if (key.Kind == GraphAccessTargetKind.Buffer)
                        foreach (BufferBoundaryState state in BufferEntryStates(_buffers[key.Index])) AddWait(state.ReadyAfter);
                    else if (key.Kind == GraphAccessTargetKind.Texture)
                        foreach (TextureBoundaryState state in TextureEntryStates(_textures[key.Index])) AddWait(state.ReadyAfter);
                }
                QueueCompletion completion = _frame.Backend.Submit(queue,
                    new QueueSubmitDesc(CollectionsMarshal.AsSpan(_submitWaits), [], _submitCommands.AsSpan(0, 1), [], []));
                AddCompletion(_submittedCompletions, completion);
                CommitLateReleaseState(completion);
                int queueIndex = QueueSlot(queue);
                if (queueIndex >= 0)
                {
                    destination[queueIndex] = completion;
                    _submittedQueues[queueIndex] = true;
                }
                foreach (PendingQueueRelease release in releases)
                {
                    if (release.ConsumerPass >= 0)
                        AddCompletion(_completionWaits[release.ConsumerPass], completion);
                    else if (_resourceRows.TryGetValue(release.Resource, out var key) &&
                        _resourceAccessLists.TryGetValue(key, out List<int>? accesses))
                        foreach (int index in accesses)
                        {
                            int pass = _accesses[index].PassIndex;
                            if (IsLive(pass) && !ReferenceEquals(_passes[pass].Queue, queue))
                                AddCompletion(_completionWaits[pass], completion);
                        }
                }
            }
            finally
            {
                _submitCommands[0] = default;
                commands.Dispose();
            }
            void AddWait(QueueCompletion? completion)
            {
                if (completion.HasValue && !ReferenceEquals(completion.Value.Queue, queue))
                    AddCompletion(_submitWaits, completion.Value);
            }
        }
    }

    private void SubmitWave(int wave, Span<QueueCompletion> destination)
    {
        Queue[] queues = _recordingQueues[wave];
        List<int>[] passes = _recordingPasses[wave];
        for (int index = 0; index < queues.Length; index++)
            SubmitBatch(queues[index], passes[index], destination);
    }

    private void SubmitBatch(
        Queue queue,
        List<int> passes,
        Span<QueueCompletion> destination)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.SubmitAssembly);
        _submitWaits.Clear();
        if (_submitCommands.Length < passes.Count)
            Array.Resize(ref _submitCommands, passes.Count);

        int commandCount = 0;
        for (int item = 0; item < passes.Count; item++)
        {
            int pass = passes[item];
            if (_recordedValid[pass])
                _submitCommands[commandCount++] = _recorded[pass];
            foreach (QueueCompletion wait in _completionWaits[pass])
                AddCompletion(_submitWaits, wait);
        }
        int submission = _submissionByPass[passes[0]];
        foreach (int dependency in _submissionDependencies[submission])
        {
            int predecessor = _submissionPasses[dependency][0];
            if (!ReferenceEquals(_passes[predecessor].Queue, queue))
                AddCompletion(_submitWaits, _passCompletions[predecessor]!.Value);
        }
        DevelopmentCheck.Require(commandCount != 0,
            "The recording batch has no executable RecordedCommands.");

        PrepareSubmissionState(submission);
        ResolveSwapchainImages(CollectionsMarshal.AsSpan(passes));
        try
        {
            QueueCompletion completion = _frame.Backend.Submit(
                queue,
                new QueueSubmitDesc(
                    CollectionsMarshal.AsSpan(_submitWaits),
                    [],
                    _submitCommands.AsSpan(0, commandCount),
                    CollectionsMarshal.AsSpan(_submitImages),
                    []));
            int queueSlot = QueueSlot(queue);
            destination[queueSlot] = completion;
            _submittedQueues[queueSlot] = true;
            AddCompletion(_submittedCompletions, completion);
            foreach (int pass in passes)
            {
                _passCompletions[pass] = completion;
                _passSubmissionFrames[pass] = _frame.Identity;
            }
            CommitSubmissionState(submission, completion);
            _frame.NotifySubmitted();
        }
        finally
        {
            for (int item = 0; item < commandCount; item++)
                _submitCommands[item] = default;
            foreach (int pass in passes)
            {
                if (!_recordedValid[pass]) continue;
                _recorded[pass].Dispose();
                _recordedValid[pass] = false;
            }
        }
    }

    private void ResolveSwapchainImages(ReadOnlySpan<int> passes)
    {
        _submitImages.Clear();
        foreach ((GraphIdentity identity, SwapchainImage image, Queue presentQueue) in _frame.SwapchainImages)
        {
            int texture = ResolveTexture(identity);
            foreach (int pass in passes)
            {
                if (_textures[texture].LastPass != pass ||
                    !ReferenceEquals(_passes[pass].Queue, presentQueue))
                    continue;
                _submitImages.Add(image);
                break;
            }
        }
    }
    private int QueueSlot(Queue queue)
    {
        ReadOnlySpan<Queue> queues = _frame.Graph.Queues;
        for (int i = 0; i < queues.Length; i++)
            if (ReferenceEquals(queues[i], queue)) return i;
        return -1;
    }

    private void DisposeUnsubmitted()
    {
        for (int pass = 0; pass < _recorded.Length; pass++)
        {
            if (!_recordedValid[pass]) continue;
            _recorded[pass].Dispose();
            _recordedValid[pass] = false;
        }
    }

    internal void BeginRawRendering(
        int pass,
        PassRenderingRegionId region,
        CommandContext context,
        int expectedOrdinal)
    {
        GraphIdentity passIdentity = _passes[pass].Identity;
        if (region.Pass != passIdentity || region.Value.Slot != expectedOrdinal)
            throw new InvalidOperationException("The rendering region is invalid or out of order.");
        RenderingDesc description = BuildGeneralRendering(pass, expectedOrdinal);
        _frame.Backend.BeginRendering(context, description);
    }

    [System.Diagnostics.Conditional("DEBUG")]
    internal void ValidateRawRegionsCompleted(int pass, int count)
    {
        int expected = RawRegionCount(pass);
        if (count != expected)
            throw new InvalidOperationException("Not every declared Raw rendering region was executed exactly once.");
    }

    private int RawRegionCount(int pass)
    {
        FramePass row = _passes[pass];
        if (row.Definition is not null) return row.Definition.RenderingRegions.Count;
        return _frame.DynamicRenderingRegions.TryGetValue(row.Identity, out List<PassRenderingRegion>? regions)
            ? regions.Count
            : 0;
    }

    private RenderingDesc BuildRasterRendering(int pass) => BuildRendering(pass, -1);
    private RenderingDesc BuildGeneralRendering(int pass, int region) => BuildRendering(pass, region);

    private RenderingDesc BuildRendering(int pass, int region)
    {
        if (region < 0)
        {
            ref readonly RasterRenderingCacheEntry cached = ref _rasterRenderingCache[pass];
            if (cached.Valid)
            {
                return new RenderingDesc(
                    _renderingColorDescriptions[pass]!,
                    cached.DepthStencil,
                    cached.Width,
                    cached.Height,
                    cached.Options, cached.X, cached.Y);
            }
        }

        FramePass row = _passes[pass];
        List<GraphColorAttachment> colors;
        GraphDepthStencilAttachment? depth;
        PassRenderingRegion? renderRegion = null;
        if (row.Definition is not null)
        {
            colors = row.Definition.ColorAttachments;
            depth = row.Definition.DepthStencilAttachment;
            if (region >= 0)
            {
                renderRegion = row.Definition.RenderingRegions[region];
            }
            else if (row.Kind == GraphPassKind.Raster && row.Definition.RenderingRegions.Count != 0)
            {
                DevelopmentCheck.Require(row.Definition.RenderingRegions.Count == 1,
                    "A Raster pass may define at most one explicit extent.");
                renderRegion = row.Definition.RenderingRegions[0];
            }
        }
        else
        {
            colors = _frame.DynamicColorAttachments.TryGetValue(row.Identity, out List<GraphColorAttachment>? found)
                ? found
                : [];
            depth = _frame.DynamicDepthAttachments.TryGetValue(row.Identity, out GraphDepthStencilAttachment foundDepth)
                ? foundDepth
                : null;
            if (region >= 0)
                renderRegion = _frame.DynamicRenderingRegions[row.Identity][region];
            else if (row.Kind == GraphPassKind.Raster &&
                     _frame.DynamicRenderingRegions.TryGetValue(row.Identity, out List<PassRenderingRegion>? rasterRegions) &&
                     rasterRegions.Count != 0)
            {
                DevelopmentCheck.Require(rasterRegions.Count == 1,
                    "A Raster pass may define at most one explicit extent.");
                renderRegion = rasterRegions[0];
            }
        }

        int selectedColorCount = 0;
        foreach (GraphColorAttachment attachment in colors)
            if (attachment.RenderingRegionIndex == region) selectedColorCount++;
        ColorAttachmentDesc[]? cachedColors = _renderingColorDescriptions[pass];
        if (cachedColors is null || cachedColors.Length != selectedColorCount)
        {
            cachedColors = selectedColorCount == 0
                ? []
                : new ColorAttachmentDesc[selectedColorCount];
            _renderingColorDescriptions[pass] = cachedColors;
        }
        Span<ColorAttachmentDesc> colorDescriptions = cachedColors;
        DevelopmentCheck.Require(region >= 0 ||
            ((row.Options.RasterWidth == 0) == (row.Options.RasterHeight == 0)),
            "A Raster pass must specify both explicit extent dimensions or neither.");
        uint width = renderRegion?.Width ??
            (row.Options.RasterWidth != 0 ? row.Options.RasterWidth : uint.MaxValue);
        uint height = renderRegion?.Height ??
            (row.Options.RasterHeight != 0 ? row.Options.RasterHeight : uint.MaxValue);
        int colorDestination = 0;
        for (uint slot = 0; slot < 8 && colorDestination < selectedColorCount; slot++)
        {
            foreach (GraphColorAttachment attachment in colors)
            {
                if (attachment.RenderingRegionIndex != region || attachment.Slot != slot)
                    continue;
                ColorAttachmentView view = GetColorAttachmentView(pass,
                    new GraphColorAttachmentViewId(attachment.View));
                ColorAttachmentView? resolve = attachment.ResolveView.IsValid
                    ? GetColorAttachmentView(pass, new GraphColorAttachmentViewId(attachment.ResolveView))
                    : null;
                colorDescriptions[colorDestination++] = new ColorAttachmentDesc(
                    view,
                    attachment.Load,
                    attachment.Store,
                    attachment.ClearValue,
                    resolve,
                    attachment.ResolveType);
                ResolveAttachmentExtent(
                    view.Description.Texture,
                    view.Description.Range.FirstMipLevel,
                    ref width,
                    ref height);
            }
        }
        DevelopmentCheck.Require(colorDestination == selectedColorCount,
            "A rendering region has an invalid Color Attachment slot.");

        DepthStencilAttachmentDesc? depthDescription = null;
        if (depth.HasValue && depth.Value.RenderingRegionIndex == region)
        {
            GraphDepthStencilAttachment attachment = depth.Value;
            DepthStencilView view = GetDepthStencilView(pass,
                new GraphDepthStencilViewId(attachment.View));
            depthDescription = new DepthStencilAttachmentDesc(
                view,
                attachment.DepthLoad,
                attachment.DepthStore,
                attachment.StencilLoad,
                attachment.StencilStore,
                attachment.ClearDepth,
                attachment.ClearStencil);
            ResolveAttachmentExtent(
                view.Description.Texture,
                view.Description.Range.FirstMipLevel,
                ref width,
                ref height);
        }

        DevelopmentCheck.Require(width != uint.MaxValue && height != uint.MaxValue,
            "A Raster rendering region has no attachment extent.");
        // Each recorded pass owns a complete Begin/EndRendering scope. Adjacent
        // attachments alone do not authorize suspension across command batches.
        RenderingOptions options = RenderingOptions.None;
        if (colorDescriptions.IsEmpty && depthDescription is null)
            options |= RenderingOptions.AllowUnorderedAccessWrites;
        if (region < 0)
        {
            _rasterRenderingCache[pass] = new RasterRenderingCacheEntry(
                true,
                depthDescription,
                width,
                height,
                options, renderRegion?.X ?? 0, renderRegion?.Y ?? 0);
        }
        return new RenderingDesc(colorDescriptions, depthDescription, width, height, options,
            renderRegion?.X ?? 0, renderRegion?.Y ?? 0);
    }

    private void InvalidateRasterRendering(int pass)
    {
        _commandChanges.Add(pass);
        if (pass < _rasterRenderingCache.Length) _rasterRenderingCache[pass] = default;
    }

    private static void ResolveAttachmentExtent(Texture texture, uint mip, ref uint width, ref uint height)
    {
        uint mipWidth = texture.Info.Width;
        uint mipHeight = texture.Info.Height;
        for (uint level = 0; level < mip; level++)
        {
            mipWidth = Math.Max(1, mipWidth >> 1);
            mipHeight = Math.Max(1, mipHeight >> 1);
        }
        width = Math.Min(width, mipWidth);
        height = Math.Min(height, mipHeight);
    }

    private readonly record struct RecordingBatch(
        List<int> Passes,
        int Start,
        int Count,
        int AccessCount,
        CommandContext Context);

    private readonly record struct RasterRenderingCacheEntry(
        bool Valid,
        DepthStencilAttachmentDesc? DepthStencil,
        uint Width,
        uint Height,
        RenderingOptions Options, uint X, uint Y);

    private readonly struct RecordBatchJob : IJobParallelFor
    {
        private readonly FrameExecutor _executor;
        private readonly RecordingBatch[] _batches;
        private readonly int _offset;

        internal RecordBatchJob(
            FrameExecutor executor,
            RecordingBatch[] batches,
            int offset)
        {
            _executor = executor;
            _batches = batches;
            _offset = offset;
        }

        public void Execute(int index) => _executor.RecordBatch(_batches[index + _offset]);
    }

    private readonly HashSet<int> _parameterPassChanges = [];
    private HashSet<ParameterConnection> _parameterValueChanges = [];
    private HashSet<ParameterConnection> _inactiveParameterChanges = [];
    private readonly Dictionary<int, HashSet<ParameterConnection>> _viewParameterConnections = [];
    private readonly Dictionary<(GraphAccessTargetKind Kind, int Index), HashSet<ParameterConnection>> _resourceParameterConnections = [];
    private readonly List<PassParameters> _passParameters = [];
    private HashSet<ParameterBlock> _parameterBindingChanges = [];
    private HashSet<ParameterBlock> _inactiveBindingChanges = [];

    private sealed class PassParameters
    {
        internal Pipeline? Pipeline;
        internal readonly List<ParameterBlock> Blocks = [];
        internal readonly List<ParameterConnection> Connections = [];
    }

    private sealed class ParameterBlock
    {
        internal int Pass;
        internal required ShaderVariableLayout Layout;
        internal PersistentParameterBindings? Bindings;
        internal ShaderVariableLayout? CachedLayout;
        internal readonly List<ParameterBindingData> BindingData = [];
        internal byte[]? Data;
        internal ResourceBinding[] Resources = [];
        internal int Count;
    }

    private sealed class ParameterBindingData
    {
        internal ResourceBinding[] Resources = [];
        internal byte[] Data = [];
        internal PersistentParameterBindings? Bindings;
    }

    private sealed class ParameterConnection
    {
        internal int Pass;
        internal ParameterBlock Block = null!;
        internal int Position;
        internal GraphParameterResourceBinding Source;
        internal int View = -1;
        internal int Primary = -1;
        internal int Secondary = -1;
        internal GraphAccessTargetKind Kind;
    }

    private void ConnectParameterBindings()
    {
        while (_passParameters.Count < _passes.Count) _passParameters.Add(new());
        for (int index = _passes.Count; index < _passParameters.Count; index++)
        {
            foreach (ParameterConnection connection in _passParameters[index].Connections)
                DisconnectParameter(connection);
            foreach (ParameterBlock block in _passParameters[index].Blocks)
                ReleaseParameterBlock(block);
        }
        if (_passParameters.Count > _passes.Count)
            _passParameters.RemoveRange(_passes.Count, _passParameters.Count - _passes.Count);

        foreach (int pass in _parameterPassChanges)
        {
            if (pass >= _passes.Count) continue;
            FramePass row = _passes[pass];
            PassParameters parameters = _passParameters[pass];
            if (!ReferenceEquals(parameters.Pipeline, row.Pipeline))
            {
                foreach (ParameterBlock value in parameters.Blocks) ReleaseParameterBlock(value);
                parameters.Pipeline = row.Pipeline;
            }
            foreach (ParameterConnection connection in parameters.Connections) DisconnectParameter(connection);
            int blockIndex = 0;
            int connectionIndex = 0;
            ParameterBlock block = GetParameterBlock(parameters, pass, blockIndex++, row.ParameterLayout, row.ParameterOrdinaryData);
            if (row.ParameterBindings is { } sources)
            {
                foreach (GraphParameterResourceBinding source in sources)
                {
                    if (source.BeginsParameterBlock)
                    {
                        if (block.Count != 0 || block.Data is { Length: > 0 })
                            block = GetParameterBlock(parameters, pass, blockIndex++, source.ParameterLayout, source.OrdinaryData);
                        else
                        {
                            block.Layout = source.ParameterLayout;
                            block.Data = source.OrdinaryData;
                        }
                        continue;
                    }
                    if (connectionIndex == parameters.Connections.Count) parameters.Connections.Add(new());
                    ParameterConnection connection = parameters.Connections[connectionIndex++];
                    connection.Pass = pass;
                    connection.Block = block;
                    connection.Position = block.Count++;
                    connection.Source = source;
                    connection.View = connection.Primary = connection.Secondary = -1;
                    EnsureCapacity(ref block.Resources, block.Count);
                    if (source.ParameterInput is not null)
                        AddParameterConnection(_parameterInputConnections, source.ParameterInput, connection);
                    else if (!source.IsNull && source.Type != ResourceBindingType.Sampler)
                    {
                        if (source.IsBufferRange || source.Type == ResourceBindingType.AccelerationStructure)
                        {
                            connection.Kind = GraphAccessTargetKind.Buffer;
                            connection.Primary = ResolveBuffer(source.BufferInput is null
                                ? source.Value : _frame.ResolveBufferInput(source.BufferInput).Buffer.Value);
                            if (source.BufferInput is not null)
                                AddParameterConnection(_bufferInputParameters, source.BufferInput, connection);
                        }
                        else if (source.Type == ResourceBindingType.TextureUav && source.SecondaryValue.IsValid)
                        {
                            connection.Kind = GraphAccessTargetKind.Texture;
                            connection.Primary = ResolveTexture(source.Value);
                            connection.Secondary = ResolveTexture(source.SecondaryValue);
                        }
                        else
                        {
                            bool found = TryResolveView(source.Value, out int view);
                            DevelopmentCheck.Require(found, "A parameter binding references an unavailable Graph View.");
                            connection.View = view;
                            AddParameterConnection(_viewParameterConnections, view, connection);
                        }
                        if (connection.Primary >= 0)
                            AddParameterConnection(_resourceParameterConnections, (connection.Kind, connection.Primary), connection);
                        if (connection.Secondary >= 0)
                            AddParameterConnection(_resourceParameterConnections, (connection.Kind, connection.Secondary), connection);
                    }
                    _parameterValueChanges.Add(connection);
                }
            }
            if (parameters.Connections.Count > connectionIndex)
                parameters.Connections.RemoveRange(connectionIndex, parameters.Connections.Count - connectionIndex);
            if (parameters.Blocks.Count > blockIndex)
            {
                for (int index = blockIndex; index < parameters.Blocks.Count; index++)
                    ReleaseParameterBlock(parameters.Blocks[index]);
                parameters.Blocks.RemoveRange(blockIndex, parameters.Blocks.Count - blockIndex);
            }
            foreach (ParameterBlock value in parameters.Blocks)
                value.Resources.AsSpan(value.Count).Clear();
        }
        _parameterPassChanges.Clear();
    }

    private ParameterBlock GetParameterBlock(PassParameters parameters, int pass, int index, ShaderVariableLayout layout, byte[]? data)
    {
        if (index == parameters.Blocks.Count) parameters.Blocks.Add(new() { Layout = layout });
        ParameterBlock block = parameters.Blocks[index];
        block.Pass = pass;
        block.Layout = layout;
        block.Data = data;
        block.Count = 0;
        _parameterBindingChanges.Add(block);
        return block;
    }

    private void ReleaseParameterBlock(ParameterBlock block)
    {
        ReleaseParameterBindingData(block);
        _parameterBindingChanges.Remove(block);
        _inactiveBindingChanges.Remove(block);
    }

    private static void ReleaseParameterBindingData(ParameterBlock block)
    {
        foreach (ParameterBindingData data in block.BindingData) data.Bindings?.Dispose();
        block.BindingData.Clear();
        block.Bindings = null;
    }

    private static void AddParameterConnection<TKey>(Dictionary<TKey, HashSet<ParameterConnection>> values, TKey key, ParameterConnection connection)
        where TKey : notnull
    {
        if (!values.TryGetValue(key, out HashSet<ParameterConnection>? connections))
            values.Add(key, connections = []);
        connections.Add(connection);
    }

    private static void RemoveParameterConnection<TKey>(Dictionary<TKey, HashSet<ParameterConnection>> values, TKey key, ParameterConnection connection)
        where TKey : notnull
    {
        if (!values.TryGetValue(key, out HashSet<ParameterConnection>? connections)) return;
        connections.Remove(connection);
        if (connections.Count == 0) values.Remove(key);
    }

    private void DisconnectParameter(ParameterConnection connection)
    {
        _parameterValueChanges.Remove(connection);
        if (connection.Source.ParameterInput is not null) RemoveParameterConnection(_parameterInputConnections, connection.Source.ParameterInput, connection);
        if (connection.Source.BufferInput is not null) RemoveParameterConnection(_bufferInputParameters, connection.Source.BufferInput, connection);
        if (connection.View >= 0) RemoveParameterConnection(_viewParameterConnections, connection.View, connection);
        if (connection.Primary >= 0) RemoveParameterConnection(_resourceParameterConnections, (connection.Kind, connection.Primary), connection);
        if (connection.Secondary >= 0) RemoveParameterConnection(_resourceParameterConnections, (connection.Kind, connection.Secondary), connection);
    }

    private void ChangeViewParameters(int view)
    {
        if (_viewParameterConnections.TryGetValue(view, out HashSet<ParameterConnection>? connections))
            _parameterValueChanges.UnionWith(connections);
    }

    private void MarkViewCommands(GraphAccessTargetKind kind, int resource)
    {
        if (resource >= 0 && _resourceAccessLists.TryGetValue((kind, resource), out List<int>? accesses))
            foreach (int access in accesses) _commandChanges.Add(_accesses[access].PassIndex);
    }

    private void ReconnectViewParameters(int view)
    {
        if (_viewParameterConnections.TryGetValue(view, out HashSet<ParameterConnection>? connections))
            foreach (ParameterConnection connection in connections) _parameterPassChanges.Add(connection.Pass);
    }

    private void ReconnectResourceParameters((GraphAccessTargetKind Kind, int Index) resource)
    {
        if (_resourceParameterConnections.TryGetValue(resource, out HashSet<ParameterConnection>? connections))
            foreach (ParameterConnection connection in connections) _parameterPassChanges.Add(connection.Pass);
    }

    private void ChangeResourceParameters((GraphAccessTargetKind Kind, int Index) resource)
    {
        if (_resourceParameterConnections.TryGetValue(resource, out HashSet<ParameterConnection>? connections))
            _parameterValueChanges.UnionWith(connections);
    }

    private void RefreshParameterBindings()
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.BindingResolution);
        RefreshBufferInputParameters();
        RefreshParameterInputs();
        _inactiveParameterChanges.Clear();
        foreach (ParameterConnection connection in _parameterValueChanges)
        {
            if (!_live[connection.Pass])
            {
                _inactiveParameterChanges.Add(connection);
                continue;
            }
            ResourceBinding value = ResolveParameterValue(connection);
            if (connection.Block.Resources[connection.Position] != value)
            {
                _commandChanges.Add(connection.Pass);
                connection.Block.Resources[connection.Position] = value;
                _parameterBindingChanges.Add(connection.Block);
            }
        }
        // Disabled passes can become live without changing their resource declarations.
        (_parameterValueChanges, _inactiveParameterChanges) = (_inactiveParameterChanges, _parameterValueChanges);
        _inactiveBindingChanges.Clear();
        foreach (ParameterBlock block in _parameterBindingChanges)
        {
            if (!_live[block.Pass])
            {
                _inactiveBindingChanges.Add(block);
                continue;
            }
            Pipeline? pipeline = _passParameters[block.Pass].Pipeline;
            if (pipeline is null || block.Count == 0)
            {
                ReleaseParameterBindingData(block);
                continue;
            }
            if (block.CachedLayout != block.Layout)
            {
                ReleaseParameterBindingData(block);
                block.CachedLayout = block.Layout;
            }
            var bindings = new ParameterBlockBindings(block.Layout, block.Resources.AsSpan(0, block.Count), block.Data ?? []);
            ParameterBindingData? matched = null;
            for (int index = block.BindingData.Count - 1; index >= 0; index--)
            {
                ParameterBindingData data = block.BindingData[index];
                if (!bindings.Resources.SequenceEqual(data.Resources) || !bindings.OrdinaryData.SequenceEqual(data.Data)) continue;
                matched = data;
                block.BindingData.RemoveAt(index);
                break;
            }
            if (matched is not null)
            {
                if (matched.Bindings is null)
                {
                    matched.Bindings = _frame.Backend.CreatePersistentParameterBindings(_frame.Graph.Device, pipeline, bindings);
                }
                block.Bindings = matched.Bindings;
                block.BindingData.Add(matched);
                continue;
            }
            // Frame resources and temporal histories can rotate at different rates.
            // Retain a bounded set of observed bindings, and materialize only reused values.
            ParameterBindingData next;
            if (block.BindingData.Count < _frame.Graph.FrameSlotCount + 1) next = new();
            else
            {
                next = block.BindingData[0];
                block.BindingData.RemoveAt(0);
                next.Bindings?.Dispose();
                next.Bindings = null;
            }
            PrepareArray(ref next.Resources, bindings.Resources.Length);
            PrepareArray(ref next.Data, bindings.OrdinaryData.Length);
            bindings.Resources.CopyTo(next.Resources);
            bindings.OrdinaryData.CopyTo(next.Data);
            block.BindingData.Add(next);
            block.Bindings = null;
            _inactiveBindingChanges.Add(block);
        }
        (_parameterBindingChanges, _inactiveBindingChanges) = (_inactiveBindingChanges, _parameterBindingChanges);
    }

    private ResourceBinding ResolveParameterValue(ParameterConnection connection)
    {
        GraphParameterResourceBinding source = connection.Source;
        if (source.ParameterInput is not null) return _frame.ResolveParameterInput(source.ParameterInput).Binding;
        if (source.IsNull) return new ResourceBinding(source.Type, null);
        if (source.Type == ResourceBindingType.Sampler) return new ResourceBinding(source.Type, source.Sampler);
        if (source.IsBufferRange)
            return ResourceBinding.ConstantBuffer(_buffers[connection.Primary].Resource!,
                source.BufferInput is null ? source.BufferRange : _frame.ResolveBufferInput(source.BufferInput).ResolveRange(source.BufferRange));
        if (source.Type == ResourceBindingType.ConstantBuffer)
        {
            FrameView view = _views[connection.View];
            return ResourceBinding.ConstantBuffer(_buffers[view.BufferIndex].Resource!, view.BufferRange);
        }
        if (source.Type == ResourceBindingType.AccelerationStructure)
        {
            ValidateAccelerationStructureParameter(connection);
            return new ResourceBinding(source.Type, source.AccelerationStructureSrv);
        }
        if (connection.Secondary >= 0)
        {
            ValidateSamplerFeedbackParameter(connection);
            return new ResourceBinding(source.Type, source.SamplerFeedbackUav);
        }
        DeviceResource? resource = _views[connection.View].View;
        DevelopmentCheck.Require(resource is not null, "A parameter binding Graph View was not materialized.");
        return new ResourceBinding(source.Type, resource);
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void ValidateAccelerationStructureParameter(ParameterConnection connection)
    {
        GraphParameterResourceBinding source = connection.Source;
        AccelerationStructureSrv? view = source.AccelerationStructureSrv;
        DevelopmentCheck.Require(view is not null, "An acceleration-structure parameter binding has no SRV.");
        Buffer storage = _buffers[connection.Primary].Resource!;
        DevelopmentCheck.Require(ReferenceEquals(storage, view.Resource.Info.Storage) &&
            GraphStructureIndex.ResolveRange(source.BufferRange, storage.Info.Size) == view.Resource.Info.StorageRange,
            "The acceleration-structure parameter binding no longer matches its Graph Buffer range.");
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void ValidateSamplerFeedbackParameter(ParameterConnection connection)
    {
        GraphParameterResourceBinding source = connection.Source;
        SamplerFeedbackUav? view = source.SamplerFeedbackUav;
        DevelopmentCheck.Require(view is not null, "A sampler-feedback parameter binding has no UAV.");
        DevelopmentCheck.Require(ReferenceEquals(_textures[connection.Primary].Resource, view.Description.Texture) &&
            ReferenceEquals(_textures[connection.Secondary].Resource, view.SampledTexture) &&
            source.TextureRange == view.Description.Range,
            "The sampler-feedback parameter binding no longer matches its Graph Textures or UAV range.");
    }

    private void SetParameterBindings(int pass, CommandContext context)
    {
        foreach (ParameterBlock block in _passParameters[pass].Blocks)
        {
            if (block.Count == 0 && block.Data is not { Length: > 0 }) continue;
            if (block.Bindings is null)
            {
                _frame.Backend.SetTransientParameterBindings(context,
                    new ParameterBlockBindings(block.Layout, block.Resources.AsSpan(0, block.Count), block.Data ?? []));
                continue;
            }
            _frame.Backend.SetPersistentParameterBindings(context, block.Bindings!);
        }
    }
}
