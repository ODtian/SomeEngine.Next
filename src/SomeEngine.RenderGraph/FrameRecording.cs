namespace SomeEngine.RenderGraph;

using System.Runtime.ExceptionServices;

internal sealed partial class FrameExecutor
{
    private static readonly Action<object?, int> RecordLatencyBatchAction = RecordLatencyBatch;
    private RecordedCommands[] _recorded = [];
    private bool[] _recordedValid = [];
    private QueueCompletion?[] _passCompletions = [];
    private bool[] _submittedQueues = [];
    private readonly List<QueueCompletion> _submittedCompletions = [];
    private int[] _waveByPass = [];
    private List<int>[] _waves = [];
    private int[] _parallelPasses = [];
    private RecordingBatch[] _parallelBatches = [];
    private readonly List<Queue> _batchQueues = [];
    private readonly Dictionary<Queue, List<int>> _batchPasses =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<QueueCompletion> _submitWaits = [];
    private RecordedCommands[] _submitCommands = [];
    private readonly List<SwapchainImage> _submitImages = [];
    private ColorAttachmentDesc[]?[] _renderingColorDescriptions = [];
    private RasterRenderingCacheEntry[] _rasterRenderingCache = [];

    internal ReadOnlySpan<QueueCompletion> SubmittedCompletions =>
        CollectionsMarshal.AsSpan(_submittedCompletions);

    private int RecordAndSubmit(Span<QueueCompletion> destination)
    {
        PrepareArray(ref _recorded, _passes.Length);
        PrepareArray(ref _recordedValid, _passes.Length);
        PrepareArray(ref _passCompletions, _passes.Length);
        PrepareArray(ref _renderingColorDescriptions, _passes.Length);
        PrepareArray(ref _rasterRenderingCache, _passes.Length);
        PrepareArray(ref _submittedQueues, _frame.Graph.MaximumQueueCompletionCount);
        Array.Clear(_recorded, 0, _passes.Length);
        Array.Clear(_recordedValid, 0, _passes.Length);
        Array.Clear(_passCompletions, 0, _passes.Length);
        Array.Clear(_submittedQueues, 0, _submittedQueues.Length);

        try
        {
            int waveCount;
            if (_reuseStableExecution)
            {
                waveCount = _stableWaveCount;
            }
            else
            {
                waveCount = BuildWaves();
                if (_stableExecutionEligible)
                    _stableWaveCount = waveCount;
            }
            if (_frame.Options.SubmissionMode == FrameSubmissionMode.RecordAllThenSubmit)
            {
                RecordPasses(_schedule);
                SubmitPendingQueueReleasesForAll(destination);
                for (int wave = 0; wave < waveCount; wave++)
                    SubmitWave(_waves[wave], destination);
            }
            else
            {
                for (int wave = 0; wave < waveCount; wave++)
                {
                    List<int> passes = _waves[wave];
                    ReadOnlySpan<int> passSpan = CollectionsMarshal.AsSpan(passes);
                    bool reuseSingleWaveBatches = _reuseStableExecution && waveCount == 1;
                    if (!reuseSingleWaveBatches)
                        BuildBatches(passes);
                    bool parallelRecording = reuseSingleWaveBatches
                        ? _stableSingleWaveParallelRecording
                        : IsParallelRecordingWorthwhile(passSpan);
                    if (_stableExecutionEligible && waveCount == 1 && !_reuseStableExecution)
                        _stableSingleWaveParallelRecording = parallelRecording;
                    bool directStableSubmit = _stableExecutionEligible &&
                        waveCount == 1 && _batchQueues.Count == 1;
                    if (!parallelRecording)
                    {
                        foreach (Queue queue in _batchQueues)
                            RecordBatch(_batchPasses[queue]);
                        SubmitPendingQueueReleasesForWave(passes, destination);
                        SubmitBatches(destination, directStableSubmit);
                    }
                    else
                    {
                        if (!TryRecordCoarseBatches())
                            RecordPasses(passSpan);
                        SubmitPendingQueueReleasesForWave(passes, destination);
                        SubmitBatches(destination, directStableSubmit);
                    }
                }
            }

            int count = 0;
            for (int queueSlot = 0; queueSlot < _submittedQueues.Length; queueSlot++)
            {
                if (!_submittedQueues[queueSlot]) continue;
                destination[count++] = destination[queueSlot];
            }
            return count;
        }
        catch
        {
            DisposeUnsubmitted();
            throw;
        }
    }

    private int BuildWaves()
    {
        if (_schedule.Length == 0) return 0;
        PrepareArray(ref _waveByPass, _passes.Length);
        Array.Clear(_waveByPass, 0, _passes.Length);
        int maximum = 0;
        foreach (int pass in _schedule)
        {
            int value = 0;
            Queue queue = _passes[pass].Queue!;
            foreach (int predecessor in _predecessors[pass])
            {
                if (!_live[predecessor] || ReferenceEquals(_passes[predecessor].Queue, queue))
                    continue;
                value = Math.Max(value, _waveByPass[predecessor] + 1);
            }
            foreach (int predecessor in _physicalPredecessors[pass])
            {
                if (!_live[predecessor] || ReferenceEquals(_passes[predecessor].Queue, queue))
                    continue;
                value = Math.Max(value, _waveByPass[predecessor] + 1);
            }
            _waveByPass[pass] = value;
            maximum = Math.Max(maximum, value);
        }

        int waveCount = maximum + 1;
        PrepareLists(ref _waves, waveCount);
        foreach (int pass in _schedule)
            _waves[_waveByPass[pass]].Add(pass);
        return waveCount;
    }
    private void RecordPasses(ReadOnlySpan<int> passes)
    {
        bool jobsEnabled =
            (_frame.Options.Debug & RenderGraphDebugOptions.DisableParallelRecording) == 0;
        int parallelCount = 0;
        ulong parallelWeight = 0;
        foreach (int pass in passes)
        {
            if (_passes[pass].Options.Recording == PassRecordingMode.CallingThread) continue;
            parallelCount++;
            parallelWeight = checked(parallelWeight + RecordingWeight(pass));
        }

        if (parallelCount == 0)
        {
            foreach (int pass in passes) RecordPass(pass);
            return;
        }
        if (_parallelPasses.Length < parallelCount)
            Array.Resize(ref _parallelPasses, parallelCount);
        int destination = 0;
        foreach (int pass in passes)
            if (_passes[pass].Options.Recording == PassRecordingMode.WorkerEligible)
                _parallelPasses[destination++] = pass;

        bool parallel = jobsEnabled && parallelCount > 1 &&
            parallelWeight >= checked((ulong)parallelCount * 8);
        if (!parallel)
        {
            foreach (int pass in passes) RecordPass(pass);
            return;
        }

        var job = new RecordPassJob(this, _parallelPasses);
        JobHandle handle = JobSystem.ScheduleParallel(job, parallelCount, 1);
        Exception? callingThreadFailure = null;
        try
        {
            foreach (int pass in passes)
                if (_passes[pass].Options.Recording == PassRecordingMode.CallingThread)
                    RecordPass(pass);
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
            // The first calling-thread failure remains authoritative, but workers must
            // still be joined before command storage is cleaned up.
        }

        if (callingThreadFailure is not null)
            ExceptionDispatchInfo.Capture(callingThreadFailure).Throw();
    }

    private bool IsParallelRecordingWorthwhile(ReadOnlySpan<int> passes)
    {
        if ((_frame.Options.Debug & RenderGraphDebugOptions.DisableParallelRecording) != 0)
            return false;
        int count = 0;
        ulong weight = 0;
        foreach (int pass in passes)
        {
            if (_passes[pass].Options.Recording != PassRecordingMode.WorkerEligible)
                continue;
            count++;
            weight = checked(weight + RecordingWeight(pass));
        }
        return count > 1 && weight >= checked((ulong)count * 8);
    }

    private ulong RecordingWeight(int pass)
    {
        ulong weight = Math.Max(_passes[pass].Options.EstimatedRecordingCost, 1);
        int aliasCount = _aliasBeforeResources[pass].Count;
        int beforeCount = checked(
            _acquires[pass].Count +
            _beforeBufferBarriers[pass].Count +
            _beforeTextureBarriers[pass].Count);
        int afterCount = checked(
            _afterBufferBarriers[pass].Count +
            _afterTextureBarriers[pass].Count +
            _releases[pass].Count);
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
        return checked(
            weight +
            (ulong)(aliasCount + beforeCount + afterCount) +
            (ulong)barrierCalls * 4);
    }

    private void RecordPass(int pass)
    {
        GraphPassKind kind = _passes[pass].Kind;
        bool bundle = false;
        using CommandContextPool.CommandContextLease lease =
            _frame.Graph.CommandContexts.Acquire(_passes[pass].Queue!, bundle, _passes[pass].Label);
        CommandContext context = lease.Context;
        bool begun = false;
        try
        {
            bool shaderVisible = _passes[pass].Queue!.Type != QueueType.Copy;
            _frame.Backend.Begin(context, new CommandRecordingDesc(
                shaderVisible
                    ? checked((uint)Math.Max(_passAccesses[pass].Count * 2, 8))
                    : 0,
                shaderVisible ? 8u : 0u,
                checked((uint)Math.Max(_passAccesses[pass].Count * 2, 8)),
                _passes[pass].Label));
            begun = true;
            RecordPassCommands(pass, kind, context);
            RecordedCommands recorded = _frame.Backend.End(context);
            begun = false;
            _recorded[pass] = recorded;
            _recordedValid[pass] = true;
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

    private void RecordBatch(List<int> passes)
        => RecordBatch(passes, 0, passes.Count, -1);

    private void RecordBatch(
        List<int> passes,
        int start,
        int count,
        int preparedAccessCount)
    {
        if (count == 0) return;
        Queue queue = _passes[passes[start]].Queue!;
        int accessCount = preparedAccessCount;
        int end = checked(start + count);
        if (accessCount < 0)
        {
            accessCount = 0;
            for (int item = start; item < end; item++)
            {
                int pass = passes[item];
                if (!ReferenceEquals(_passes[pass].Queue, queue))
                    throw new InvalidOperationException("A recording batch spans multiple Queues.");
                accessCount = checked(accessCount + _passAccesses[pass].Count);
            }
        }

        using CommandContextPool.CommandContextLease lease =
            _frame.Graph.CommandContexts.Acquire(queue, false, _passes[passes[start]].Label);
        CommandContext context = lease.Context;
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

    private bool TryRecordCoarseBatches()
    {
        int maximumUnitCount = Math.Min(Environment.ProcessorCount, 2);
        if (maximumUnitCount < 2)
            return false;
        int batchCount;
        if (_reuseStableExecution && _stableCoarseBatchCount != 0)
        {
            batchCount = _stableCoarseBatchCount;
        }
        else
        {
            foreach (Queue queue in _batchQueues)
            foreach (int pass in _batchPasses[queue])
                if (_passes[pass].Options.Recording != PassRecordingMode.WorkerEligible)
                    return false;
            batchCount = BuildCoarseRecordingBatches(maximumUnitCount);
            if (_stableExecutionEligible && _stableWaveCount == 1)
                _stableCoarseBatchCount = batchCount;
        }
        if (batchCount < 2)
            return false;

        if (batchCount == 2 && JobSystem.TryHandoffLatencyWork(
            this,
            RecordLatencyBatchAction,
            1,
            JobPriority.High,
            out long latencySequence))
            return RecordCallingBatchAndJoinLatencyWorker(latencySequence);

        var job = new RecordBatchJob(this, _parallelBatches, 1);
        JobHandle handle = JobSystem.ScheduleParallel(job, batchCount - 1, 1);
        Exception? callingThreadFailure = null;
        try
        {
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

    private bool RecordCallingBatchAndJoinLatencyWorker(long latencySequence)
    {
        Exception? callingThreadFailure = null;
        try
        {
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

    private int BuildCoarseRecordingBatches(int maximumUnitCount)
    {
        int required = checked(_batchQueues.Count * maximumUnitCount);
        if (_parallelBatches.Length < required)
            Array.Resize(ref _parallelBatches, required);
        int destination = 0;
        foreach (Queue queue in _batchQueues)
        {
            List<int> passes = _batchPasses[queue];
            int unitCount = Math.Min(maximumUnitCount, Math.Max(1, (passes.Count + 15) / 16));
            ulong remainingWeight = 0;
            foreach (int pass in passes)
            {
                remainingWeight = checked(remainingWeight + RecordingWeight(pass));
            }
            int start = 0;
            for (int unit = 0; unit < unitCount; unit++)
            {
                int remainingUnits = unitCount - unit;
                int maximumEnd = passes.Count - (remainingUnits - 1);
                ulong targetWeight = (remainingWeight + (ulong)remainingUnits - 1) /
                    (ulong)remainingUnits;
                int count = 0;
                ulong weight = 0;
                while (start + count < maximumEnd)
                {
                    ulong nextWeight = RecordingWeight(passes[start + count]);
                    if (count != 0 && weight < targetWeight &&
                        weight + nextWeight > targetWeight &&
                        targetWeight - weight <= weight + nextWeight - targetWeight)
                        break;
                    weight = checked(weight + nextWeight);
                    count++;
                    if (weight >= targetWeight)
                        break;
                }
                if (count == 0)
                {
                    weight = RecordingWeight(passes[start]);
                    count = 1;
                }
                int accessCount = 0;
                for (int item = start; item < start + count; item++)
                    accessCount = checked(accessCount + _passAccesses[passes[item]].Count);
                _parallelBatches[destination++] = new RecordingBatch(
                    passes,
                    start,
                    count,
                    accessCount);
                start += count;
                remainingWeight -= weight;
            }
        }
        return destination;
    }

    private void RecordBatch(in RecordingBatch batch) =>
        RecordBatch(batch.Passes, batch.Start, batch.Count, batch.AccessCount);

    private static void RecordLatencyBatch(object? state, int index)
    {
        FrameExecutor executor = (FrameExecutor)state!;
        executor.RecordBatch(executor._parallelBatches[index]);
    }

    private void RecordPassCommands(int pass, GraphPassKind kind, CommandContext context)
    {
        RecordBefore(pass, context);
        if (kind == GraphPassKind.Raster)
        {
            RenderingDesc description = BuildRasterRendering(pass);
            _frame.Backend.BeginRendering(context, description);
            InvokePass(pass, context);
            _frame.Backend.EndRendering(context);
        }
        else
        {
            InvokePass(pass, context);
        }
        RecordAfter(pass, context);
    }

    private void InvokePass(int pass, CommandContext context)
    {
        ref readonly FramePass row = ref _passes[pass];
        if (row.Pipeline is not null)
        {
            _frame.Backend.SetPipeline(context, row.Pipeline);
            if (row.ParameterLayout != VariableLayoutReflection.Null)
                SetParameterBindings(pass, context, row);
        }
        if (row.PersistentCallbacks is not null)
            row.PersistentCallbacks.Record(_frame, pass, context);
        else if (row.FrameCallbacks is not null)
            row.FrameCallbacks.Record(row.FrameCallbackIndex, _frame, pass, context);
        else
            throw new InvalidOperationException("The Pass has no command callback.");
    }

    private void SetParameterBindings(int pass, CommandContext context, in FramePass row)
    {
        List<GraphParameterResourceBinding>? source = row.ParameterBindings;
        int count = source?.Count ?? 0;
        ResourceBinding[] rented = ArrayPool<ResourceBinding>.Shared.Rent(Math.Max(count, 1));
        try
        {
            Span<ResourceBinding> bindings = rented.AsSpan(0, count);
            for (int i = 0; i < count; i++)
                bindings[i] = ResolveParameterBinding(pass, source![i]);
            ParameterBlockBindings block = new(
                row.ParameterLayout,
                bindings,
                row.ParameterOrdinaryData ?? []);
            _frame.Backend.SetTransientParameterBindings(context, block);
        }
        finally
        {
            Array.Clear(rented, 0, count);
            ArrayPool<ResourceBinding>.Shared.Return(rented);
        }
    }

    private ResourceBinding ResolveParameterBinding(
        int pass,
        in GraphParameterResourceBinding binding)
    {
        if (binding.Type == ResourceBindingType.Sampler)
            return ResourceBinding.SampledWith(binding.Sampler!);

        if (binding.Type == ResourceBindingType.AccelerationStructure)
        {
            AccelerationStructureSrv accelerationStructureView = binding.AccelerationStructureSrv ??
                throw new InvalidOperationException(
                    "An acceleration-structure parameter binding has no SRV.");
            Buffer storage = GetBuffer(pass, new GraphBufferId(binding.Value));
            BufferRange range = GraphStructureIndex.ResolveRange(
                binding.BufferRange,
                storage.Info.Size);
            if (!ReferenceEquals(storage, accelerationStructureView.Resource.Info.Storage) ||
                range != accelerationStructureView.Resource.Info.StorageRange)
            {
                throw new InvalidOperationException(
                    "The acceleration-structure parameter binding no longer matches its Graph Buffer range.");
            }
            return ResourceBinding.AccelerationStructure(accelerationStructureView);
        }

        if (binding.Type == ResourceBindingType.TextureUav &&
            binding.SecondaryValue.IsValid)
        {
            SamplerFeedbackUav feedbackView = binding.SamplerFeedbackUav ??
                throw new InvalidOperationException(
                    "A sampler-feedback parameter binding has no UAV.");
            Texture feedback = GetTexture(pass, new GraphTextureId(binding.Value));
            Texture sampled = GetTexture(pass, new GraphTextureId(binding.SecondaryValue));
            if (!ReferenceEquals(feedback, feedbackView.Description.Texture) ||
                !ReferenceEquals(sampled, feedbackView.SampledTexture) ||
                binding.TextureRange != feedbackView.Description.Range)
            {
                throw new InvalidOperationException(
                    "The sampler-feedback parameter binding no longer matches its Graph Textures or UAV range.");
            }
            return ResourceBinding.StorageTexture(feedbackView);
        }

        if (!_viewIndices.TryGetValue(binding.Value, out int index))
            throw new InvalidOperationException("A parameter binding references an unavailable Graph View.");
        DeviceResource view = _views[index].View ??
            throw new InvalidOperationException("A parameter binding Graph View was not materialized.");
        return binding.Type switch
        {
            ResourceBindingType.ConstantBuffer => ResourceBinding.ConstantBuffer((BufferCbv)view),
            ResourceBindingType.BufferSrv => ResourceBinding.ReadOnlyBuffer((BufferSrv)view),
            ResourceBindingType.BufferUav => ResourceBinding.WritableBuffer((BufferUav)view),
            ResourceBindingType.TextureSrv => ResourceBinding.SampledTexture((TextureSrv)view),
            ResourceBindingType.TextureUav => ResourceBinding.StorageTexture((TextureUav)view),
            _ => throw new InvalidOperationException("The parameter binding type is unsupported."),
        };
    }

    private void RecordBefore(int pass, CommandContext context)
    {
        List<AliasingResource> aliasBefore = _aliasBeforeResources[pass];
        if (aliasBefore.Count == 0)
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
        List<AliasingResource> aliasAfter = _aliasAfterResources[pass];
        if (aliasAfter.Count != aliasBefore.Count)
            throw new InvalidOperationException("An aliasing boundary is incomplete.");
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

    private void SubmitPendingQueueReleasesForAll(Span<QueueCompletion> destination)
    {
        foreach (PendingQueueRelease release in _lateReleases)
            SubmitPendingQueueRelease(release, destination);
    }

    private void SubmitPendingQueueReleasesForWave(List<int> passes, Span<QueueCompletion> destination)
    {
        foreach (PendingQueueRelease release in _lateReleases)
            if (passes.Contains(release.ConsumerPass))
                SubmitPendingQueueRelease(release, destination);
    }

    private void SubmitPendingQueueRelease(in PendingQueueRelease release, Span<QueueCompletion> destination)
    {
        using CommandContextPool.CommandContextLease lease =
            _frame.Graph.CommandContexts.Acquire(release.SourceQueue, false, "RenderGraph late release");
        CommandContext context = lease.Context;
        _frame.Backend.Begin(context, new CommandRecordingDesc(Label: "RenderGraph late release"));
        _frame.Backend.Barrier(context, new QueueRelease(
            release.Resource,
            release.TextureRange,
            release.Sync,
            release.Access,
            release.Layout,
            release.DestinationQueue.Type));
        RecordedCommands commands = _frame.Backend.End(context);
        if (_submitCommands.Length == 0)
            Array.Resize(ref _submitCommands, 1);
        _submitCommands[0] = commands;
        try
        {
            QueueCompletion completion = _frame.Backend.Submit(
                release.SourceQueue,
                new QueueSubmitDesc([], [], _submitCommands.AsSpan(0, 1), [], []));
            AddCompletion(_completionWaits[release.ConsumerPass], completion);
            int queueSlot = QueueSlot(release.SourceQueue);
            destination[queueSlot] = completion;
            _submittedQueues[queueSlot] = true;
            AddCompletion(_submittedCompletions, completion);
        }
        finally
        {
            _submitCommands[0] = default;
            commands.Dispose();
        }
    }

    private void SubmitWave(List<int> passes, Span<QueueCompletion> destination)
    {
        BuildBatches(passes);
        SubmitBatches(destination);
    }

    private void BuildBatches(List<int> passes)
    {
        _batchQueues.Clear();
        foreach (List<int> batch in _batchPasses.Values)
            batch.Clear();

        foreach (int pass in passes)
        {
            Queue queue = _passes[pass].Queue!;
            if (!_batchPasses.TryGetValue(queue, out List<int>? batch))
            {
                batch = [];
                _batchPasses.Add(queue, batch);
            }
            if (batch.Count == 0)
                _batchQueues.Add(queue);
            batch.Add(pass);
        }
    }

    private void SubmitBatches(
        Span<QueueCompletion> destination,
        bool directStableSubmit = false)
    {
        foreach (Queue queue in _batchQueues)
            SubmitBatch(queue, _batchPasses[queue], destination, directStableSubmit);
    }

    private void SubmitBatch(
        Queue queue,
        List<int> passes,
        Span<QueueCompletion> destination,
        bool directStableSubmit)
    {
        _submitWaits.Clear();
        if (_submitCommands.Length < passes.Count)
            Array.Resize(ref _submitCommands, passes.Count);

        int commandCount = 0;
        for (int item = 0; item < passes.Count; item++)
        {
            int pass = passes[item];
            if (_recordedValid[pass])
                _submitCommands[commandCount++] = _recorded[pass];
            if (directStableSubmit)
                continue;
            foreach (QueueCompletion wait in _completionWaits[pass])
                AddCompletion(_submitWaits, wait);
            foreach (int predecessor in _predecessors[pass])
            {
                if (!_live[predecessor] || !_passCompletions[predecessor].HasValue ||
                    ReferenceEquals(_passes[predecessor].Queue, queue))
                    continue;
                AddCompletion(_submitWaits, _passCompletions[predecessor]!.Value);
            }
            foreach (int predecessor in _physicalPredecessors[pass])
            {
                if (!_live[predecessor] || !_passCompletions[predecessor].HasValue ||
                    ReferenceEquals(_passes[predecessor].Queue, queue))
                    continue;
                AddCompletion(_submitWaits, _passCompletions[predecessor]!.Value);
            }
        }
        if (commandCount == 0)
            throw new InvalidOperationException("The recording batch has no executable RecordedCommands.");

        if (directStableSubmit)
            _submitImages.Clear();
        else
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
                if (!directStableSubmit)
                {
                    _passCompletions[pass] = completion;
                }
                if (!_stableExecutionEligible || !_reuseStableExecution)
                    CommitPassState(pass, completion);
            }
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
                if (_textures[texture].LastUse != _scheduledPosition[pass] ||
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
        throw new InvalidOperationException("The Queue is not registered with the RenderGraph.");
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
        if (region.Value.Owner != passIdentity.Owner ||
            region.Value.Slot != expectedOrdinal ||
            region.Value.Generation != checked((uint)passIdentity.Slot + 1))
            throw new InvalidOperationException("The Raw rendering region is invalid or out of order.");
        RenderingDesc description = BuildGeneralRendering(pass, expectedOrdinal);
        _frame.Backend.BeginRendering(context, description);
    }

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
        if (region < 0 && _reuseStableExecution)
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
            if (region >= 0) renderRegion = row.Definition.RenderingRegions[region];
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
        uint width = renderRegion?.Width ?? uint.MaxValue;
        uint height = renderRegion?.Height ?? uint.MaxValue;
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
        if (colorDestination != selectedColorCount)
            throw new InvalidOperationException("A rendering region has an invalid Color Attachment slot.");

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

        if (width == uint.MaxValue || height == uint.MaxValue)
            throw new InvalidOperationException("A Raster rendering region has no attachment extent.");
        RenderingOptions options = region < 0 ? ResolveRasterOptions(pass) : RenderingOptions.None;
        if (region < 0 && _stableExecutionEligible)
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

    private void InvalidateRasterRenderingCache()
    {
        if (_rasterRenderingCache.Length != 0)
            Array.Clear(_rasterRenderingCache);
    }

    private RenderingOptions ResolveRasterOptions(int pass)
    {
        if ((_frame.Options.Debug & RenderGraphDebugOptions.DisableRasterMerging) != 0 ||
            _passes[pass].Options.RasterMerging == RasterPassMergeMode.Isolated)
            return RenderingOptions.None;
        RenderingOptions options = RenderingOptions.None;
        int position = _scheduledPosition[pass];
        if (position > 0 && CompatibleRaster(_schedule[position - 1], pass))
            options |= RenderingOptions.Resuming;
        if (position + 1 < _schedule.Length && CompatibleRaster(pass, _schedule[position + 1]))
            options |= RenderingOptions.Suspending;
        return options;
    }

    private bool CompatibleRaster(int first, int second)
    {
        if (_passes[first].Kind != GraphPassKind.Raster || _passes[second].Kind != GraphPassKind.Raster ||
            !ReferenceEquals(_passes[first].Queue, _passes[second].Queue) ||
            _passes[first].Options.RasterMerging == RasterPassMergeMode.Isolated ||
            _passes[second].Options.RasterMerging == RasterPassMergeMode.Isolated)
            return false;
        GraphPass? a = _passes[first].Definition;
        GraphPass? b = _passes[second].Definition;
        if (a is null || b is null) return false;
        if (a.ColorAttachments.Count != b.ColorAttachments.Count ||
            a.DepthStencilAttachment?.View != b.DepthStencilAttachment?.View)
            return false;
        for (int i = 0; i < a.ColorAttachments.Count; i++)
            if (a.ColorAttachments[i].View != b.ColorAttachments[i].View) return false;
        return true;
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

    private readonly struct RecordPassJob : IJobParallelFor
    {
        private readonly FrameExecutor _pipeline;
        private readonly int[] _passes;
        internal RecordPassJob(FrameExecutor pipeline, int[] passes)
        {
            _pipeline = pipeline;
            _passes = passes;
        }
        public void Execute(int index) => _pipeline.RecordPass(_passes[index]);
    }

    private readonly record struct RecordingBatch(
        List<int> Passes,
        int Start,
        int Count,
        int AccessCount);

    private readonly record struct RasterRenderingCacheEntry(
        bool Valid,
        DepthStencilAttachmentDesc? DepthStencil,
        uint Width,
        uint Height,
        RenderingOptions Options);

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

}

