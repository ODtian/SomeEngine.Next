using System.Collections.Concurrent;
using Vortice.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

public sealed partial class Device : IDevice
{
    private readonly DeviceDomain _domain;
    private readonly Options _options;
    private readonly NativeContext _native;
    private readonly int _coordinatorThread;
    private readonly ConcurrentQueue<GraphicsDiagnostic> _diagnostics = new();
    private readonly ConcurrentDictionary<Format, bool> _averageResolveSupport = new();
    private readonly object _requirementGate = new();
    private readonly Dictionary<(BufferDesc Desc, MemoryType MemoryType), ResourceRequirements> _bufferRequirements = [];
    private readonly Dictionary<TextureDesc, ResourceRequirements> _textureRequirements = [];
    private readonly CpuDescriptorPool _cpuDescriptors;
    private readonly HandleTable<NativeHeap> _heaps;
    private readonly HandleTable<NativeBuffer> _buffers;
    private readonly HandleTable<NativeTexture> _textures;
    private readonly HandleTable<RecordedCommand> _commands;
    private readonly object _retirementGate = new();
    private readonly List<RetiredNative> _retiredObjects = new();
    private readonly List<RetiredAllocation> _retiredAllocations = new();
    private readonly object[] _allocationGates = [new(), new(), new()];
    private readonly Stack<CommandAllocation>[] _availableAllocations = [new(), new(), new()];
    private int _submissionThread;
    private int _nativeBufferRequirementQueries;
    private int _nativeTextureRequirementQueries;
    private bool _lost;
    private bool _disposed;

    public Device(Options? options = null)
    {
        _options = options ?? new Options();
        if (_options.ResourceDescriptorsPerCommandList <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Resource descriptor capacity must be positive.");
        if (_options.SamplerDescriptorsPerCommandList <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Sampler descriptor capacity must be positive.");
        _domain = DeviceDomain.Allocate();
        _heaps = new(_domain);
        _buffers = new(_domain);
        _textures = new(_domain);
        _commands = new(_domain);
        _textureViews = new(_domain);
        _bufferViews = new(_domain);
        _samplers = new(_domain);
        _bindGroupLayouts = new(_domain);
        _bindGroups = new(_domain);
        _shaders = new(_domain);
        _pipelineLayouts = new(_domain);
        _pipelines = new(_domain);
        _native = NativeContext.Create(_options);
        _cpuDescriptors = new CpuDescriptorPool(_native.Device);
        _coordinatorThread = Environment.CurrentManagedThreadId;
    }

    public DeviceDomain Domain => _domain;
    public DeviceInfo Info => _native.Info;
    public DeviceCompilationSnapshot Compilation => _native.Compilation;
    internal ID3D12Device NativeDevice => _native.Device;
    internal int CpuDescriptorHeapCount => _cpuDescriptors.HeapCount;
    internal int OutstandingCpuDescriptorCount => _cpuDescriptors.OutstandingDescriptorCount;
    internal int NativeBufferRequirementQueryCount => Volatile.Read(ref _nativeBufferRequirementQueries);
    internal int NativeTextureRequirementQueryCount => Volatile.Read(ref _nativeTextureRequirementQueries);

    internal bool SupportsAverageTextureResolve(Format format) =>
        _averageResolveSupport.GetOrAdd(format, QueryAverageTextureResolveSupport);

    private bool QueryAverageTextureResolveSupport(Format format)
    {
        FeatureDataFormatSupport support = new()
        {
            Format = Mappings.Format(format),
        };
        return _native.Device.CheckFeatureSupport(Feature.FormatSupport, ref support) &&
            (support.Support1 & FormatSupport1.MultisampleResolve) != 0;
    }

    public ICommandContext AcquireCommandContext(QueueType queue, string? name = null)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        if (!Compilation.Supports(queue)) throw new NotSupportedException($"Queue '{queue}' is not supported by this device.");

        CommandAllocation allocation;
        int index = (int)queue;
        lock (_allocationGates[index])
        {
            if (_availableAllocations[index].TryPop(out CommandAllocation? available))
            {
                allocation = available;
                allocation.Allocator.Reset();
                allocation.List.Reset(allocation.Allocator, null!);
                allocation.Descriptors.Reset();
            }
            else
            {
                CommandListType type = Mappings.CommandListType(queue);
                ID3D12CommandAllocator allocator = _native.Device.CreateCommandAllocator(type);
                ID3D12GraphicsCommandList list = _native.Device.CreateCommandList<ID3D12GraphicsCommandList>(0, type, allocator, null!);
                allocation = new CommandAllocation(
                    queue,
                    allocator,
                    list,
                    new CommandDescriptorArena(
                        _native.Device,
                        _options.ResourceDescriptorsPerCommandList,
                        _options.SamplerDescriptorsPerCommandList));
            }
        }

        if (!string.IsNullOrWhiteSpace(name)) allocation.Name = name;
        return new CommandContext(this, allocation);
    }

    public GpuCompletion Submit(QueueType queue, ReadOnlySpan<CommandListHandle> commandLists, ReadOnlySpan<GpuCompletion> waits = default)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        EnsureSubmissionOwner();
        if (!Compilation.Supports(queue)) throw new NotSupportedException($"Queue '{queue}' is not supported by this device.");
        if (commandLists.IsEmpty) throw new ArgumentException("At least one command list is required.", nameof(commandLists));

        HashSet<CommandListHandle> unique = [];
        RecordedCommand[] records = new RecordedCommand[commandLists.Length];
        ID3D12CommandList[] nativeLists = new ID3D12CommandList[commandLists.Length];
        for (int index = 0; index < commandLists.Length; index++)
        {
            CommandListHandle handle = commandLists[index];
            if (!unique.Add(handle)) throw new ArgumentException("A command list cannot appear twice in one submission.", nameof(commandLists));
            RecordedCommand record = _commands.Get(handle.Domain, handle.Slot, handle.Generation, "command list");
            if (record.Queue != queue) throw new ArgumentException($"Command list {handle} belongs to {record.Queue}, not {queue}.", nameof(commandLists));
            records[index] = record;
            nativeLists[index] = record.Allocation.List;
        }

        NativeQueue[] waitQueues = new NativeQueue[waits.Length];
        for (int index = 0; index < waits.Length; index++)
        {
            waitQueues[index] = ValidateCompletion(waits[index], nameof(waits));
        }

        NativeQueue destination = _native.GetQueue(queue);
        lock (destination.SubmissionGate)
        {
            ulong value = checked(destination.SubmittedValue + 1);
            try
            {
                for (int index = 0; index < waits.Length; index++)
                {
                    GpuCompletion wait = waits[index];
                    NativeQueue source = waitQueues[index];
                    destination.Queue.Wait(source.Fence, wait.Value).CheckError();
                }

                destination.Queue.ExecuteCommandLists(nativeLists);
                destination.Queue.Signal(destination.Fence, value).CheckError();
                destination.SubmittedValue = value;

                for (int index = 0; index < records.Length; index++)
                {
                    CommandListHandle handle = commandLists[index];
                    RecordedCommand removed = _commands.Remove(handle.Domain, handle.Slot, handle.Generation, "command list");
                    removed.MarkSubmitted(queue, value);
                    lock (_retirementGate) _retiredAllocations.Add(new RetiredAllocation(removed.Allocation, queue, value));
                }

                return new GpuCompletion(_domain, queue, value);
            }
            catch (Exception exception)
            {
                _lost = true;
                RecordFailure("Submit", exception);
                throw new InvalidOperationException("D3D12 submission failed; the device execution domain is no longer usable.", exception);
            }
        }
    }

    public void DiscardCommandList(CommandListHandle commandList)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        RecordedCommand record = _commands.Remove(commandList.Domain, commandList.Slot, commandList.Generation, "command list");
        record.Cancel();
        lock (_allocationGates[(int)record.Queue]) _availableAllocations[(int)record.Queue].Push(record.Allocation);
    }

    public ulong GetCompletedValue(QueueType queue)
    {
        ThrowIfDisposed();
        return _native.GetQueue(queue).Fence.CompletedValue;
    }

    public bool Wait(in GpuCompletion completion, TimeSpan timeout)
    {
        ThrowIfDisposed();
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(timeout));
        NativeQueue queue = ValidateCompletion(completion, nameof(completion));
        if (queue.Fence.CompletedValue >= completion.Value) return true;
        if (timeout == TimeSpan.Zero) return false;

        lock (queue.WaitGate)
        {
            if (queue.Fence.CompletedValue >= completion.Value) return true;
            while (queue.CompletionEvent.WaitOne(TimeSpan.Zero)) { }
            queue.Fence.SetEventOnCompletion(completion.Value, queue.CompletionEvent).CheckError();
            long started = timeout == Timeout.InfiniteTimeSpan ? 0 : Environment.TickCount64;
            while (queue.Fence.CompletedValue < completion.Value)
            {
                TimeSpan remaining = timeout == Timeout.InfiniteTimeSpan
                    ? timeout
                    : timeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
                if (timeout != Timeout.InfiniteTimeSpan && remaining <= TimeSpan.Zero) return false;
                if (!queue.CompletionEvent.WaitOne(remaining))
                    return queue.Fence.CompletedValue >= completion.Value;
            }
            return true;
        }
    }

    public int CollectGarbage()
    {
        EnsureCoordinator();
        ThrowIfDisposed();
        int collected = 0;
        lock (_retirementGate)
        {
            bool madeProgress;
            do
            {
                madeProgress = false;
                for (int index = 0; index < _retiredObjects.Count;)
                {
                    RetiredNative retired = _retiredObjects[index];
                    if (!retired.Point.IsComplete(_native) || !retired.Value.CanDisposeNative)
                    {
                        index++;
                        continue;
                    }
                    _retiredObjects.RemoveAt(index);
                    retired.Value.Dispose();
                    collected++;
                    madeProgress = true;
                }
            }
            while (madeProgress);

            for (int index = _retiredAllocations.Count - 1; index >= 0; index--)
            {
                RetiredAllocation retired = _retiredAllocations[index];
                if (_native.GetQueue(retired.Queue).Fence.CompletedValue < retired.Value) continue;
                _retiredAllocations.RemoveAt(index);
                lock (_allocationGates[(int)retired.Queue])
                {
                    _availableAllocations[(int)retired.Queue].Push(retired.Allocation);
                }
                collected++;
            }
        }
        return collected;
    }

    public GraphicsDiagnostic[] DrainDiagnostics()
    {
        List<GraphicsDiagnostic> diagnostics = [.. _native.DrainDiagnostics()];
        while (_diagnostics.TryDequeue(out GraphicsDiagnostic item)) diagnostics.Add(item);
        return diagnostics.ToArray();
    }

    internal NativeBuffer GetBuffer(BufferHandle handle) => _buffers.Get(handle.Domain, handle.Slot, handle.Generation, "buffer");
    internal NativeTexture GetTexture(TextureHandle handle) => _textures.Get(handle.Domain, handle.Slot, handle.Generation, "texture");

    internal CommandListHandle Register(CommandAllocation allocation, IReadOnlyCollection<NativeLifetime> usage)
    {
        RecordedCommand command = new(allocation, usage.ToArray());
        HandleKey key = _commands.Add(command);
        return new CommandListHandle(_domain, key.Slot, key.Generation);
    }

    internal void Discard(CommandAllocation allocation, IReadOnlyCollection<NativeLifetime> usage)
    {
        foreach (NativeLifetime item in usage) item.CancelPending();
        allocation.List.Close();
        lock (_allocationGates[(int)allocation.Queue]) _availableAllocations[(int)allocation.Queue].Push(allocation);
    }

    internal void RecordFailure(string operation, Exception exception) =>
        _diagnostics.Enqueue(new GraphicsDiagnostic(GraphicsDiagnosticSeverity.Error, "D3D12", $"{operation}: {exception.Message}"));

    internal RetirementPoint BeginRetirement(NativeLifetime value) => value.BeginRetirement();

    internal void ScheduleRetirement(NativeLifetime value, in RetirementPoint point)
    {
        if (point.IsComplete(_native) && value.CanDisposeNative)
        {
            value.Dispose();
            return;
        }
        lock (_retirementGate) _retiredObjects.Add(new RetiredNative(value, point));
    }

    private void EnsureCoordinator()
    {
        if (Environment.CurrentManagedThreadId != _coordinatorThread)
        {
            throw new InvalidOperationException("Resource lifetime and submission must be coordinated by the device owner thread.");
        }
    }

    private void EnsureSubmissionOwner()
    {
        int current = Environment.CurrentManagedThreadId;
        int existing = Interlocked.CompareExchange(ref _submissionThread, current, 0);
        if (existing != 0 && existing != current)
        {
            throw new InvalidOperationException("All submissions for a device must have one logical thread owner.");
        }
    }

    private void ThrowIfUnavailable()
    {
        ThrowIfDisposed();
        if (_lost) throw new InvalidOperationException("The D3D12 device execution domain is lost.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private NativeQueue ValidateCompletion(in GpuCompletion completion, string parameter)
    {
        if (!completion.IsValid) throw new ArgumentException("A valid completion is required.", parameter);
        if (completion.Domain != _domain) throw new ArgumentException("The completion belongs to another device.", parameter);
        NativeQueue queue = _native.GetQueue(completion.Queue);
        lock (queue.SubmissionGate)
        {
            if (completion.Value > queue.SubmittedValue)
            {
                throw new ArgumentException(
                    $"Queue {completion.Queue} has not published completion value {completion.Value}.",
                    parameter);
            }
        }
        return queue;
    }

    public void Dispose()
    {
        if (_disposed) return;
        EnsureCoordinator();

        foreach (NativeQueue queue in new[] { _native.Graphics, _native.Compute, _native.Copy })
        {
            if (queue.SubmittedValue != 0 && !Wait(new GpuCompletion(_domain, queue.Type, queue.SubmittedValue), _options.ShutdownTimeout))
            {
                _diagnostics.Enqueue(new GraphicsDiagnostic(GraphicsDiagnosticSeverity.Error, "D3D12", $"Timed out draining {queue.Type} during shutdown."));
            }
        }

        CollectGarbage();
        foreach (RecordedCommand command in _commands.Drain())
        {
            command.Cancel();
            command.Allocation.Dispose();
        }
        foreach (NativeBuffer buffer in _buffers.Drain()) buffer.Dispose();
        foreach (NativeTexture texture in _textures.Drain()) texture.Dispose();
        lock (_retirementGate)
        {
            // Forced shutdown still preserves the placed-resource-before-heap native lifetime.
            foreach (RetiredNative retired in _retiredObjects)
            {
                if (retired.Value is not NativeHeap) retired.Value.Dispose();
            }
            foreach (RetiredNative retired in _retiredObjects)
            {
                if (retired.Value is NativeHeap) retired.Value.Dispose();
            }
            foreach (RetiredAllocation retired in _retiredAllocations) retired.Allocation.Dispose();
            _retiredObjects.Clear();
            _retiredAllocations.Clear();
        }
        foreach (NativeHeap heap in _heaps.Drain()) heap.Dispose();
        for (int index = 0; index < _availableAllocations.Length; index++)
        {
            lock (_allocationGates[index])
            {
                foreach (CommandAllocation allocation in _availableAllocations[index]) allocation.Dispose();
                _availableAllocations[index].Clear();
            }
        }

        DisposePipelineState();
        _cpuDescriptors.Dispose();
        _native.Dispose();
        _disposed = true;
    }

    partial void DisposePipelineState();

    private readonly record struct RetiredNative(NativeLifetime Value, RetirementPoint Point);
    private readonly record struct RetiredAllocation(CommandAllocation Allocation, QueueType Queue, ulong Value);
}

internal sealed class CommandAllocation : IDisposable
{
    public CommandAllocation(
        QueueType queue,
        ID3D12CommandAllocator allocator,
        ID3D12GraphicsCommandList list,
        CommandDescriptorArena descriptors)
    {
        Queue = queue;
        Allocator = allocator;
        List = list;
        Descriptors = descriptors;
    }

    public QueueType Queue { get; }
    public ID3D12CommandAllocator Allocator { get; }
    public ID3D12GraphicsCommandList List { get; }
    public CommandDescriptorArena Descriptors { get; }
    public string? Name { get; set; }

    public void Dispose()
    {
        Descriptors.Dispose();
        List.Dispose();
        Allocator.Dispose();
    }
}

internal sealed class RecordedCommand
{
    private readonly NativeLifetime[] _usage;

    public RecordedCommand(CommandAllocation allocation, NativeLifetime[] usage)
    {
        Allocation = allocation;
        _usage = usage;
    }

    public CommandAllocation Allocation { get; }
    public QueueType Queue => Allocation.Queue;

    public void MarkSubmitted(QueueType queue, ulong value)
    {
        foreach (NativeLifetime item in _usage) item.MarkSubmitted(queue, value);
    }

    public void Cancel()
    {
        foreach (NativeLifetime item in _usage) item.CancelPending();
    }
}
