namespace SomeEngine.RenderGraph;

using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

public sealed partial class RenderGraph
{
    private IGraphicsBackend _backend = null!;
    private Device _device = null!;
    private Heap[] _heaps = [];
    private CommandContext?[] _commandContexts = [];
    private RecordedCommands?[] _recordedCommands = [];
    private ColorAttachmentDesc[] _renderingColorScratch = [];
    private ArenaSlice<int> _commandRenderingColorOffsets;
    private RecordedCommands[] _batchCommandScratch = [];
    private SwapchainImage[] _batchSwapchainImageScratch = [];
    private readonly QueueCompletion[] _batchWaitScratch = new QueueCompletion[3];
    private QueueCompletion[] _batchCompletions = [];
    private QueueCompletion[] _resourceCompletions = [];
    private int[] _resourceCompletionCounts = [];
    private ArenaSlice<int> _bufferViewRepresentatives;
    private ArenaSlice<int> _textureViewRepresentatives;
    private ResourceBinding[] _materializedBindings = [];
    private bool _collectDetailedTimings;
    private long _recorderAcquisitionTicks;
    private long _barrierTicks;
    private long _renderingTicks;
    private long _callbackTicks;
    private long _finishTicks;
    private ArenaSlice<long> _passCallbackTicks;
    private int _materializedBufferCount;
    private int _materializedTextureCount;
    private int _materializedBufferViewCount;
    private int _materializedTextureViewCount;
    private int _accelerationStructureCount;
    private int _materializedBindingCount;

    private Buffer[] _materializedBuffers = [];
    private Texture[] _materializedTextures = [];
    private MaterializedBufferView?[] _materializedBufferViews = [];
    private MaterializedTextureView?[] _materializedTextureViews = [];
    private AccelerationStructure[] _materializedAccelerationStructures = [];

    internal Span<Buffer> MaterializedBuffers =>
        _materializedBuffers.AsSpan(0, _materializedBufferCount);
    internal Span<Texture> MaterializedTextures =>
        _materializedTextures.AsSpan(0, _materializedTextureCount);
    internal Span<MaterializedBufferView?> MaterializedBufferViews =>
        _materializedBufferViews.AsSpan(0, _materializedBufferViewCount);
    internal Span<MaterializedTextureView?> MaterializedTextureViews =>
        _materializedTextureViews.AsSpan(0, _materializedTextureViewCount);
    internal Span<AccelerationStructure> AccelerationStructures =>
        _materializedAccelerationStructures.AsSpan(0, _accelerationStructureCount);
    internal Device Device => _device;
    internal IGraphicsBackend Backend => _backend;

    private void InitializeExecution()
    {
        _materializedBufferCount = Buffers.Length;
        _materializedTextureCount = Textures.Length;
        _materializedBufferViewCount = MaterializedBufferViewCount == 0 ? 0 : BufferViewCount;
        _materializedTextureViewCount = MaterializedTextureViewCount == 0 ? 0 : TextureViewCount;
        _accelerationStructureCount =
            MaterializedAccelerationStructureCount == 0 ? 0 : AccelerationStructureCount;
        _materializedBindingCount = ShaderArgumentCount;

        _materializedBuffers = RentExecutionArray<Buffer>(_materializedBufferCount);
        _materializedTextures = RentExecutionArray<Texture>(_materializedTextureCount);
        _materializedBufferViews =
            RentExecutionArray<MaterializedBufferView?>(_materializedBufferViewCount);
        _materializedTextureViews =
            RentExecutionArray<MaterializedTextureView?>(_materializedTextureViewCount);
        _materializedAccelerationStructures =
            RentExecutionArray<AccelerationStructure>(_accelerationStructureCount);
        _materializedBindings = RentExecutionArray<ResourceBinding>(_materializedBindingCount);
        _heaps = RentExecutionArray<Heap>(Heaps.Length);

        _bufferViewRepresentatives =
            AllocateSlice<int>(_materializedBufferViewCount, clear: false);
        _bufferViewRepresentatives.Span.Fill(-1);
        _textureViewRepresentatives =
            AllocateSlice<int>(_materializedTextureViewCount, clear: false);
        _textureViewRepresentatives.Span.Fill(-1);

        int commandCount = CommandUnits.Count;
        _commandContexts = RentExecutionArray<CommandContext?>(commandCount);
        _recordedCommands = RentExecutionArray<RecordedCommands?>(commandCount);
        _commandRenderingColorOffsets = AllocateSlice<int>(commandCount + 1, clear: false);
        int colorCount = 0;
        for (int command = 0; command < commandCount; command++)
        {
            _commandRenderingColorOffsets[command] = colorCount;
            colorCount = checked(colorCount + GetRuntimeCmdRenderingColorCapacity(command));
        }
        _commandRenderingColorOffsets[commandCount] = colorCount;
        _renderingColorScratch = RentExecutionArray<ColorAttachmentDesc>(colorCount);

        int maximumBatchCommandCount = 0;
        foreach (CommandBatch batch in CommandBatches)
            maximumBatchCommandCount = Math.Max(maximumBatchCommandCount, batch.CommandUnitCount);
        _batchCommandScratch = RentExecutionArray<RecordedCommands>(maximumBatchCommandCount);
        _batchSwapchainImageScratch = RentExecutionArray<SwapchainImage>(_swapchainImages.Count);
        _batchCompletions = RentExecutionArray<QueueCompletion>(CommandBatches.Count);
        _resourceCompletions = RentExecutionArray<QueueCompletion>(checked(ResourceCount * 3));
        _resourceCompletionCounts = RentExecutionArray<int>(ResourceCount);
    }

    private static T[] RentExecutionArray<T>(int count)
    {
        if (count == 0) return [];
        T[] values = ArrayPool<T>.Shared.Rent(count);
        values.AsSpan(0, count).Clear();
        return values;
    }

    private static void ReturnExecutionArray<T>(ref T[] values)
    {
        if (values.Length == 0) return;
        ArrayPool<T>.Shared.Return(values, clearArray: true);
        values = [];
    }

    private void ReturnExecutionStorage()
    {
        ReturnExecutionArray(ref _materializedBuffers);
        ReturnExecutionArray(ref _materializedTextures);
        ReturnExecutionArray(ref _materializedBufferViews);
        ReturnExecutionArray(ref _materializedTextureViews);
        ReturnExecutionArray(ref _materializedAccelerationStructures);
        ReturnExecutionArray(ref _materializedBindings);
        ReturnExecutionArray(ref _heaps);
        ReturnExecutionArray(ref _commandContexts);
        ReturnExecutionArray(ref _recordedCommands);
        ReturnExecutionArray(ref _renderingColorScratch);
        ReturnExecutionArray(ref _batchCommandScratch);
        ReturnExecutionArray(ref _batchSwapchainImageScratch);
        ReturnExecutionArray(ref _batchCompletions);
        ReturnExecutionArray(ref _resourceCompletions);
        ReturnExecutionArray(ref _resourceCompletionCounts);
        _materializedBufferCount = 0;
        _materializedTextureCount = 0;
        _materializedBufferViewCount = 0;
        _materializedTextureViewCount = 0;
        _accelerationStructureCount = 0;
        _materializedBindingCount = 0;
    }

    internal bool TryGetResourceOrdinal(Resource resource, out int ordinal)
    {
        if (resource is null || !ReferenceEquals(resource.Device, _device))
        {
            ordinal = -1;
            return false;
        }
        for (int index = 0; index < Buffers.Length; index++)
        {
            if (!ReferenceEquals(MaterializedBuffers[index], resource)) continue;
            ordinal = index;
            return true;
        }
        for (int index = 0; index < Textures.Length; index++)
        {
            if (!ReferenceEquals(MaterializedTextures[index], resource)) continue;
            ordinal = GetTextureResourceOrdinal(index);
            return true;
        }
        ordinal = -1;
        return false;
    }

    internal void AcquireResourcesAndViews(
        bool collectTimings,
        out ResourceAcquisitionCpuTimings timings)
    {
        long started = collectTimings ? Stopwatch.GetTimestamp() : 0;
        try
        {
            InitializeExecution();
            long initialized = collectTimings ? Stopwatch.GetTimestamp() : 0;
            timings = AcquirePhysicalResourcesAndViews(collectTimings) with
            {
                Setup = collectTimings
                    ? Stopwatch.GetElapsedTime(started, initialized)
                    : default,
            };
        }
        catch
        {
            _ = DisposeExecutionObjects(null);
            throw;
        }
    }

    internal QueueCompletion[] EncodeAndSubmit() =>
        EncodeAndSubmit(collectTimings: false, collectDetailedTimings: false, out _);

    internal QueueCompletion[] EncodeAndSubmit(
        bool collectTimings,
        bool collectDetailedTimings,
        out CommandSubmissionCpuTimings timings)
    {
        long started = collectTimings ? Stopwatch.GetTimestamp() : 0;
        _collectDetailedTimings = collectDetailedTimings;
        _passCallbackTicks = collectDetailedTimings
            ? AllocateSlice<long>(Passes.Length)
            : default;
        Exception? failure = null;
        QueueCompletion[] result = [];

        try
        {
            AcquireCommandContexts();
            long acquired = collectTimings ? Stopwatch.GetTimestamp() : 0;
            EncodeInParallel();
            if (collectTimings) _recorderAcquisitionTicks = acquired - started;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        long encoded = collectTimings ? Stopwatch.GetTimestamp() : 0;

        if (failure is null)
        {
            try
            {
                for (int batch = 0; batch < CommandBatches.Count; batch++)
                    SubmitBatch(batch);
                result = CollectPublishedCompletions();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }
        long submitted = collectTimings ? Stopwatch.GetTimestamp() : 0;

        failure = DisposeUnsubmittedCommands(failure);
        failure = DisposeCommandContexts(failure);
        QueueCompletion[] published = result.Length == 0
            ? CollectPublishedCompletions()
            : result;
        failure = DisposeExecutionObjects(failure);
        long cleaned = collectTimings ? Stopwatch.GetTimestamp() : 0;

        timings = collectTimings
            ? new CommandSubmissionCpuTimings(
                Stopwatch.GetElapsedTime(started, encoded),
                Stopwatch.GetElapsedTime(encoded, submitted),
                Stopwatch.GetElapsedTime(submitted, cleaned))
            {
                RecorderAcquisition = Stopwatch.GetElapsedTime(0, _recorderAcquisitionTicks),
                Barrier = Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _barrierTicks)),
                Rendering = Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _renderingTicks)),
                Callbacks = Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _callbackTicks)),
                Close = Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _finishTicks)),
            }
            : default;

        if (failure is not null)
        {
            if (published.Length != 0)
                throw new RenderGraphExecutionException(published, failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return result;
    }

    internal QueueCompletion[] MaterializeBatchPositions() =>
        _batchCompletions.AsSpan(0, CommandBatches.Count).ToArray();

    private ResourceAcquisitionCpuTimings AcquirePhysicalResourcesAndViews(bool collectTimings)
    {
        long started = collectTimings ? Stopwatch.GetTimestamp() : 0;
        for (int heap = 0; heap < Heaps.Length; heap++)
        {
            GraphMemoryRequirements plan = Heaps[heap];
            _heaps[heap] = _backend.CreateHeap(
                _device,
                new HeapDesc(
                    plan.Size,
                    plan.Alignment,
                    plan.MemoryType,
                    plan.Flags,
                    Label: $"rg-heap-{heap}"));
        }
        long heapsCreated = collectTimings ? Stopwatch.GetTimestamp() : 0;

        for (int buffer = 0; buffer < Buffers.Length; buffer++)
        {
            if (!IsResourceLive(buffer)) continue;
            ResourceUnversionedData row = Buffers[buffer];
            if (row.IsImported)
            {
                MaterializedBuffers[buffer] = GetImportedBuffer(row);
            }
            else
            {
                int heap = RequirePlacementHeap(buffer);
                MaterializedBuffers[buffer] = _backend.CreatePlacedBuffer(
                    _device,
                    _heaps[heap],
                    PlacementOffsets[buffer],
                    GetBufferDescription(buffer));
            }
            InitializeUploadBuffer(buffer, row);
        }

        for (int texture = 0; texture < Textures.Length; texture++)
        {
            int resource = GetTextureResourceOrdinal(texture);
            if (!IsResourceLive(resource)) continue;
            ResourceUnversionedData row = Textures[texture];
            if (row.IsImported)
            {
                MaterializedTextures[texture] = GetImportedTexture(row);
            }
            else
            {
                int heap = RequirePlacementHeap(resource);
                TextureDesc description = GetTextureDescription(texture).ToRhiDescription();
                MaterializedTextures[texture] = _backend.CreatePlacedTexture(
                    _device,
                    _heaps[heap],
                    PlacementOffsets[resource],
                    description);
            }
        }
        long resourcesCreated = collectTimings ? Stopwatch.GetTimestamp() : 0;

        AcquireBufferViews();
        AcquireAccelerationStructures();
        AcquireTextureViews();
        MaterializeParameterBindings();
        long viewsCreated = collectTimings ? Stopwatch.GetTimestamp() : 0;

        return collectTimings
            ? new ResourceAcquisitionCpuTimings(
                default,
                Stopwatch.GetElapsedTime(started, heapsCreated),
                Stopwatch.GetElapsedTime(heapsCreated, resourcesCreated),
                Stopwatch.GetElapsedTime(resourcesCreated, viewsCreated),
                default)
            : default;
    }

    private int RequirePlacementHeap(int resource)
    {
        int heap = PlacementHeaps[resource];
        if ((uint)heap >= (uint)Heaps.Length)
            throw new InvalidOperationException("A live transient resource has no physical placement.");
        return heap;
    }

    private void InitializeUploadBuffer(int resource, in ResourceUnversionedData row)
    {
        ReadOnlySpan<byte> data = GetBufferInitialData(row);
        if (data.IsEmpty) return;
        Buffer buffer = MaterializedBuffers[resource];
        BufferRange range = new(0, checked((ulong)data.Length));
        MappedBuffer mapping = _backend.Map(buffer, MapType.Write, range);
        try
        {
            data.CopyTo(mapping.Bytes);
            mapping.Flush(range);
        }
        finally
        {
            mapping.Dispose();
        }
    }

    private void AcquireBufferViews()
    {
        if (_materializedBufferViewCount == 0) return;
        ArenaSlice<int> hashes = AllocateSlice<int>(GetViewHashSlotCount(_materializedBufferViewCount));
        int mask = hashes.Length - 1;
        for (int view = 0; view < _materializedBufferViewCount; view++)
        {
            if (!IsBufferViewMaterialized(view)) continue;
            int slot = GetBufferViewHash(view) & mask;
            while (hashes[slot] != 0)
            {
                int representative = hashes[slot] - 1;
                if (BufferViewEquals(view, representative))
                {
                    _bufferViewRepresentatives[view] = representative;
                    MaterializedBufferViews[view] = MaterializedBufferViews[representative];
                    goto Next;
                }
                slot = (slot + 1) & mask;
            }

            Buffer buffer = MaterializedBuffers[_bufferViewResources[view]];
            BufferRange range = _bufferViewRanges[view];
            Format? format = GetBufferViewFormat(view);
            uint stride = _bufferViewStrides[view];
            string? label = GetBufferViewName(view);
            GraphBindingType type = _bufferViewTypes[view];
            DeviceResource materialized = type switch
            {
                GraphBindingType.ConstantBuffer => _backend.CreateBufferCbv(
                    _device,
                    new BufferCbvDesc(buffer, range, label)),
                GraphBindingType.ReadOnlyBuffer => _backend.CreateBufferSrv(
                    _device,
                    new BufferSrvDesc(buffer, range, format, stride, label)),
                GraphBindingType.StorageBuffer => _backend.CreateBufferUav(
                    _device,
                    new BufferUavDesc(buffer, range, format, stride, Label: label)),
                _ => throw new InvalidOperationException($"Unsupported graph buffer view type {type}."),
            };
            MaterializedBufferViews[view] = new MaterializedBufferView(type, materialized);
            _bufferViewRepresentatives[view] = view;
            hashes[slot] = view + 1;
        Next:
            ;
        }
    }

    private void AcquireTextureViews()
    {
        if (_materializedTextureViewCount == 0) return;
        ArenaSlice<int> hashes = AllocateSlice<int>(GetViewHashSlotCount(_materializedTextureViewCount));
        int mask = hashes.Length - 1;
        for (int view = 0; view < _materializedTextureViewCount; view++)
        {
            if (!IsTextureViewMaterialized(view)) continue;
            int slot = GetTextureViewHash(view) & mask;
            while (hashes[slot] != 0)
            {
                int representative = hashes[slot] - 1;
                if (TextureViewEquals(view, representative))
                {
                    _textureViewRepresentatives[view] = representative;
                    MaterializedTextureViews[view] = MaterializedTextureViews[representative];
                    goto Next;
                }
                slot = (slot + 1) & mask;
            }

            Texture texture = MaterializedTextures[_textureViewResources[view]];
            TextureSubresourceRange range = _textureViewRanges[view];
            Format format = _textureViewFormats[view];
            TextureViewDimension dimension = _textureViewDimensions[view];
            GraphTextureViewUsage usage = _textureViewUsages[view];
            string? label = GetTextureViewName(view);
            MaterializedTextureView materialized = new()
            {
                ShaderResource = (usage & GraphTextureViewUsage.ShaderResource) != 0
                    ? _backend.CreateTextureSrv(
                        _device,
                        new TextureSrvDesc(texture, range, format, dimension, label))
                    : null,
                Storage = (usage & GraphTextureViewUsage.Storage) != 0
                    ? _backend.CreateTextureUav(
                        _device,
                        new TextureUavDesc(texture, range, format, dimension, label))
                    : null,
                ColorAttachment = (usage &
                    (GraphTextureViewUsage.ColorAttachment |
                     GraphTextureViewUsage.ResolveDestination)) != 0
                    ? _backend.CreateColorAttachmentView(
                        _device,
                        new ColorAttachmentViewDesc(texture, range, format, dimension, label))
                    : null,
                ReadOnlyDepthStencilAttachment =
                    (usage & GraphTextureViewUsage.DepthStencilAttachment) != 0
                        ? _backend.CreateDepthStencilView(
                            _device,
                            new DepthStencilViewDesc(
                                texture,
                                range,
                                format,
                                dimension,
                                ReadOnlyDepth: true,
                                ReadOnlyStencil: true,
                                Label: label))
                        : null,
                WritableDepthStencilAttachment =
                    (usage & GraphTextureViewUsage.DepthStencilAttachment) != 0
                        ? _backend.CreateDepthStencilView(
                            _device,
                            new DepthStencilViewDesc(texture, range, format, dimension, Label: label))
                        : null,
            };
            MaterializedTextureViews[view] = materialized;
            _textureViewRepresentatives[view] = view;
            hashes[slot] = view + 1;
        Next:
            ;
        }
    }

    private void AcquireAccelerationStructures()
    {
        for (int ordinal = 0; ordinal < _accelerationStructureCount; ordinal++)
        {
            if (!IsAccelerationStructureMaterialized(ordinal)) continue;
            int buffer = GetAccelerationStructureBuffer(ordinal);
            AccelerationStructures[ordinal] = _backend.CreateAccelerationStructure(
                _device,
                MaterializedBuffers[buffer],
                GetAccelerationStructureRange(ordinal),
                GetAccelerationStructureType(ordinal),
                GetBufferDescription(buffer).Label);
        }
    }

    private void MaterializeParameterBindings()
    {
        for (int argument = 0; argument < ShaderArgumentCount; argument++)
        {
            GraphBindingType type = GetShaderArgumentType(argument);
            int view = GetShaderArgumentView(argument);
            _materializedBindings[argument] = type switch
            {
                GraphBindingType.ConstantBuffer or
                GraphBindingType.ReadOnlyBuffer or
                GraphBindingType.StorageBuffer =>
                    MaterializedBufferViews[view]?.ToBinding() ??
                    throw new InvalidOperationException("A shader buffer argument was not materialized."),
                GraphBindingType.SampledTexture or
                GraphBindingType.StorageTexture =>
                    MaterializedTextureViews[view]?.ToBinding(type) ??
                    throw new InvalidOperationException("A shader texture argument was not materialized."),
                GraphBindingType.AccelerationStructure => ResourceBinding.AccelerationStructure(
                    _backend.CreateAccelerationStructureSrv(
                        _device,
                        new AccelerationStructureSrvDesc(AccelerationStructures[view]))),
                GraphBindingType.Sampler => ResourceBinding.SampledWith(
                    GetSampler(GetShaderArgumentSampler(argument))),
                _ => throw new InvalidOperationException($"Unsupported graph shader argument {type}."),
            };
        }
    }

    private void AcquireCommandContexts()
    {
        long started = _collectDetailedTimings ? Stopwatch.GetTimestamp() : 0;
        for (int command = 0; command < CommandUnits.Count; command++)
        {
            RuntimeCmd unit = CommandUnits[command];
            CommandContext context = _backend.CreateCommandContext(
                _device,
                new CommandContextDesc(
                    unit.Queue,
                    QueueIndex: 0,
                    NodeIndex: 0,
                    InitialSlotCount: 1,
                    Label: $"rg-{unit.Name}-{command}"));
            _commandContexts[command] = context;
        }
        AddDetailedTicks(ref _recorderAcquisitionTicks, started);
    }

    internal int GetRuntimeCmdRenderingColorCapacity(int command)
    {
        int capacity = 0;
        foreach (int pass in GetCommandUnitPasses(CommandUnits[command]))
            capacity = Math.Max(capacity, Passes[pass].ColorAttachmentCount);
        return capacity;
    }

    internal bool RuntimeCmdRequiresCoordinator(int command)
    {
        foreach (int pass in GetCommandUnitPasses(CommandUnits[command]))
            if ((Passes[pass].Flags & PassFlags.NeverParallel) != 0)
                return true;
        return false;
    }

    private void EncodeInParallel()
    {
        if (CommandUnits.Count == 0) return;
        bool hasWorker = false;
        bool hasCoordinator = false;
        for (int command = 0; command < CommandUnits.Count; command++)
        {
            if (RuntimeCmdRequiresCoordinator(command)) hasCoordinator = true;
            else hasWorker = true;
        }

        Exception? failure = null;
        long sequence = 0;
        bool handedOff = false;
        if (hasWorker)
        {
            try
            {
                handedOff = JobSystem.TryHandoffLatencyWork(
                    this,
                    static (state, _) => ((RenderGraph)state!).EncodeWorkerCommands(),
                    0,
                    _commandPriority,
                    out sequence);
                if (!handedOff) EncodeWorkerCommands();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }
        if (hasCoordinator)
        {
            try { EncodeCoordinatorCommands(); }
            catch (Exception exception) { failure = CombineFailure(failure, exception); }
        }
        if (handedOff)
        {
            try { JobSystem.JoinLatencyWork(sequence); }
            catch (Exception exception) { failure = CombineFailure(failure, exception); }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void EncodeWorkerCommands()
    {
        for (int command = 0; command < CommandUnits.Count; command++)
            if (!RuntimeCmdRequiresCoordinator(command)) EncodeRuntimeCommand(command);
    }

    private void EncodeCoordinatorCommands()
    {
        for (int command = 0; command < CommandUnits.Count; command++)
            if (RuntimeCmdRequiresCoordinator(command)) EncodeRuntimeCommand(command);
    }

    private void EncodeRuntimeCommand(int command)
    {
        CommandContext context = Interlocked.Exchange(ref _commandContexts[command], null)
            ?? throw new InvalidOperationException($"Runtime command {command} has no recording context.");
        bool begun = false;
        bool ended = false;
        try
        {
            RuntimeCmd unit = CommandUnits[command];
            bool shaderCapable = unit.Queue != QueueType.Copy;
            _backend.Begin(
                context,
                new CommandRecordingDesc(
                    InitialResourceDescriptorCapacity: shaderCapable ? 64u : 0u,
                    InitialSamplerDescriptorCapacity: shaderCapable ? 16u : 0u,
                    InitialCapturedResourceCapacity: 64));
            begun = true;
            int colorOffset = _commandRenderingColorOffsets[command];
            int colorCount = _commandRenderingColorOffsets[command + 1] - colorOffset;
            Span<ColorAttachmentDesc> colorScratch =
                _renderingColorScratch.AsSpan(colorOffset, colorCount);
            EncodeCommandUnit(context, unit, colorScratch);
            long finish = _collectDetailedTimings ? Stopwatch.GetTimestamp() : 0;
            _recordedCommands[command] = _backend.End(context);
            ended = true;
            AddDetailedTicks(ref _finishTicks, finish);
        }
        finally
        {
            if (begun && !ended)
            {
                try { _backend.Discard(context); }
                catch { }
            }
            context.Dispose();
        }
    }

    private void EncodeCommandUnit(
        CommandContext context,
        in RuntimeCmd unit,
        Span<ColorAttachmentDesc> colorScratch)
    {
        switch (unit.CmdId)
        {
            case RuntimeCmd.StandaloneCmdId:
                foreach (int pass in GetCommandUnitPasses(unit))
                    EncodeLogicalPass(context, pass, colorScratch);
                break;
            case RuntimeCmd.RasterScopeCmdId:
                EncodeRasterScope(context, GetCommandUnitPasses(unit), colorScratch);
                break;
            case RuntimeCmd.AliasingBarrierCmdId:
                EmitAliasingBarriers(context, GetCommandUnitAliases(unit));
                break;
            case RuntimeCmd.BarrierCmdId:
                EmitBarriers(context, GetCommandUnitBarriers(unit));
                break;
            default:
                throw new NotSupportedException($"Runtime command id {unit.CmdId} has no RHI lowering.");
        }
    }

    private void EncodeLogicalPass(
        CommandContext context,
        int pass,
        Span<ColorAttachmentDesc> colorScratch)
    {
        EmitBarriers(context, GetBeforeBarriers(pass));
        Extent2D extent = GetExtent2D(pass);
        if (extent.IsValid)
        {
            BeginRendering(context, pass, pass, extent, colorScratch);
            try { ExecuteLogicalPass(context, pass); }
            finally { EndRendering(context); }
        }
        else
        {
            ExecuteLogicalPass(context, pass);
        }
        EmitBarriers(context, GetAfterBarriers(pass));
    }

    private void EncodeRasterScope(
        CommandContext context,
        ReadOnlySpan<int> passes,
        Span<ColorAttachmentDesc> colorScratch)
    {
        if (passes.IsEmpty) throw new InvalidOperationException("A raster scope is empty.");
        int first = passes[0];
        int last = passes[^1];
        EmitBarriers(context, GetBeforeBarriers(first));
        Extent2D extent = GetExtent2D(first);
        if (!extent.IsValid) throw new InvalidOperationException("A raster scope has no render extent.");
        BeginRendering(context, first, last, extent, colorScratch);
        try
        {
            for (int index = 0; index < passes.Length; index++)
            {
                int pass = passes[index];
                if (index != 0 && GetBeforeBarriers(pass).Length != 0)
                    throw new InvalidOperationException("A merged raster boundary contains an entry barrier.");
                if (index != passes.Length - 1 && GetAfterBarriers(pass).Length != 0)
                    throw new InvalidOperationException("A merged raster boundary contains an exit barrier.");
                ExecuteLogicalPass(context, pass);
            }
        }
        finally
        {
            EndRendering(context);
        }
        EmitBarriers(context, GetAfterBarriers(last));
    }

    private void ExecuteLogicalPass(CommandContext context, int pass)
    {
        long started = _collectDetailedTimings ? Stopwatch.GetTimestamp() : 0;
        Pipeline? pipeline = GetPassPipeline(pass);
        if (pipeline is not null)
        {
            _backend.SetPipeline(context, pipeline);
            VariableLayoutReflection layout = GetPassParameterLayout(pass);
            if (layout != VariableLayoutReflection.Null)
            {
                PassData row = Passes[pass];
                ReadOnlySpan<ResourceBinding> resources =
                    _materializedBindings.AsSpan(row.ShaderArgumentOffset, row.ShaderArgumentCount);
                ParameterBlockBindings bindings = new(
                    layout,
                    resources,
                    GetPassParameterOrdinaryData(pass));
                _backend.SetTransientParameterBindings(context, bindings);
            }
        }
        PassExecutor executor = _passExecutors[pass] ??
            throw new InvalidOperationException("A graph pass has no command encoder.");
        UnsafeGraphContext graphContext = new(_backend, context, this, pass);
        executor(ref graphContext);
        long elapsed = AddDetailedTicks(ref _callbackTicks, started);
        if (_collectDetailedTimings) _passCallbackTicks[pass] = elapsed;
    }

    private void BeginRendering(
        CommandContext context,
        int firstPass,
        int lastPass,
        in Extent2D extent,
        Span<ColorAttachmentDesc> colorScratch)
    {
        long started = _collectDetailedTimings ? Stopwatch.GetTimestamp() : 0;
        PassData first = Passes[firstPass];
        ReadOnlySpan<PassFragmentData> opening = GetPassColorAttachments(first);
        ReadOnlySpan<PassFragmentData> ending = GetPassColorAttachments(Passes[lastPass]);
        Span<ColorAttachmentDesc> colors = colorScratch[..opening.Length];
        for (int index = 0; index < opening.Length; index++)
        {
            PassFragmentData start = opening[index];
            PassFragmentData end = ending[index];
            MaterializedTextureView view = MaterializedTextureViews[start.View] ??
                throw new InvalidOperationException("A color attachment view was not materialized.");
            ColorAttachmentView color = view.ColorAttachment ??
                throw new InvalidOperationException("A graph color attachment has no RTV identity.");
            ColorAttachmentView? resolve = end.HasResolve
                ? MaterializedTextureViews[end.ResolveView]?.ColorAttachment
                : null;
            colors[index] = new ColorAttachmentDesc(
                color,
                start.Load,
                StoreType.Store,
                start.ClearColor,
                resolve,
                end.ResolveType);
        }

        DepthStencilAttachmentDesc? depthStencil = null;
        if (GetPassDepthStencilAttachment(first) is PassFragmentData row)
        {
            MaterializedTextureView view = MaterializedTextureViews[row.View] ??
                throw new InvalidOperationException("A depth-stencil view was not materialized.");
            bool readOnly = row.DepthReadOnly && row.StencilReadOnly;
            DepthStencilView dsv = readOnly
                ? view.ReadOnlyDepthStencilAttachment ??
                    throw new InvalidOperationException("A read-only DSV was not materialized.")
                : view.WritableDepthStencilAttachment ??
                    throw new InvalidOperationException("A writable DSV was not materialized.");
            depthStencil = new DepthStencilAttachmentDesc(
                dsv,
                row.HasDepth ? row.DepthLoad : LoadType.Discard,
                row.HasDepth ? StoreType.Store : StoreType.Discard,
                row.HasStencil ? row.StencilLoad : LoadType.Discard,
                row.HasStencil ? StoreType.Store : StoreType.Discard,
                row.ClearDepth,
                row.ClearStencil);
        }

        RenderingOptions options = HasRasterUnorderedAccess(firstPass, lastPass)
            ? RenderingOptions.AllowUnorderedAccessWrites
            : RenderingOptions.None;
        RenderingDesc rendering = new(
            colors,
            depthStencil,
            checked((uint)extent.Width),
            checked((uint)extent.Height),
            options);
        _backend.BeginRendering(context, rendering);
        AddDetailedTicks(ref _renderingTicks, started);
    }

    private bool HasRasterUnorderedAccess(int firstPass, int lastPass)
    {
        for (int pass = firstPass; pass <= lastPass; pass++)
            foreach (ref readonly PassInputData access in GetPassAccesses(Passes[pass]))
                if ((access.Flags & GraphAccess.Write) != 0 &&
                    access.State == GraphResourceUsage.UnorderedAccess)
                    return true;
        return false;
    }

    private void EndRendering(CommandContext context)
    {
        long started = _collectDetailedTimings ? Stopwatch.GetTimestamp() : 0;
        _backend.EndRendering(context);
        AddDetailedTicks(ref _renderingTicks, started);
    }

    private void EmitAliasingBarriers(
        CommandContext context,
        ReadOnlySpan<PlannedAliasingBarrier> barriers)
    {
        long started = _collectDetailedTimings ? Stopwatch.GetTimestamp() : 0;
        AliasingResource[] before = new AliasingResource[1];
        AliasingResource[] after = new AliasingResource[1];
        foreach (ref readonly PlannedAliasingBarrier barrier in barriers)
        {
            before[0] = new AliasingResource(GetResource(barrier.BeforeResource));
            after[0] = new AliasingResource(GetResource(barrier.AfterResource));
            SomeEngine.Graphics.AliasingBarrier value = new(before, after);
            _backend.Barrier(context, value);
        }
        AddDetailedTicks(ref _barrierTicks, started);
    }

    private void EmitBarriers(
        CommandContext context,
        ReadOnlySpan<PlannedBarrier> barriers)
    {
        if (barriers.IsEmpty) return;
        long started = _collectDetailedTimings ? Stopwatch.GetTimestamp() : 0;
        foreach (ref readonly PlannedBarrier barrier in barriers)
            EmitBarrier(context, barrier);
        AddDetailedTicks(ref _barrierTicks, started);
    }

    private void EmitBarrier(CommandContext context, in PlannedBarrier barrier)
    {
        Resource resource = GetResource(barrier.Resource);
        if (resource is Buffer buffer)
        {
            (PipelineSync beforeSync, ResourceAccess beforeAccess) =
                barrier.UsesPlacementInitialState
                    ? (buffer.InitialSync, buffer.InitialAccess)
                    : BufferState(barrier.Before);
            (PipelineSync afterSync, ResourceAccess afterAccess) = BufferState(barrier.After);
            switch (barrier.Kind)
            {
                case GraphBarrierKind.Resource:
                    _backend.Barrier(
                        context,
                        new BufferBarrier(buffer, beforeSync, afterSync, beforeAccess, afterAccess));
                    break;
                case GraphBarrierKind.QueueRelease:
                    _backend.Barrier(
                        context,
                        new QueueRelease(
                            buffer,
                            null,
                            beforeSync,
                            beforeAccess,
                            null,
                            barrier.OtherQueue));
                    break;
                case GraphBarrierKind.QueueAcquire:
                    _backend.Barrier(
                        context,
                        new QueueAcquire(
                            buffer,
                            null,
                            barrier.OtherQueue,
                            afterSync,
                            afterAccess,
                            null));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown graph barrier kind {barrier.Kind}.");
            }
            return;
        }

        Texture texture = (Texture)resource;
        (PipelineSync textureBeforeSync, ResourceAccess textureBeforeAccess, TextureLayout beforeLayout) =
            barrier.UsesPlacementInitialState
                ? (texture.InitialSync, texture.InitialAccess, texture.InitialLayout)
                : TextureState(barrier.Before);
        (PipelineSync textureAfterSync, ResourceAccess textureAfterAccess, TextureLayout afterLayout) =
            TextureState(barrier.After);
        switch (barrier.Kind)
        {
            case GraphBarrierKind.Resource:
                _backend.Barrier(
                    context,
                    new TextureBarrier(
                        texture,
                        barrier.TextureRange,
                        textureBeforeSync,
                        textureAfterSync,
                        textureBeforeAccess,
                        textureAfterAccess,
                        beforeLayout,
                        afterLayout));
                break;
            case GraphBarrierKind.QueueRelease:
                _backend.Barrier(
                    context,
                    new QueueRelease(
                        texture,
                        barrier.TextureRange,
                        textureBeforeSync,
                        textureBeforeAccess,
                        beforeLayout,
                        barrier.OtherQueue));
                break;
            case GraphBarrierKind.QueueAcquire:
                _backend.Barrier(
                    context,
                    new QueueAcquire(
                        texture,
                        barrier.TextureRange,
                        barrier.OtherQueue,
                        textureAfterSync,
                        textureAfterAccess,
                        afterLayout));
                break;
            default:
                throw new InvalidOperationException($"Unknown graph barrier kind {barrier.Kind}.");
        }
    }

    private static (PipelineSync Sync, ResourceAccess Access) BufferState(
        GraphResourceUsage usage) => usage switch
    {
        GraphResourceUsage.Common => (PipelineSync.None, ResourceAccess.Common),
        GraphResourceUsage.VertexOrConstantBuffer =>
            (PipelineSync.AllShading, ResourceAccess.VertexBuffer | ResourceAccess.ConstantBuffer),
        GraphResourceUsage.IndexBuffer => (PipelineSync.IndexInput, ResourceAccess.IndexBuffer),
        GraphResourceUsage.UnorderedAccess => (PipelineSync.AllShading, ResourceAccess.UnorderedAccess),
        GraphResourceUsage.ShaderResource => (PipelineSync.AllShading, ResourceAccess.ShaderResource),
        GraphResourceUsage.IndirectArgument =>
            (PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
        GraphResourceUsage.CopySource => (PipelineSync.Copy, ResourceAccess.CopySource),
        GraphResourceUsage.CopyDestination => (PipelineSync.Copy, ResourceAccess.CopyDestination),
        GraphResourceUsage.AccelerationStructure =>
            (PipelineSync.RayTracing | PipelineSync.BuildRayTracingAccelerationStructure,
             ResourceAccess.RayTracingAccelerationStructureRead |
             ResourceAccess.RayTracingAccelerationStructureWrite),
        _ => throw new InvalidOperationException($"Graph usage {usage} is not valid for a Buffer."),
    };

    private static (PipelineSync Sync, ResourceAccess Access, TextureLayout Layout) TextureState(
        GraphResourceUsage usage) => usage switch
    {
        GraphResourceUsage.Common =>
            (PipelineSync.None, ResourceAccess.Common, TextureLayout.Common),
        GraphResourceUsage.Undefined =>
            (PipelineSync.None, ResourceAccess.NoAccess, TextureLayout.Undefined),
        GraphResourceUsage.Present =>
            (PipelineSync.None, ResourceAccess.NoAccess, TextureLayout.Present),
        GraphResourceUsage.RenderTarget =>
            (PipelineSync.RenderTarget, ResourceAccess.RenderTarget, TextureLayout.RenderTarget),
        GraphResourceUsage.UnorderedAccess =>
            (PipelineSync.AllShading, ResourceAccess.UnorderedAccess, TextureLayout.UnorderedAccess),
        GraphResourceUsage.DepthRead =>
            (PipelineSync.DepthStencil, ResourceAccess.DepthStencilRead, TextureLayout.DepthStencilRead),
        GraphResourceUsage.DepthWrite =>
            (PipelineSync.DepthStencil, ResourceAccess.DepthStencilWrite, TextureLayout.DepthStencilWrite),
        GraphResourceUsage.DepthReadShaderResource =>
            (PipelineSync.DepthStencil | PipelineSync.AllShading,
             ResourceAccess.DepthStencilRead | ResourceAccess.ShaderResource,
             TextureLayout.DepthStencilRead),
        GraphResourceUsage.ShaderResource =>
            (PipelineSync.AllShading, ResourceAccess.ShaderResource, TextureLayout.ShaderResource),
        GraphResourceUsage.CopySource =>
            (PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource),
        GraphResourceUsage.CopyDestination =>
            (PipelineSync.Copy, ResourceAccess.CopyDestination, TextureLayout.CopyDestination),
        GraphResourceUsage.ResolveSource =>
            (PipelineSync.Resolve, ResourceAccess.ResolveSource, TextureLayout.ResolveSource),
        GraphResourceUsage.ResolveDestination =>
            (PipelineSync.Resolve, ResourceAccess.ResolveDestination, TextureLayout.ResolveDestination),
        GraphResourceUsage.ShadingRateSource =>
            (PipelineSync.Draw, ResourceAccess.ShadingRateSource, TextureLayout.ShadingRateSource),
        _ => throw new InvalidOperationException($"Graph usage {usage} is not valid for a Texture."),
    };

    private void SubmitBatch(int batchOrdinal)
    {
        CommandBatch batch = CommandBatches[batchOrdinal];
        Queue queue = _backend.GetQueue(_device, batch.Queue);
        _batchWaitScratch.AsSpan().Clear();
        int waitCount = 0;
        foreach (int predecessor in GetBatchDependencies(batch))
            MergeWait(_batchCompletions[predecessor], queue, ref waitCount);
        foreach (QueueCompletion external in GetBatchExternalWaits(batch))
            MergeWait(external, queue, ref waitCount);

        ReadOnlySpan<int> units = GetBatchCommandUnits(batch);
        Span<RecordedCommands> commands = _batchCommandScratch.AsSpan(0, units.Length);
        for (int index = 0; index < units.Length; index++)
        {
            int unit = units[index];
            commands[index] = _recordedCommands[unit] ??
                throw new InvalidOperationException(
                    $"Runtime command {unit} has no executable payload.");
        }

        int swapchainImageCount = 0;
        foreach (int resource in GetBatchResources(batch))
        {
            if (IsBufferResourceOrdinal(resource))
                continue;
            ResourceUnversionedData row =
                GetTextureByResourceOrdinal(resource);
            if (!row.IsSwapchainImage || !IsLastBatchForResource(batchOrdinal, resource))
                continue;
            if (batch.Queue != QueueType.Graphics)
            {
                throw new InvalidOperationException(
                    "A swapchain image's final Present transition must execute on a Graphics Queue.");
            }
            _batchSwapchainImageScratch[swapchainImageCount++] = GetSwapchainImage(row);
        }

        QueueSubmitDesc submission = new(
            _batchWaitScratch.AsSpan(0, waitCount),
            ReadOnlySpan<TimelinePoint>.Empty,
            commands,
            _batchSwapchainImageScratch.AsSpan(0, swapchainImageCount),
            ReadOnlySpan<TimelineSignal>.Empty);
        QueueCompletion completion = _backend.Submit(queue, submission);
        _batchCompletions[batchOrdinal] = completion;
        foreach (int resource in GetBatchResources(batch))
            MergeResourceCompletion(resource, completion);
        foreach (int unit in units)
        {
            _recordedCommands[unit]?.Dispose();
            _recordedCommands[unit] = null;
        }
        commands.Clear();
        _batchSwapchainImageScratch.AsSpan(0, swapchainImageCount).Clear();
    }

    private bool IsLastBatchForResource(int batchOrdinal, int resource)
    {
        for (int candidate = batchOrdinal + 1; candidate < CommandBatches.Count; candidate++)
        {
            foreach (int subsequent in GetBatchResources(CommandBatches[candidate]))
            {
                if (subsequent == resource)
                    return false;
            }
        }
        return true;
    }

    private void MergeWait(QueueCompletion completion, Queue destination, ref int count)
    {
        if (completion == default || ReferenceEquals(completion.Queue, destination)) return;
        for (int index = 0; index < count; index++)
        {
            QueueCompletion current = _batchWaitScratch[index];
            if (!ReferenceEquals(current.Queue, completion.Queue)) continue;
            if (completion.Value > current.Value) _batchWaitScratch[index] = completion;
            return;
        }
        if (count == _batchWaitScratch.Length)
            throw new InvalidOperationException("A submission references too many source queues.");
        _batchWaitScratch[count++] = completion;
    }

    private void MergeResourceCompletion(int resource, QueueCompletion completion)
    {
        int count = _resourceCompletionCounts[resource];
        Span<QueueCompletion> values = _resourceCompletions.AsSpan(resource * 3, 3);
        for (int index = 0; index < count; index++)
        {
            if (!ReferenceEquals(values[index].Queue, completion.Queue)) continue;
            if (completion.Value > values[index].Value) values[index] = completion;
            return;
        }
        if (count == values.Length)
            throw new InvalidOperationException("A resource references too many queues.");
        values[count] = completion;
        _resourceCompletionCounts[resource] = count + 1;
    }

    private QueueCompletion[] CollectPublishedCompletions()
    {
        QueueCompletion[] latest = new QueueCompletion[3];
        int count = 0;
        for (int batch = 0; batch < CommandBatches.Count; batch++)
        {
            QueueCompletion completion = _batchCompletions[batch];
            if (completion == default) continue;
            int existing = -1;
            for (int index = 0; index < count; index++)
                if (ReferenceEquals(latest[index].Queue, completion.Queue))
                {
                    existing = index;
                    break;
                }
            if (existing < 0) latest[count++] = completion;
            else if (completion.Value > latest[existing].Value) latest[existing] = completion;
        }
        return latest[..count].ToArray();
    }

    private Resource GetResource(int resource) => IsBufferResourceOrdinal(resource)
        ? MaterializedBuffers[resource]
        : MaterializedTextures[GetTextureOrdinal(resource)];

    private Exception? DisposeUnsubmittedCommands(Exception? failure)
    {
        for (int command = _recordedCommands.Length - 1; command >= 0; command--)
        {
            RecordedCommands? recorded = _recordedCommands[command];
            if (!recorded.HasValue) continue;
            _recordedCommands[command] = null;
            try { recorded.Value.Dispose(); }
            catch (Exception exception) { failure = CombineFailure(failure, exception); }
        }
        return failure;
    }

    private Exception? DisposeCommandContexts(Exception? failure)
    {
        for (int command = _commandContexts.Length - 1; command >= 0; command--)
        {
            CommandContext? context = _commandContexts[command];
            if (context is null) continue;
            _commandContexts[command] = null;
            try { _backend.Discard(context); }
            catch (Exception exception) { failure = CombineFailure(failure, exception); }
            try { context.Dispose(); }
            catch (Exception exception) { failure = CombineFailure(failure, exception); }
        }
        return failure;
    }

    private Exception? DisposeExecutionObjects(Exception? failure)
    {
        for (int index = _materializedBindings.Length - 1; index >= 0; index--)
        {
            if (_materializedBindings[index].Type != ResourceBindingType.AccelerationStructure ||
                _materializedBindings[index].Value is not AccelerationStructureSrv view)
                continue;
            try { view.Dispose(); }
            catch (Exception exception) { failure = CombineFailure(failure, exception); }
        }
        for (int index = _accelerationStructureCount - 1; index >= 0; index--)
        {
            AccelerationStructure? value = _materializedAccelerationStructures[index];
            if (value is null) continue;
            try { value.Dispose(); }
            catch (Exception exception) { failure = CombineFailure(failure, exception); }
        }
        for (int index = _materializedTextureViewCount - 1; index >= 0; index--)
        {
            MaterializedTextureView? value = _materializedTextureViews[index];
            if (value is null || _textureViewRepresentatives[index] != index) continue;
            try { value.Dispose(); }
            catch (Exception exception) { failure = CombineFailure(failure, exception); }
        }
        for (int index = _materializedBufferViewCount - 1; index >= 0; index--)
        {
            MaterializedBufferView? value = _materializedBufferViews[index];
            if (value is null || _bufferViewRepresentatives[index] != index) continue;
            try { value.Dispose(); }
            catch (Exception exception) { failure = CombineFailure(failure, exception); }
        }
        for (int texture = _materializedTextureCount - 1; texture >= 0; texture--)
        {
            Texture? value = _materializedTextures[texture];
            if (value is null || Textures[texture].IsImported) continue;
            try { value.Dispose(); }
            catch (Exception exception) { failure = CombineFailure(failure, exception); }
        }
        for (int buffer = _materializedBufferCount - 1; buffer >= 0; buffer--)
        {
            Buffer? value = _materializedBuffers[buffer];
            if (value is null || Buffers[buffer].IsImported) continue;
            try { value.Dispose(); }
            catch (Exception exception) { failure = CombineFailure(failure, exception); }
        }
        for (int heap = _heaps.Length - 1; heap >= 0; heap--)
        {
            Heap? value = _heaps[heap];
            if (value is null) continue;
            try { value.Dispose(); }
            catch (Exception exception) { failure = CombineFailure(failure, exception); }
        }
        return failure;
    }

    private int GetBufferViewHash(int ordinal)
    {
        HashCode hash = new();
        hash.Add(_bufferViewResources[ordinal]);
        hash.Add(_bufferViewRanges[ordinal]);
        hash.Add(_bufferViewTypes[ordinal]);
        hash.Add(GetBufferViewFormat(ordinal));
        hash.Add(_bufferViewStrides[ordinal]);
        return hash.ToHashCode();
    }

    private int GetTextureViewHash(int ordinal)
    {
        HashCode hash = new();
        hash.Add(_textureViewResources[ordinal]);
        hash.Add(_textureViewRanges[ordinal]);
        hash.Add(_textureViewUsages[ordinal]);
        hash.Add(_textureViewFormats[ordinal]);
        hash.Add(_textureViewDimensions[ordinal]);
        return hash.ToHashCode();
    }

    private bool BufferViewEquals(int left, int right) =>
        _bufferViewResources[left] == _bufferViewResources[right] &&
        _bufferViewRanges[left] == _bufferViewRanges[right] &&
        _bufferViewTypes[left] == _bufferViewTypes[right] &&
        GetBufferViewFormat(left) == GetBufferViewFormat(right) &&
        _bufferViewStrides[left] == _bufferViewStrides[right];

    private bool TextureViewEquals(int left, int right) =>
        _textureViewResources[left] == _textureViewResources[right] &&
        _textureViewRanges[left] == _textureViewRanges[right] &&
        _textureViewUsages[left] == _textureViewUsages[right] &&
        _textureViewFormats[left] == _textureViewFormats[right] &&
        _textureViewDimensions[left] == _textureViewDimensions[right];

    private static int GetViewHashSlotCount(int count)
    {
        int result = 2;
        while (result < checked(count * 2)) result = checked(result * 2);
        return result;
    }

    internal long GetPassCallbackTicks(int pass) =>
        _passCallbackTicks.IsEmpty ? 0 : _passCallbackTicks[pass];

    private long AddDetailedTicks(ref long destination, long started)
    {
        if (!_collectDetailedTimings) return 0;
        long elapsed = Stopwatch.GetTimestamp() - started;
        Interlocked.Add(ref destination, elapsed);
        return elapsed;
    }

    private static Exception CombineFailure(Exception? current, Exception next) =>
        current is null
            ? next
            : new AggregateException(
                "Render Graph execution and cleanup both failed.",
                current,
                next);
}

internal readonly record struct InvocationCpuTimings(
    TimeSpan Close,
    CompilerCpuTimings Compiler,
    ResourceAcquisitionCpuTimings Acquisition,
    CommandSubmissionCpuTimings Commands);

internal readonly record struct CommandSubmissionCpuTimings(
    TimeSpan Encoding,
    TimeSpan Submit,
    TimeSpan Cleanup)
{
    public TimeSpan RecorderAcquisition { get; init; }
    public TimeSpan Barrier { get; init; }
    public TimeSpan Rendering { get; init; }
    public TimeSpan Callbacks { get; init; }
    public TimeSpan Close { get; init; }
}

internal readonly record struct ResourceAcquisitionCpuTimings(
    TimeSpan Setup,
    TimeSpan Heaps,
    TimeSpan Resources,
    TimeSpan Views,
    TimeSpan Bindless);
