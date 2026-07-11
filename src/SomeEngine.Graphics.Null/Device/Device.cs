namespace SomeEngine.Graphics.Null;

/// <summary>
/// Deterministic CPU-backed implementation of the graphics contract. Device mutation and submission
/// are coordinator-thread owned; each acquired command context independently binds to its first
/// recording thread.
/// </summary>
public sealed partial class Device : IDevice
{
    private static long s_nextSemanticGeneration;
    private readonly DeviceDomain _domain;
    private readonly object _gate = new();
    private readonly Options _options;
    private readonly int _coordinatorThreadId;
    private readonly ulong[] _submitted = new ulong[3];
    private readonly ulong[] _completed = new ulong[3];
    private readonly List<GraphicsDiagnostic> _diagnostics = [];
    private readonly GenerationRegistry<HeapRecord> _heaps;
    private readonly GenerationRegistry<BufferRecord> _buffers;
    private readonly GenerationRegistry<TextureRecord> _textures;
    private readonly GenerationRegistry<TextureViewRecord> _textureViews;
    private readonly GenerationRegistry<BufferViewRecord> _bufferViews;
    private readonly GenerationRegistry<SamplerRecord> _samplers;
    private readonly GenerationRegistry<BindGroupLayoutRecord> _bindGroupLayouts;
    private readonly GenerationRegistry<BindGroupRecord> _bindGroups;
    private readonly GenerationRegistry<ShaderRecord> _shaders;
    private readonly GenerationRegistry<PipelineLayoutRecord> _pipelineLayouts;
    private readonly GenerationRegistry<PipelineRecord> _pipelines;
    private readonly GenerationRegistry<CommandListRecord> _commandLists;
    private Statistics _statistics;
    private bool _disposed;

    public Device() : this(null) { }

    public Device(Options? options)
    {
        _domain = DeviceDomain.Allocate();
        _heaps = new(_domain, "heap");
        _buffers = new(_domain, "buffer");
        _textures = new(_domain, "texture");
        _textureViews = new(_domain, "texture view");
        _bufferViews = new(_domain, "buffer view");
        _samplers = new(_domain, "sampler");
        _bindGroupLayouts = new(_domain, "bind group layout");
        _bindGroups = new(_domain, "bind group");
        _shaders = new(_domain, "shader");
        _pipelineLayouts = new(_domain, "pipeline layout");
        _pipelines = new(_domain, "pipeline");
        _commandLists = new(_domain, "command list");
        _options = options ?? new Options();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.DeviceName);
        _coordinatorThreadId = Environment.CurrentManagedThreadId;
        List<QueueType> queues = [QueueType.Graphics];
        if (_options.SupportsAsyncCompute) queues.Add(QueueType.Compute);
        if (_options.SupportsCopyQueue) queues.Add(QueueType.Copy);
        Compilation = new DeviceCompilationSnapshot(
            checked((ulong)Interlocked.Increment(ref s_nextSemanticGeneration)),
            _options.ResourceHeapTier,
            queues,
            _options.SupportsEnhancedBarriers,
            _options.SupportsAsyncCompute,
            _options.SupportsCopyQueue);
        Info = new DeviceInfo(_options.DeviceName, BackendKind.Null, HardwareAccelerated: false);
    }

    public DeviceDomain Domain => _domain;
    public DeviceInfo Info { get; }
    public DeviceCompilationSnapshot Compilation { get; }
    public Statistics Statistics { get { lock (_gate) return _statistics; } }

    public ResourceRequirements GetBufferRequirements(
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal)
    {
        desc.Validate();
        if (!Enum.IsDefined(memoryType)) throw new ArgumentOutOfRangeException(nameof(memoryType));
        ValidateBufferMemoryUsage(desc, memoryType);
        const ulong alignment = 256;
        return new ResourceRequirements(
            AlignUp(desc.Size, alignment),
            alignment,
            memoryType,
            ResourceHeapClass.Buffer,
            CompatibilityClass(ResourceHeapClass.Buffer, desc.Usage.HasFlag(BufferUsage.ShaderWrite)));
    }

    public ResourceRequirements GetTextureRequirements(in TextureDesc desc)
    {
        desc.Validate();
        ulong alignment = desc.Usage.HasFlag(TextureUsage.ColorAttachment) || desc.Usage.HasFlag(TextureUsage.DepthStencilAttachment)
            ? 65_536UL
            : 4_096UL;
        ResourceHeapClass resourceClass = desc.Usage.HasFlag(TextureUsage.ColorAttachment) || desc.Usage.HasFlag(TextureUsage.DepthStencilAttachment)
            ? ResourceHeapClass.RenderTargetOrDepth
            : ResourceHeapClass.Texture;
        return new ResourceRequirements(
            AlignUp(TextureLayout.GetByteSize(desc), alignment),
            alignment,
            MemoryType.DeviceLocal,
            resourceClass,
            TextureCompatibilityClass(desc, resourceClass));
    }

    public TextureCopyFootprint GetTextureCopyFootprint(
        in TextureDesc desc,
        in TextureCopyRegion region,
        ulong requestedBufferOffset = 0)
    {
        desc.Validate();
        (int width, int height, int depth, int bytesPerTexel) = TextureLayout.ValidateCopyRegion(desc, region);
        uint rowSize = checked((uint)(width * bytesPerTexel));
        uint rowsPerImage = checked((uint)height);
        ulong size = checked((ulong)(depth - 1) * rowsPerImage * rowSize + (ulong)(height - 1) * rowSize + rowSize);
        return new TextureCopyFootprint(new TextureBufferLayout(requestedBufferOffset, rowSize, rowsPerImage), rowSize, size);
    }

    public ICommandContext AcquireCommandContext(QueueType queue, string? name = null)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            RequireSupportedQueue(queue);
            _statistics = _statistics with { CommandContextAcquires = _statistics.CommandContextAcquires + 1 };
            return new CommandContext(this, queue, name);
        }
    }

    public GpuCompletion Submit(QueueType queue, ReadOnlySpan<CommandListHandle> commandLists, ReadOnlySpan<GpuCompletion> waits = default)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            RequireSupportedQueue(queue);
            if (commandLists.IsEmpty) throw new ArgumentException("At least one command list is required.", nameof(commandLists));
            ValidateWaits(waits);

            HashSet<CommandListHandle> unique = [];
            CommandListRecord[] records = new CommandListRecord[commandLists.Length];
            for (int index = 0; index < commandLists.Length; index++)
            {
                CommandListHandle handle = commandLists[index];
                if (!unique.Add(handle)) throw ValidationError("A command list cannot appear twice in one submission.");
                CommandListRecord record = RequireCommandList(handle);
                if (record.Queue != queue)
                {
                    throw ValidationError($"Command list queue {record.Queue} does not match submission queue {queue}.");
                }
                if (!record.ReferencesPinned)
                {
                    throw new InvalidOperationException("A live command list has lost its unpublished reference pins.");
                }
                records[index] = record;
            }

            ulong signal = checked(_submitted[(int)queue] + 1);
            SubmissionState staged = ValidateSubmission(records);
            staged.Commit();

            _submitted[(int)queue] = signal;
            for (int index = 0; index < commandLists.Length; index++)
            {
                CommandListHandle handle = commandLists[index];
                SubmitReferencePins(records[index].References, queue, signal);
                records[index].ReferencesPinned = false;
                _commandLists.MarkUsed(handle.Domain, handle.Slot, handle.Generation, queue, signal);
                _commandLists.Destroy(handle.Domain, handle.Slot, handle.Generation);
            }

            _statistics = _statistics with
            {
                Submissions = _statistics.Submissions + 1,
                SubmissionWaits = _statistics.SubmissionWaits + waits.Length,
                SubmittedCommandLists = _statistics.SubmittedCommandLists + commandLists.Length,
            };
            if (_options.AutoCompleteSubmissions)
            {
                _completed[(int)queue] = signal;
                Monitor.PulseAll(_gate);
            }
            return new GpuCompletion(_domain, queue, signal);
        }
    }

    public void DiscardCommandList(CommandListHandle commandList)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            CommandListRecord record = RequireCommandList(commandList);
            if (!record.ReferencesPinned)
            {
                throw new InvalidOperationException("A live command list has lost its unpublished reference pins.");
            }
            CancelReferencePins(record.References);
            record.ReferencesPinned = false;
            _commandLists.Destroy(commandList.Domain, commandList.Slot, commandList.Generation);
            _commandLists.Collect(_completed);
        }
    }

    public ulong GetCompletedValue(QueueType queue)
    {
        lock (_gate)
        {
            EnsureNotDisposed();
            RequireSupportedQueue(queue);
            return _completed[(int)queue];
        }
    }

    public bool Wait(in GpuCompletion completion, TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        lock (_gate)
        {
            EnsureNotDisposed();
            ValidateCompletion(completion, nameof(completion));
            if (_completed[(int)completion.Queue] >= completion.Value) return true;
            if (timeout == TimeSpan.Zero) return false;

            long started = timeout == Timeout.InfiniteTimeSpan ? 0 : Environment.TickCount64;
            TimeSpan remaining = timeout;
            while (_completed[(int)completion.Queue] < completion.Value)
            {
                if (!Monitor.Wait(_gate, remaining)) return false;
                EnsureNotDisposed();
                if (timeout == Timeout.InfiniteTimeSpan) continue;
                long elapsedMilliseconds = Environment.TickCount64 - started;
                if (elapsedMilliseconds >= timeout.TotalMilliseconds) return false;
                remaining = timeout - TimeSpan.FromMilliseconds(elapsedMilliseconds);
            }
            return true;
        }
    }

    /// <summary>Test hook that advances one simulated queue without a device-wide idle.</summary>
    public void AdvanceCompletion(in GpuCompletion completion)
    {
        lock (_gate)
        {
            EnsureNotDisposed();
            ValidateCompletion(completion, nameof(completion));
            _completed[(int)completion.Queue] = Math.Max(_completed[(int)completion.Queue], completion.Value);
            Monitor.PulseAll(_gate);
        }
    }

    public int CollectGarbage()
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            int retired = 0;
            retired += _commandLists.Collect(_completed);
            retired += _bindGroups.Collect(_completed);
            retired += _pipelines.Collect(_completed);
            retired += _pipelineLayouts.Collect(_completed);
            retired += _shaders.Collect(_completed);
            retired += _bindGroupLayouts.Collect(_completed);
            retired += _textureViews.Collect(_completed);
            retired += _bufferViews.Collect(_completed);
            retired += _samplers.Collect(_completed);
            retired += _textures.Collect(_completed);
            retired += _buffers.Collect(_completed);
            retired += _heaps.Collect(_completed);
            _statistics = _statistics with
            {
                GarbageCollections = _statistics.GarbageCollections + 1,
                RetiredObjects = _statistics.RetiredObjects + retired,
            };
            return retired;
        }
    }

    public GraphicsDiagnostic[] DrainDiagnostics()
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            GraphicsDiagnostic[] result = _diagnostics.ToArray();
            _diagnostics.Clear();
            return result;
        }
    }

    public void Dispose()
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            if (_disposed) return;
            for (int queue = 0; queue < _completed.Length; queue++) _completed[queue] = _submitted[queue];
            foreach ((_, GenerationRegistry<CommandListRecord>.Slot slot) in _commandLists.Occupied())
            {
                CommandListRecord record = slot.Value!;
                if (!record.ReferencesPinned) continue;
                CancelReferencePins(record.References);
                record.ReferencesPinned = false;
            }
            _commandLists.Clear();
            _bindGroups.Clear();
            _pipelines.Clear();
            _pipelineLayouts.Clear();
            _shaders.Clear();
            _bindGroupLayouts.Clear();
            _textureViews.Clear();
            _bufferViews.Clear();
            _samplers.Clear();
            _textures.Clear();
            _buffers.Clear();
            _heaps.Clear();
            _disposed = true;
            Monitor.PulseAll(_gate);
        }
    }

    internal CommandListHandle PublishCommandList(QueueType queue, RecordedCommand[] commands, CommandReferences references, string? name)
    {
        lock (_gate)
        {
            EnsureNotDisposed();
            RequireSupportedQueue(queue);
            ExpandAndPinReferences(references);
            try
            {
                (uint slot, uint generation) = _commandLists.Allocate(new CommandListRecord
                {
                    Queue = queue,
                    Commands = commands,
                    References = references,
                    Name = name,
                    ReferencesPinned = true,
                });
                _statistics = _statistics with
                {
                    CommandListFinishes = _statistics.CommandListFinishes + 1,
                    RecordedCommands = _statistics.RecordedCommands + commands.Length,
                };
                return new CommandListHandle(_domain, slot, generation);
            }
            catch
            {
                CancelReferencePins(references);
                throw;
            }
        }
    }

    internal void EnsureAvailableForContext()
    {
        lock (_gate) EnsureNotDisposed();
    }

    internal InvalidOperationException ValidationError(string message)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _diagnostics.Add(new GraphicsDiagnostic(GraphicsDiagnosticSeverity.Error, "Null", message));
            }
        }
        return new InvalidOperationException(message);
    }

    private void ValidateWaits(ReadOnlySpan<GpuCompletion> waits)
    {
        foreach (ref readonly GpuCompletion wait in waits)
        {
            ValidateCompletion(wait, nameof(waits));
        }
    }

    private void ValidateCompletion(in GpuCompletion completion, string parameter)
    {
        if (!completion.IsValid) throw new ArgumentException("A valid completion is required.", parameter);
        if (completion.Domain != _domain) throw new ArgumentException("The completion belongs to another device.", parameter);
        RequireSupportedQueue(completion.Queue);
        if (completion.Value > _submitted[(int)completion.Queue])
        {
            throw new ArgumentException(
                $"Queue {completion.Queue} has not published completion value {completion.Value}.",
                parameter);
        }
    }

    private CommandListRecord RequireCommandList(CommandListHandle handle) =>
        _commandLists.RequireAlive(handle.Domain, handle.Slot, handle.Generation).Value!;

    private void RequireSupportedQueue(QueueType queue)
    {
        if (!Enum.IsDefined(queue) || !Compilation.Supports(queue))
        {
            throw new NotSupportedException($"Queue {queue} is not supported by this device.");
        }
    }

    private void EnsureCoordinatorThread()
    {
        int current = Environment.CurrentManagedThreadId;
        if (current != _coordinatorThreadId)
        {
            throw new InvalidOperationException($"Device mutation belongs to coordinator thread {_coordinatorThreadId}, not {current}.");
        }
    }

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static ulong AlignUp(ulong value, ulong alignment) => checked((value + alignment - 1) / alignment * alignment);
    private static ulong CompatibilityClass(ResourceHeapClass resourceClass, bool specialized) =>
        ((ulong)resourceClass << 32) | (specialized ? 1UL : 0UL);

    private static ulong TextureCompatibilityClass(in TextureDesc desc, ResourceHeapClass resourceClass) =>
        desc.CompatibilitySignature() ^
        ((ulong)resourceClass << 61) ^
        (TextureLayout.IsDepth(desc.Format) ? 1UL << 60 : 0UL);
}
