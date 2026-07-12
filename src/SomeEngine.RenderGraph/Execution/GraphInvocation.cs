namespace SomeEngine.RenderGraph;

using System.Runtime.ExceptionServices;

internal sealed class GraphInvocation
{
    private readonly IDevice _device;
    private readonly CompiledGraph _compiled;
    private readonly CompiledGraphLease _compiledLease;
    private readonly HeapHandle[] _heaps;
    private readonly ICommandContext?[] _contexts;
    private readonly CommandListHandle[] _commands;
    private readonly GpuCompletion[] _batchCompletions;
    private readonly GpuCompletion[][] _externalWaits;
    private readonly Capture? _capture;

    private GraphInvocation(
        IDevice device,
        FrozenGraph frozen,
        CompiledGraphLease compiledLease,
        Capture? capture)
    {
        _device = device;
        Frozen = frozen;
        _compiledLease = compiledLease;
        _compiled = compiledLease.Graph;
        Buffers = new BufferHandle[frozen.Resources.Length];
        Textures = new TextureHandle[frozen.Resources.Length];
        BufferViews = new BufferViewHandle[frozen.BufferViews.Length];
        TextureViews = new TextureViewHandle[frozen.TextureViews.Length];
        _heaps = new HeapHandle[_compiled.Heaps.Length];
        _contexts = new ICommandContext?[_compiled.RecordUnits.Length];
        _commands = new CommandListHandle[_compiled.RecordUnits.Length];
        _batchCompletions = new GpuCompletion[_compiled.ExecutionBatches.Length];
        _externalWaits = BuildExternalWaits(frozen, _compiled);
        _capture = capture;
    }

    public FrozenGraph Frozen { get; }
    public BufferHandle[] Buffers { get; }
    public TextureHandle[] Textures { get; }
    public BufferViewHandle[] BufferViews { get; }
    public TextureViewHandle[] TextureViews { get; }
    internal DeviceDomain Domain => _device.Domain;
    internal Capture? Capture => _capture;

    public static GraphInvocation Realize(
        IDevice device,
        FrozenGraph frozen,
        CompiledGraphLease compiledLease,
        Capture? capture = null)
    {
        ArgumentNullException.ThrowIfNull(compiledLease);
        GraphInvocation? invocation = null;
        Exception? failure = null;
        try
        {
            invocation = new GraphInvocation(device, frozen, compiledLease, capture);
            invocation.RealizeResourcesAndViews();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (failure is not null)
        {
            if (invocation is not null) failure = invocation.DestroyTransients(failure);
            failure = AttemptCleanup(failure, compiledLease.Release);
            ExceptionDispatchInfo.Capture(failure!).Throw();
        }
        return invocation!;
    }

    public GpuCompletion[] RecordAndSubmit()
    {
        GpuCompletion[]? result = null;
        Exception? failure = null;
        try
        {
            LeaseCommandContexts();
            RecordInParallel();
            for (int batch = 0; batch < _compiled.ExecutionBatches.Length; batch++) SubmitBatch(batch);
            result = CollectCompletions();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        failure = DiscardUnsubmittedCommands(failure);
        failure = DisposeUnclaimedContexts(failure);
        failure = DestroyTransients(failure);
        GpuCompletion[] published = result ?? CollectCompletions();
        failure = AttemptCleanup(failure, _compiledLease.Release);
        if (failure is not null)
        {
            if (published.Length != 0)
                throw new GraphSubmissionException(new GpuCompletionSet(published), failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return result ?? [];
    }

    private void RealizeResourcesAndViews()
    {
        for (int heap = 0; heap < _compiled.Heaps.Length; heap++)
        {
            CompiledHeap value = _compiled.Heaps[heap];
            _heaps[heap] = _device.CreateHeap(new HeapDesc(value.Size, value.MemoryType, value.ResourceClass, $"rg-heap-{heap}"));
        }

        for (int resource = 0; resource < Frozen.Resources.Length; resource++)
        {
            if (!_compiled.LiveResources[resource]) continue;
            FrozenResource value = Frozen.Resources[resource];
            if (value.IsImported)
            {
                if (value.Kind == ResourceNodeKind.Buffer) Buffers[resource] = value.ImportedBuffer.Handle;
                else Textures[resource] = value.ImportedTexture.Handle;
                continue;
            }

            CompiledPlacement placement = _compiled.Placements[resource];
            if (!placement.IsPlaced) throw new InvalidOperationException("Transient resource has no physical placement.");
            if (value.Kind == ResourceNodeKind.Buffer)
                Buffers[resource] = _device.CreatePlacedBuffer(_heaps[placement.Heap], placement.Offset, value.BufferDesc);
            else
                Textures[resource] = _device.CreatePlacedTexture(_heaps[placement.Heap], placement.Offset, value.TextureDesc);
        }

        for (int view = 0; view < Frozen.BufferViews.Length; view++)
        {
            if (!_compiled.LiveBufferViews[view]) continue;
            FrozenBufferView value = Frozen.BufferViews[view];
            BufferViews[view] = _device.CreateBufferView(new BufferViewDesc(
                Buffers[value.Resource],
                value.Range,
                value.Kind,
                value.Format,
                value.Stride,
                value.Name));
        }

        for (int view = 0; view < Frozen.TextureViews.Length; view++)
        {
            if (!_compiled.LiveTextureViews[view]) continue;
            FrozenTextureView value = Frozen.TextureViews[view];
            TextureViews[view] = _device.CreateTextureView(new TextureViewDesc(
                Textures[value.Resource],
                value.Range,
                value.Usage,
                value.Format,
                value.Name,
                value.Dimension));
        }
    }

    private void LeaseCommandContexts()
    {
        for (int unit = 0; unit < _compiled.RecordUnits.Length; unit++)
        {
            CompiledRecordUnit recordUnit = _compiled.RecordUnits[unit];
            string name = recordUnit.LogicalPassOrdinals.Length == 1
                ? Frozen.Passes[recordUnit.LogicalPassOrdinals[0]].Name
                : $"rg-record-unit-{unit}";
            _contexts[unit] = _device.AcquireCommandContext(recordUnit.Queue, name);
        }
    }

    private void RecordInParallel()
    {
        List<JobHandle> handles = new(_compiled.RecordUnits.Length);
        Exception? failure = null;
        try
        {
            for (int unit = 0; unit < _compiled.RecordUnits.Length; unit++)
            {
                if (RequiresCoordinator(_compiled.RecordUnits[unit])) continue;
                handles.Add(JobSystem.Schedule(new RecordJob(this, unit)));
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (failure is null)
        {
            try
            {
                for (int unit = 0; unit < _compiled.RecordUnits.Length; unit++)
                {
                    if (RequiresCoordinator(_compiled.RecordUnits[unit])) RecordUnit(unit);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        try
        {
            JobSystem.CombineDependencies(handles.ToArray()).Complete();
        }
        catch (Exception exception)
        {
            failure = CombineFailure(failure, exception);
        }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private bool RequiresCoordinator(in CompiledRecordUnit unit) =>
        unit.LogicalPassOrdinals.Any(pass => Frozen.Passes[pass].RecordingLane == PassRecordingLane.Coordinator);

    private void RecordUnit(int unit)
    {
        ICommandContext commands = Interlocked.Exchange(ref _contexts[unit], null)
            ?? throw new InvalidOperationException($"Record unit {unit} has no leased command context.");
        using (commands)
        {
            CompiledRecordUnit recordUnit = _compiled.RecordUnits[unit];
            switch (recordUnit.Kind)
            {
                case CompiledRecordUnitKind.Standalone:
                    foreach (int pass in recordUnit.LogicalPassOrdinals) RecordLogicalPass(commands, pass);
                    break;
                case CompiledRecordUnitKind.RasterScope:
                    RecordRasterScope(commands, recordUnit.LogicalPassOrdinals);
                    break;
                case CompiledRecordUnitKind.AliasAcquire:
                {
                    ResourceBarrier[] barriers = recordUnit.AliasAcquires
                        .Select(acquire => ResourceBarrier.Aliasing(
                            GetResource(acquire.BeforeResource),
                            GetResource(acquire.AfterResource)))
                        .ToArray();
                    commands.Barriers(barriers);
                    break;
                }
                case CompiledRecordUnitKind.InternalBarriers:
                {
                    ResourceBarrier[] barriers = Materialize(recordUnit.InternalBarriers);
                    commands.Barriers(barriers);
                    break;
                }
                default:
                    throw new NotSupportedException($"Record-unit kind {recordUnit.Kind} has no backend lowering.");
            }
            _commands[unit] = commands.Finish();
        }
    }

    private void RecordLogicalPass(ICommandContext commands, int pass)
    {
        ResourceBarrier[] before = Materialize(_compiled.BeforeBarriers[pass]);
        if (before.Length != 0) commands.Barriers(before);
        if (_compiled.Rendering[pass] is CompiledRendering rendering)
        {
            RenderingInfo info = MaterializeRenderingInfo(pass, rendering);
            commands.BeginRendering(info);
            try
            {
                ExecuteLogicalPass(commands, pass);
            }
            finally
            {
                commands.EndRendering();
            }
        }
        else
        {
            ExecuteLogicalPass(commands, pass);
        }
        ResourceBarrier[] after = Materialize(_compiled.AfterBarriers[pass]);
        if (after.Length != 0) commands.Barriers(after);
    }

    private void RecordRasterScope(ICommandContext commands, int[] passes)
    {
        int first = passes[0];
        int last = passes[^1];
        ResourceBarrier[] before = Materialize(_compiled.BeforeBarriers[first]);
        if (before.Length != 0) commands.Barriers(before);
        CompiledRendering rendering = _compiled.Rendering[first] ??
            throw new InvalidOperationException("A raster record unit contains a non-raster pass.");
        commands.BeginRendering(MaterializeRenderingInfo(first, rendering));
        try
        {
            for (int index = 0; index < passes.Length; index++)
            {
                int pass = passes[index];
                if (index != 0 && _compiled.BeforeBarriers[pass].Length != 0)
                    throw new InvalidOperationException("A merged raster boundary contains a before-barrier.");
                if (index != passes.Length - 1 && _compiled.AfterBarriers[pass].Length != 0)
                    throw new InvalidOperationException("A merged raster boundary contains an after-barrier.");
                ExecuteLogicalPass(commands, pass);
            }
        }
        finally
        {
            commands.EndRendering();
        }
        ResourceBarrier[] after = Materialize(_compiled.AfterBarriers[last]);
        if (after.Length != 0) commands.Barriers(after);
    }

    private void ExecuteLogicalPass(ICommandContext commands, int pass)
    {
        PassExecution execution = Frozen.Passes[pass].Execution ??
            throw new InvalidOperationException("Invocation pass has no executor.");
        execution(new GraphCommandContext(commands, this, pass), new PassResources(this, pass));
    }

    private void SubmitBatch(int batchOrdinal)
    {
        CompiledExecutionBatch batch = _compiled.ExecutionBatches[batchOrdinal];
        QueueType queue = batch.Queue;
        Dictionary<QueueType, ulong> waitValues = new();
        foreach (int predecessor in batch.Dependencies)
        {
            GpuCompletion completion = _batchCompletions[predecessor];
            if (!completion.IsValid || completion.Queue == queue) continue;
            waitValues[completion.Queue] = waitValues.TryGetValue(completion.Queue, out ulong current)
                ? Math.Max(current, completion.Value)
                : completion.Value;
        }
        foreach (GpuCompletion completion in _externalWaits[batchOrdinal])
        {
            if (completion.Queue == queue) continue;
            waitValues[completion.Queue] = waitValues.TryGetValue(completion.Queue, out ulong current)
                ? Math.Max(current, completion.Value)
                : completion.Value;
        }

        DeviceDomain domain = _device.Domain;
        GpuCompletion[] waits = waitValues.OrderBy(static pair => pair.Key)
            .Select(pair => new GpuCompletion(domain, pair.Key, pair.Value)).ToArray();
        CommandListHandle[] commands = new CommandListHandle[batch.RecordUnits.Length];
        for (int index = 0; index < batch.RecordUnits.Length; index++)
        {
            int recordUnit = batch.RecordUnits[index];
            commands[index] = _commands[recordUnit];
            if (!commands[index].IsValid)
                throw new InvalidOperationException($"Execution batch {batchOrdinal} contains an unfinished record unit.");
        }
        _batchCompletions[batchOrdinal] = _device.Submit(queue, commands, waits);
        foreach (int recordUnit in batch.RecordUnits) _commands[recordUnit] = default;
    }

    private GpuCompletion[] CollectCompletions()
    {
        Dictionary<QueueType, ulong> values = new();
        foreach (GpuCompletion completion in _batchCompletions)
        {
            if (!completion.IsValid) continue;
            values[completion.Queue] = values.TryGetValue(completion.Queue, out ulong current)
                ? Math.Max(current, completion.Value)
                : completion.Value;
        }
        DeviceDomain domain = _device.Domain;
        return values.OrderBy(static pair => pair.Key).Select(pair => new GpuCompletion(domain, pair.Key, pair.Value)).ToArray();
    }

    private static GpuCompletion[][] BuildExternalWaits(FrozenGraph graph, CompiledGraph compiled)
    {
        List<GpuCompletion>[] perBatch = Enumerable.Range(0, compiled.ExecutionBatches.Length)
            .Select(static _ => new List<GpuCompletion>())
            .ToArray();
        HashSet<(int Resource, QueueType Queue)> seenOnQueue = [];
        for (int batchOrdinal = 0; batchOrdinal < compiled.ExecutionBatches.Length; batchOrdinal++)
        {
            CompiledExecutionBatch batch = compiled.ExecutionBatches[batchOrdinal];
            foreach (int unit in batch.RecordUnits)
            foreach (int resource in EnumerateImportedWaitResources(graph, compiled.RecordUnits[unit]))
            {
                if (!seenOnQueue.Add((resource, batch.Queue))) continue;
                FrozenResource value = graph.Resources[resource];
                GpuCompletion[] readiness = value.Kind == ResourceNodeKind.Buffer
                    ? value.ImportedBuffer.Readiness ?? []
                    : value.ImportedTexture.Readiness ?? [];
                perBatch[batchOrdinal].AddRange(readiness);
            }
        }
        return perBatch.Select(static values => values.ToArray()).ToArray();
    }

    private static IEnumerable<int> EnumerateImportedWaitResources(
        FrozenGraph graph,
        CompiledRecordUnit unit)
    {
        foreach (int resource in unit.LogicalPassOrdinals
                     .SelectMany(pass => graph.Passes[pass].Accesses)
                     .Select(static access => access.Resource)
                     .Concat(unit.InternalBarriers.Select(static barrier => barrier.Resource))
                     .Distinct())
        {
            if (graph.Resources[resource].IsImported) yield return resource;
        }
    }

    private ResourceBarrier[] Materialize(BarrierTemplate[] templates)
    {
        ResourceBarrier[] barriers = new ResourceBarrier[templates.Length];
        for (int index = 0; index < templates.Length; index++)
        {
            BarrierTemplate template = templates[index];
            ResourceHandle resource = GetResource(template.Resource);
            barriers[index] = template.Kind switch
            {
                BarrierKind.Transition => ResourceBarrier.Transition(resource, template.Before, template.After, template.TextureRange),
                BarrierKind.UnorderedAccess => ResourceBarrier.UnorderedAccess(resource),
                BarrierKind.Aliasing => ResourceBarrier.Aliasing(GetResource(template.AliasingBefore), resource),
                _ => throw new ArgumentOutOfRangeException(nameof(templates)),
            };
        }
        return barriers;
    }

    private RenderingInfo MaterializeRenderingInfo(int pass, in CompiledRendering rendering)
    {
        FrozenColorAttachment[] frozen = Frozen.Passes[pass].ColorAttachments;
        ColorAttachment[] colors = new ColorAttachment[frozen.Length];
        for (int index = 0; index < colors.Length; index++)
        {
            FrozenColorAttachment attachment = frozen[index];
            colors[index] = new ColorAttachment(
                TextureViews[attachment.View],
                attachment.Load,
                StoreAction.Store,
                attachment.ClearColor);
        }
        DepthStencilAttachment? depthStencil = null;
        if (Frozen.Passes[pass].DepthStencilAttachment is FrozenDepthStencilAttachment frozenDepthStencil)
        {
            DepthAttachmentOperations? depth = frozenDepthStencil.Depth is DepthAttachmentOps depthOps
                ? new DepthAttachmentOperations(depthOps.Load, StoreAction.Store, depthOps.ReadOnly, depthOps.ClearValue)
                : null;
            StencilAttachmentOperations? stencil = frozenDepthStencil.Stencil is StencilAttachmentOps stencilOps
                ? new StencilAttachmentOperations(stencilOps.Load, StoreAction.Store, stencilOps.ReadOnly, stencilOps.ClearValue)
                : null;
            depthStencil = new DepthStencilAttachment(TextureViews[frozenDepthStencil.View], depth, stencil);
        }
        return new RenderingInfo(colors, depthStencil, rendering.Width, rendering.Height);
    }

    private ResourceHandle GetResource(int resource) => Frozen.Resources[resource].Kind == ResourceNodeKind.Buffer
        ? Buffers[resource].Resource
        : Textures[resource].Resource;

    private Exception? DiscardUnsubmittedCommands(Exception? failure)
    {
        for (int pass = 0; pass < _commands.Length; pass++)
        {
            if (!_commands[pass].IsValid) continue;
            CommandListHandle command = _commands[pass];
            _commands[pass] = default;
            failure = AttemptCleanup(failure, () => _device.DiscardCommandList(command));
        }
        return failure;
    }

    private Exception? DisposeUnclaimedContexts(Exception? failure)
    {
        for (int pass = 0; pass < _contexts.Length; pass++)
        {
            ICommandContext? context = Interlocked.Exchange(ref _contexts[pass], null);
            if (context is not null) failure = AttemptCleanup(failure, context.Dispose);
        }
        return failure;
    }

    private Exception? DestroyTransients(Exception? failure)
    {
        for (int view = TextureViews.Length - 1; view >= 0; view--)
        {
            if (!TextureViews[view].IsValid) continue;
            TextureViewHandle handle = TextureViews[view];
            TextureViews[view] = default;
            failure = AttemptCleanup(failure, () => _device.DestroyTextureView(handle));
        }
        for (int view = BufferViews.Length - 1; view >= 0; view--)
        {
            if (!BufferViews[view].IsValid) continue;
            BufferViewHandle handle = BufferViews[view];
            BufferViews[view] = default;
            failure = AttemptCleanup(failure, () => _device.DestroyBufferView(handle));
        }
        for (int resource = Frozen.Resources.Length - 1; resource >= 0; resource--)
        {
            FrozenResource value = Frozen.Resources[resource];
            if (value.IsImported) continue;
            if (value.Kind == ResourceNodeKind.Buffer && Buffers[resource].IsValid)
            {
                BufferHandle buffer = Buffers[resource];
                Buffers[resource] = default;
                failure = AttemptCleanup(failure, () => _device.DestroyBuffer(buffer));
            }
            else if (value.Kind == ResourceNodeKind.Texture && Textures[resource].IsValid)
            {
                TextureHandle texture = Textures[resource];
                Textures[resource] = default;
                failure = AttemptCleanup(failure, () => _device.DestroyTexture(texture));
            }
        }
        for (int heap = _heaps.Length - 1; heap >= 0; heap--)
        {
            if (!_heaps[heap].IsValid) continue;
            HeapHandle allocation = _heaps[heap];
            _heaps[heap] = default;
            failure = AttemptCleanup(failure, () => _device.DestroyHeap(allocation));
        }
        return failure;
    }

    private static Exception? AttemptCleanup(Exception? failure, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failure = CombineFailure(failure, exception);
        }
        return failure;
    }

    private static Exception CombineFailure(Exception? current, Exception next) => current is null
        ? next
        : new AggregateException("Render-graph invocation and cleanup both failed.", current, next);

    private readonly struct RecordJob : IJob
    {
        private readonly GraphInvocation _invocation;
        private readonly int _unit;
        public RecordJob(GraphInvocation invocation, int unit)
        {
            _invocation = invocation;
            _unit = unit;
        }
        public void Execute() => _invocation.RecordUnit(_unit);
    }
}
