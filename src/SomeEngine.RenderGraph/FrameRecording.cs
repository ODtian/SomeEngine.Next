namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private RecordedCommands[] _recorded = [];
    private bool[] _recordedValid = [];
    private QueueCompletion?[] _passCompletions = [];
    private bool[] _submittedQueues = [];
    private readonly List<QueueCompletion> _submittedCompletions = [];
    private int[][] _waveSubmissions = [];
    private RecordingBatch[] _recordingBatches = [];
    private (int Start, int Count)[] _submissionRecordings = [];
    private JobHandle[] _recordingJobs = [];
    private readonly List<CommandContext> _recordingContexts = [];
    private readonly List<QueueCompletion> _submitWaits = [];
    private RecordedCommands[] _submitCommands = [];
    private readonly List<SwapchainImage> _submitImages = [];
    private ColorAttachmentDesc[]?[] _renderingColorDescriptions = [];
    private RasterRenderingCacheEntry[] _rasterRenderingCache = [];

    internal ReadOnlySpan<QueueCompletion> SubmittedCompletions =>
        CollectionsMarshal.AsSpan(_submittedCompletions);

    private int RecordAndSubmit(Span<QueueCompletion> destination)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.RecordSubmit);
        RefreshCompletionWaits();
        int workerCount = JobSystem.WorkerCount;
        _reuseRecording = !_recordingChanged && _recordingWorkerCount == workerCount;
        PrepareArray(ref _recorded, _passes.Count);
        PrepareArray(ref _recordedValid, _passes.Count);
        PrepareArray(ref _passCompletions, _passes.Count);
        PrepareArray(ref _renderingColorDescriptions, _passes.Count);
        PrepareArray(ref _rasterRenderingCache, _passes.Count);
        PrepareArray(ref _submittedQueues, _frame.Graph.MaximumQueueCompletionCount);
        _submittedCompletions.EnsureCapacity(_frame.Graph.MaximumQueueCompletionCount);
        Array.Clear(_recorded, 0, _passes.Count);
        Array.Clear(_recordedValid, 0, _passes.Count);
        Array.Clear(_passCompletions, 0, _passes.Count);
        Array.Clear(_submittedQueues, 0, _submittedQueues.Length);

        try
        {
            int waveCount;
            if (_reuseRecording)
            {
                waveCount = _waveCount;
            }
            else
            {
                waveCount = BuildWaves();
                _waveCount = waveCount;
                BuildRecordingGroups(waveCount);
                _recordingWorkerCount = workerCount;
            }
            PrepareStateUpdates();
            StartRecordingJobs();
            if (_frame.Options.SubmissionMode == FrameSubmissionMode.RecordAllThenSubmit)
            {
                for (int wave = 0; wave < waveCount; wave++)
                    CompleteWaveRecording(wave);
                SubmitLateReleases(destination);
                for (int wave = 0; wave < waveCount; wave++)
                    SubmitWave(wave, destination);
            }
            else
            {
                SubmitLateReleases(destination);
                for (int wave = 0; wave < waveCount; wave++)
                {
                    foreach (int submission in _waveSubmissions[wave])
                    {
                        CompleteSubmissionRecording(submission);
                        List<int> passes = _submissionPasses[submission];
                        SubmitBatch(_passes[passes[0]].Queue!, passes, destination);
                    }
                }
            }

            _recordingChanged = false;
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
            // Recording jobs may still hold frame data and command contexts after a
            // different batch fails. Drain them before releasing unsubmitted commands.
            foreach (JobHandle job in _recordingJobs)
            {
                try { job.Complete(); }
                catch { }
            }
            Array.Clear(_recordingJobs);
            DisposeUnsubmitted();
            throw;
        }
    }

    private int _recordingWorkerCount = -1;

    private int _submissionCount;
    private List<int>[] _submissionPasses = [];
    private List<int>[] _submissionDependencies = [];
    private int[] _submissionByPass = [];
    private int[] _submissionWaves = [];
    private readonly Dictionary<Queue, int> _lastSubmission = new(ReferenceEqualityComparer.Instance);
    private readonly List<int> _incomingSubmissions = [];
    private readonly List<int> _dependencySearch = [];
    private bool[] _dependencyVisited = [];

    private int BuildWaves()
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.BatchConstruction);
        _submissionCount = 0;
        if (_schedule.Length == 0) return 0;
        PrepareArray(ref _submissionByPass, _passes.Count);
        PrepareArray(ref _submissionWaves, _passes.Count);
        PrepareArray(ref _dependencyVisited, _passes.Count);
        PrepareLists(ref _submissionPasses, _passes.Count);
        PrepareLists(ref _submissionDependencies, _passes.Count);
        Array.Fill(_submissionByPass, -1);
        Array.Fill(_submissionWaves, -1);
        _lastSubmission.Clear();
        int count = 0;
        foreach (int pass in _schedule)
        {
            Queue queue = _passes[pass].Queue!;
            _incomingSubmissions.Clear();
            AddSubmissionDependencies(_predecessors[pass]);
            AddSubmissionDependencies(_physicalPredecessors[pass]);
            int previous = _lastSubmission.TryGetValue(queue, out int found) ? found : -1;
            int current = previous;
            bool split = current < 0;
            if (!split)
            {
                foreach (int dependency in _incomingSubmissions)
                {
                    if (dependency != current && SubmissionDependsOn(dependency, current, count))
                    {
                        split = true;
                        break;
                    }
                }
            }
            if (split)
            {
                current = count++;
                if (previous >= 0)
                    _submissionDependencies[current].Add(previous);
                _lastSubmission[queue] = current;
            }
            _submissionPasses[current].Add(pass);
            _submissionByPass[pass] = current;
            foreach (int dependency in _incomingSubmissions)
                if (dependency != current && !_submissionDependencies[current].Contains(dependency))
                    _submissionDependencies[current].Add(dependency);
        }

        _submissionCount = count;
        int remaining = count;
        int maximum = 0;
        while (remaining != 0)
        {
            bool progress = false;
            for (int submission = 0; submission < count; submission++)
            {
                if (_submissionWaves[submission] >= 0) continue;
                int wave = 0;
                bool ready = true;
                foreach (int dependency in _submissionDependencies[submission])
                {
                    int dependencyWave = _submissionWaves[dependency];
                    if (dependencyWave < 0)
                    {
                        ready = false;
                        break;
                    }
                    wave = Math.Max(wave, dependencyWave + 1);
                }
                if (!ready) continue;
                _submissionWaves[submission] = wave;
                maximum = Math.Max(maximum, wave);
                remaining--;
                progress = true;
            }
            if (!progress)
                throw new InvalidOperationException("Queue submission dependencies contain a cycle.");
        }

        int waveCount = maximum + 1;
        return waveCount;
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
        foreach (CommandContext context in _recordingContexts)
            context.Dispose();
        _recordingContexts.Clear();
    }

    private void BuildRecordingGroups(int waveCount)
    {
        ReleaseRecordingContexts();
        PrepareArray(ref _waveSubmissions, waveCount);
        PrepareArray(ref _submissionRecordings, _submissionCount);
        bool parallel = JobSystem.WorkerCount != 0 &&
            (_frame.Options.Debug & RenderGraphDebugOptions.DisableParallelRecording) == 0;
        List<RecordingBatch> batches = [];
        for (int wave = 0; wave < waveCount; wave++)
        {
            List<int> submissions = [];
            for (int submission = 0; submission < _submissionCount; submission++)
            {
                if (_submissionWaves[submission] != wave) continue;
                List<int> passes = _submissionPasses[submission];
                submissions.Add(submission);
                int firstBatch = batches.Count;
                int start = 0;
                while (start < passes.Count)
                {
                    bool workerEligible = parallel &&
                        _passes[passes[start]].Options.Recording == PassRecordingMode.WorkerEligible;
                    int end = start + 1;
                    while (end < passes.Count && (!parallel ||
                        (_passes[passes[end]].Options.Recording == PassRecordingMode.WorkerEligible) == workerEligible))
                        end++;
                    AddRecordingBatches(passes, start, end, workerEligible, batches);
                    start = end;
                }
                _submissionRecordings[submission] = (firstBatch, batches.Count - firstBatch);
            }
            _waveSubmissions[wave] = submissions.ToArray();
        }
        _recordingBatches = batches.ToArray();
        _recordingJobs = new JobHandle[_recordingBatches.Length];
    }

    private void AddRecordingBatches(List<int> passes, int start, int end, bool workerEligible,
        List<RecordingBatch> batches)
    {
        int count = end - start;
        int batchCount = workerEligible
            ? Math.Min(JobSystem.WorkerCount + 1, Math.Max(1, (count + 15) / 16))
            : 1;
        ulong remainingWeight = 0;
        for (int item = start; item < end; item++)
            remainingWeight = SaturatingAdd(remainingWeight, RecordingWeight(passes[item]));
        for (int batch = 0; batch < batchCount; batch++)
        {
            int remainingBatches = batchCount - batch;
            int maximumEnd = end - (remainingBatches - 1);
            ulong targetWeight = remainingWeight / (ulong)remainingBatches;
            int batchEnd = start;
            int accessCount = 0;
            ulong weight = 0;
            do
            {
                int pass = passes[batchEnd++];
                weight = SaturatingAdd(weight, RecordingWeight(pass));
                accessCount = checked(accessCount + _passAccesses[pass].Count);
            }
            while (batchEnd < maximumEnd && weight < targetWeight);
            // The caller records the first batch while workers prepare later batches.
            bool onWorker = workerEligible && batches.Count != 0;
            batches.Add(CreateRecordingBatch(passes, start, batchEnd - start, accessCount, onWorker));
            start = batchEnd;
            remainingWeight = remainingWeight > weight ? remainingWeight - weight : 0;
        }
    }

    private void StartRecordingJobs()
    {
        for (int index = 0; index < _recordingBatches.Length; index++)
        {
            if (!_recordingBatches[index].OnWorker) continue;
            var job = new RecordBatchJob(this, index);
            _recordingJobs[index] = JobSystem.Schedule(job, new JobScheduleOptions(JobPriority.High));
        }
    }

    private void CompleteWaveRecording(int wave)
    {
        foreach (int submission in _waveSubmissions[wave])
            CompleteSubmissionRecording(submission);
    }

    private void CompleteSubmissionRecording(int submission)
    {
        (int start, int count) = _submissionRecordings[submission];
        for (int index = start; index < start + count; index++)
        {
            if (_recordingBatches[index].OnWorker)
            {
                try { _recordingJobs[index].Complete(); }
                finally { _recordingJobs[index] = default; }
            }
            else RecordBatch(_recordingBatches[index]);
        }
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
        Queue queue = _passes[passes[start]].Queue!;
        int accessCount = preparedAccessCount;
        int end = checked(start + count);
        if (accessCount < 0)
        {
            accessCount = 0;
            for (int item = start; item < end; item++)
            {
                int pass = passes[item];
                DevelopmentCheck.Require(ReferenceEquals(_passes[pass].Queue, queue),
                    "A recording batch spans multiple Queues.");
                accessCount = checked(accessCount + _passAccesses[pass].Count);
            }
        }

        bool begun = false;
        try
        {
            bool shaderVisible = queue.Type != QueueType.Copy;
            _frame.Backend.Begin(context, new CommandRecordingDesc(
                shaderVisible ? checked((uint)Math.Max(accessCount * 2, 8)) : 0,
                shaderVisible ? 8u : 0u,
                checked((uint)Math.Max(accessCount * 2, 8)),
                _passes[passes[start]].Label));
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

    private RecordingBatch CreateRecordingBatch(List<int> passes, int start, int count, int accessCount, bool onWorker)
    {
        Queue queue = _passes[passes[start]].Queue!;
        CommandContext context = _frame.Backend.CreateCommandContext(
            _frame.Graph.Device,
            new CommandContextDesc(queue.Type, queue.Index, InitialSlotCount: 1,
                Label: _passes[passes[start]].Label));
        try
        {
            _recordingContexts.Add(context);
            return new RecordingBatch(passes, start, count, accessCount, context, onWorker);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private void RecordBatch(in RecordingBatch batch) =>
        RecordBatch(batch.Passes, batch.Start, batch.Count, batch.AccessCount, batch.Context);

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private void RecordPassCommands(int pass, GraphPassKind kind, CommandContext context)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.PassDispatch);
        RecordBefore(pass, context);
        if (kind == GraphPassKind.Raster)
        {
            RenderingDesc description = BuildRasterRendering(pass);
            _frame.Backend.BeginRendering(context, description);
            InvokePass(pass, context);
            _frame.Backend.EndRendering(context);
        }
        else InvokePass(pass, context);
        RecordAfter(pass, context);
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
        foreach (int submission in _waveSubmissions[wave])
        {
            List<int> passes = _submissionPasses[submission];
            SubmitBatch(_passes[passes[0]].Queue!, passes, destination);
        }
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
            foreach (int pass in passes) _passCompletions[pass] = completion;
            CommitSubmissionState(submission, completion);
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
        DevelopmentCheck.Require(region.Value.Owner == passIdentity.Owner &&
            region.Value.Slot == expectedOrdinal &&
            region.Value.Generation == checked((uint)passIdentity.Slot + 1),
            "The Raw rendering region is invalid or out of order.");
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
                    cached.Options);
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
                options);
        }
        return new RenderingDesc(colorDescriptions, depthDescription, width, height, options);
    }

    private void InvalidateRasterRendering(int pass)
    {
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
        CommandContext Context,
        bool OnWorker);

    private readonly record struct RasterRenderingCacheEntry(
        bool Valid,
        DepthStencilAttachmentDesc? DepthStencil,
        uint Width,
        uint Height,
        RenderingOptions Options);

    private readonly struct RecordBatchJob(FrameExecutor executor, int batch) : IJob
    {
        public void Execute() => executor.RecordBatch(executor._recordingBatches[batch]);
    }

    private readonly HashSet<int> _parameterPassChanges = [];
    private HashSet<ParameterConnection> _parameterValueChanges = [];
    private HashSet<ParameterConnection> _inactiveParameterChanges = [];
    private readonly Dictionary<int, HashSet<ParameterConnection>> _viewParameterConnections = [];
    private readonly Dictionary<(GraphAccessTargetKind Kind, int Index), HashSet<ParameterConnection>> _resourceParameterConnections = [];
    private readonly List<PassParameters> _passParameters = [];

    private sealed class PassParameters
    {
        internal readonly List<ParameterBlock> Blocks = [];
        internal readonly List<ParameterConnection> Connections = [];
    }

    private sealed class ParameterBlock
    {
        internal required ShaderVariableLayout Layout;
        internal bool Changed;
        internal Pipeline? Pipeline;
        internal PersistentParameterBindings? Bindings;
        internal byte[]? Data;
        internal ResourceBinding[] Resources = [];
        internal int Count;
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
            foreach (ParameterConnection connection in parameters.Connections) DisconnectParameter(connection);
            int blockIndex = 0;
            int connectionIndex = 0;
            ParameterBlock block = GetParameterBlock(parameters, blockIndex++, row.Pipeline, row.ParameterLayout, row.ParameterOrdinaryData);
            if (row.ParameterBindings is { } sources)
            {
                foreach (GraphParameterResourceBinding source in sources)
                {
                    if (source.BeginsParameterBlock)
                    {
                        if (block.Count != 0 || block.Data is { Length: > 0 })
                            block = GetParameterBlock(parameters, blockIndex++, row.Pipeline, source.ParameterLayout, source.OrdinaryData);
                        else
                        {
                            ChangeParameterBlock(block, row.Pipeline, source.ParameterLayout, source.OrdinaryData);
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

    private ParameterBlock GetParameterBlock(PassParameters parameters, int index,
        Pipeline? pipeline, ShaderVariableLayout layout, byte[]? data)
    {
        if (index == parameters.Blocks.Count) parameters.Blocks.Add(new() { Layout = layout });
        ParameterBlock block = parameters.Blocks[index];
        ChangeParameterBlock(block, pipeline, layout, data);
        block.Count = 0;
        return block;
    }

    private void ChangeParameterBlock(ParameterBlock block, Pipeline? pipeline,
        ShaderVariableLayout layout, byte[]? data)
    {
        if (!ReferenceEquals(block.Pipeline, pipeline) || block.Layout != layout)
        {
            block.Bindings?.Dispose();
            block.Bindings = null;
        }
        block.Pipeline = pipeline;
        block.Layout = layout;
        block.Data = data;
        block.Changed = true;
    }

    private void ReleaseParameterBlock(ParameterBlock block)
    {
        block.Bindings?.Dispose();
        block.Bindings = null;
    }

    internal void ReleaseParameterBindings()
    {
        foreach (PassParameters pass in _passParameters)
            foreach (ParameterBlock block in pass.Blocks)
                ReleaseParameterBlock(block);
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
                connection.Block.Resources[connection.Position] = value;
                connection.Block.Changed = true;
            }
        }
        // Disabled passes can become live without changing their resource declarations.
        (_parameterValueChanges, _inactiveParameterChanges) = (_inactiveParameterChanges, _parameterValueChanges);
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
            if (block.Changed)
            {
                var bindings = new ParameterBlockBindings(block.Layout,
                    block.Resources.AsSpan(0, block.Count), block.Data ?? []);
                if (block.Bindings is null)
                    block.Bindings = _frame.Backend.CreatePersistentParameterBindings(
                        _frame.Graph.Device, block.Pipeline!, bindings);
                else
                    _frame.Backend.UpdatePersistentParameterBindings(block.Bindings, bindings);
                block.Changed = false;
            }
            _frame.Backend.SetPersistentParameterBindings(context, block.Bindings!);
        }
    }
}

