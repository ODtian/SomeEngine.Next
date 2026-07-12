namespace SomeEngine.Graphics.Null;

public sealed partial class Device
{
    private readonly Dictionary<object, string> _objectNames = [];

    public BufferMapping MapBuffer(BufferHandle buffer, BufferMapMode mode, in BufferRange range)
    {
        EnsureCoordinatorThread();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        lock (_gate)
        {
            EnsureNotDisposed();
            BufferRecord record = RequireBuffer(buffer);
            if (mode == BufferMapMode.Write && record.MemoryType != MemoryType.Upload)
                throw ValidationError("Write mappings require upload memory.");
            if (mode == BufferMapMode.Read && record.MemoryType != MemoryType.Readback)
                throw ValidationError("Read mappings require readback memory.");
            if (record.IsMapped) throw ValidationError("A buffer permits only one active mapping lease.");
            if (!_buffers.HasCompletedLastUse(buffer.Domain, buffer.Slot, buffer.Generation, _completed))
                throw ValidationError("A buffer cannot be mapped before its exact queue uses have completed.");
            ResolveBufferRange(record.Desc, range, out ulong offset, out ulong size);
            record.IsMapped = true;
            Span<byte> memory = record.Storage.AsSpan(
                checked(record.BaseOffset + (int)offset),
                checked((int)size));
            try
            {
                return new BufferMapping(
                    memory,
                    new NullBufferMappingOwner(() => ReleaseMapping(buffer)),
                    mode,
                    new BufferRange(offset, size));
            }
            catch
            {
                record.IsMapped = false;
                throw;
            }
        }
    }

    public bool WaitIdle(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        GpuCompletion[] snapshot;
        lock (_gate)
        {
            EnsureNotDisposed();
            snapshot = Compilation.Queues
                .Select(queue => new GpuCompletion(_domain, queue, _submitted[(int)queue]))
                .Where(static completion => completion.Value != 0)
                .ToArray();
        }
        long started = timeout == Timeout.InfiniteTimeSpan ? 0 : Environment.TickCount64;
        foreach (GpuCompletion completion in snapshot)
        {
            TimeSpan remaining = timeout == Timeout.InfiniteTimeSpan
                ? timeout
                : timeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
            if (timeout != Timeout.InfiniteTimeSpan && remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            if (!Wait(completion, remaining)) return false;
        }
        return true;
    }

    public void SetName(HeapHandle heap, string? name) => SetObjectName(heap, name, () => _ = RequireHeap(heap));
    public void SetName(BufferHandle buffer, string? name) => SetObjectName(buffer, name, () => _ = RequireBuffer(buffer));
    public void SetName(TextureHandle texture, string? name) => SetObjectName(texture, name, () => _ = RequireTexture(texture));
    public void SetName(TextureViewHandle view, string? name) => SetObjectName(view, name, () => _ = RequireTextureView(view));
    public void SetName(BufferViewHandle view, string? name) => SetObjectName(view, name, () => _ = RequireBufferView(view));
    public void SetName(SamplerHandle sampler, string? name) => SetObjectName(sampler, name, () => _ = RequireSampler(sampler));
    public void SetName(BindGroupLayoutHandle layout, string? name) => SetObjectName(layout, name, () => _ = RequireBindGroupLayout(layout));
    public void SetName(BindGroupHandle group, string? name) => SetObjectName(group, name, () => _ = RequireBindGroup(group));
    public void SetName(ShaderHandle shader, string? name) => SetObjectName(shader, name, () => _ = RequireShader(shader));
    public void SetName(PipelineLayoutHandle layout, string? name) => SetObjectName(layout, name, () => _ = RequirePipelineLayout(layout));
    public void SetName(PipelineHandle pipeline, string? name) => SetObjectName(pipeline, name, () => _ = RequirePipeline(pipeline));
    public void SetName(CommandListHandle commandList, string? name) => SetObjectName(commandList, name, () => _ = RequireCommandList(commandList));
    public void SetName(QueryPoolHandle pool, string? name) => SetObjectName(pool, name, () => _ = RequireQueryPool(pool));
    public void SetName(SwapchainHandle swapchain, string? name) => SetObjectName(swapchain, name, () => _ = RequireSwapchain(swapchain));
    public void SetName(BindlessTableHandle table, string? name) => SetObjectName(table, name, () => _ = RequireBindlessTable(table));

    private void SetObjectName<T>(T handle, string? name, Action validate) where T : struct
    {
        EnsureCoordinatorThread();
        if (name is not null) ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            EnsureNotDisposed();
            validate();
            object key = handle;
            if (name is null) _objectNames.Remove(key);
            else _objectNames[key] = name;
        }
    }

    private void ReleaseMapping(BufferHandle buffer)
    {
        lock (_gate)
        {
            if (_disposed) return;
            BufferRecord record = RequireBuffer(buffer);
            if (!record.IsMapped) throw new InvalidOperationException("The buffer mapping lease was already released.");
            record.IsMapped = false;
        }
    }

    private sealed class NullBufferMappingOwner : IBufferMappingOwner
    {
        private Action? _release;

        public NullBufferMappingOwner(Action release) => _release = release;

        public bool IsDisposed => Volatile.Read(ref _release) is null;

        public void Dispose()
        {
            Action? release = Interlocked.Exchange(ref _release, null);
            release?.Invoke();
        }
    }
}
