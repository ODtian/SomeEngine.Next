using System.Diagnostics;

namespace SomeEngine.Graphics.Tests;

internal sealed partial class StrictConformanceBackend
{
    public Swapchain CreateSwapchain(Device device, in SwapchainDesc desc)
    {
        ConformanceDevice native = RequireDevice(device);
        if (native.Presentation is null)
            throw Unsupported(nameof(Presentation));
        ConformanceSurface surface = desc.Surface as ConformanceSurface
            ?? throw new ArgumentException("The Surface has the wrong backend type.", nameof(desc));
        if (!ReferenceEquals(surface.Owner, this))
            throw new ArgumentException("The Surface belongs to another backend.", nameof(desc));
        surface.ThrowIfDisposed();
        ValidateSwapchainDescription(desc);
        var result = new ConformanceSwapchain(this, native, surface, desc);
        native.Register(result);
        return result;
    }

    public SwapchainAcquireStatus Acquire(
        Swapchain swapchain,
        in SwapchainAcquireOptions options,
        out SwapchainImage image)
    {
        _ = Timeouts.ToMilliseconds(options.Timeout, nameof(options));
        if (options.PreserveContents)
            throw new NotSupportedException("The strict backend does not preserve presented contents.");
        ConformanceSwapchain native = RequireResource(swapchain) as ConformanceSwapchain
            ?? throw new ArgumentException("The Swapchain has the wrong backend type.", nameof(swapchain));
        return native.Acquire(out image);
    }

    public PresentStatus Present(Queue queue, in SwapchainImage image)
    {
        ConformanceQueue nativeQueue = RequireQueue(queue);
        ConformanceSwapchainImageLease lease = image.Lease as ConformanceSwapchainImageLease
            ?? throw new ArgumentException("The SwapchainImage has the wrong backend type.", nameof(image));
        if (!ReferenceEquals(lease.Swapchain.Device, nativeQueue.Device) ||
            nativeQueue.Type != QueueType.Graphics || nativeQueue.Index != 0)
        {
            throw new ArgumentException("Presentation requires the owning Graphics Queue.", nameof(queue));
        }
        return lease.Present(nativeQueue, image.Sequence);
    }

    public ReconfigureStatus Reconfigure(Swapchain swapchain, in SwapchainConfig config)
    {
        ConformanceSwapchain native = RequireResource(swapchain) as ConformanceSwapchain
            ?? throw new ArgumentException("The Swapchain has the wrong backend type.", nameof(swapchain));
        ValidateSwapchainConfig(config, nameof(config));
        return native.Reconfigure(config);
    }

    public QueryPool CreateQueryPool(Device device, in QueryPoolDesc desc)
    {
        ConformanceDevice native = RequireDevice(device);
        if (desc.Count == 0 || desc.NodeIndex is not (uint.MaxValue or 0) ||
            desc.Type is QueryType.PipelineStatistics or QueryType.StreamOutputStatistics)
        {
            throw new NotSupportedException("The requested QueryPool is unavailable.");
        }
        _ = native.GetQueue(desc.QueueType, 0);
        QueryResultInfo info = new(8, 8, 8);
        var result = new ConformanceQueryPool(this, native, desc with { NodeIndex = 0 }, info);
        native.Register(result);
        return result;
    }

    public void BeginQuery(CommandContext context, QueryPool pool, uint queryIndex)
    {
        ConformanceCommandContext command = RequireContext(context);
        ConformanceQueryPool native = RequireQueryPool(command, pool, queryIndex);
        if (native.Description.Type == QueryType.Timestamp)
            throw new ArgumentException("Timestamp queries are written, not begun.", nameof(pool));
        command.Record(() => native.Begin(queryIndex));
    }

    public void EndQuery(CommandContext context, QueryPool pool, uint queryIndex)
    {
        ConformanceCommandContext command = RequireContext(context);
        ConformanceQueryPool native = RequireQueryPool(command, pool, queryIndex);
        if (native.Description.Type == QueryType.Timestamp)
            throw new ArgumentException("Timestamp queries are written, not ended.", nameof(pool));
        command.Record(() => native.End(queryIndex));
    }

    public void WriteTimestamp(CommandContext context, QueryPool pool, uint queryIndex)
    {
        ConformanceCommandContext command = RequireContext(context);
        ConformanceQueryPool native = RequireQueryPool(command, pool, queryIndex);
        if (native.Description.Type != QueryType.Timestamp)
            throw new ArgumentException("WriteTimestamp requires a Timestamp QueryPool.", nameof(pool));
        command.Record(() => native.Write(queryIndex, unchecked((ulong)Stopwatch.GetTimestamp())));
    }

    public void ResolveQueries(
        CommandContext context,
        QueryPool pool,
        uint firstQuery,
        uint queryCount,
        Buffer destination,
        in BufferRange destinationRange)
    {
        ConformanceCommandContext command = RequireContext(context);
        ConformanceQueryPool native = RequireQueryPool(command, pool, firstQuery);
        ConformanceBuffer buffer = RequireBuffer(
            (ConformanceDevice)command.Device,
            destination,
            nameof(destination));
        if (queryCount == 0 || firstQuery > native.Description.Count ||
            queryCount > native.Description.Count - firstQuery)
        {
            throw new ArgumentOutOfRangeException(nameof(queryCount));
        }
        BufferRange resolved = destinationRange.Resolve(buffer.Info.Size);
        ulong required = checked((ulong)queryCount * native.ResultInfo.ResultStride);
        if (resolved.Size < required)
            throw new ArgumentOutOfRangeException(nameof(destinationRange));
        byte[] storage = buffer.Storage;
        int storageOffset = checked(buffer.StorageOffset + (int)resolved.Offset);
        command.Record(() =>
        {
            for (uint offset = 0; offset < queryCount; offset++)
            {
                ulong value = native.Read(checked(firstQuery + offset));
                int resultOffset = checked(
                    storageOffset + (int)(offset * native.ResultInfo.ResultStride));
                BitConverter.TryWriteBytes(storage.AsSpan(resultOffset, sizeof(ulong)), value);
            }
        });
    }

    private static void ValidateSwapchainDescription(in SwapchainDesc desc)
    {
        if (desc.ImageCount < 2 || desc.ImageCount > 8 ||
            (desc.ImageUsages & TextureUsages.ColorAttachment) == 0)
        {
            throw new ArgumentException("The Swapchain description is invalid.", nameof(desc));
        }
        ValidateSwapchainConfig(desc.Config, nameof(desc));
    }

    private static void ValidateSwapchainConfig(
        in SwapchainConfig config,
        string parameterName)
    {
        if (config.Width == 0 || config.Height == 0 ||
            !Enum.IsDefined(config.Format) || !Enum.IsDefined(config.ColorSpace) ||
            !Enum.IsDefined(config.PresentType) || config.MaximumFrameLatency == 0)
        {
            throw new ArgumentException("The Swapchain configuration is invalid.", parameterName);
        }
        if (config.Hdr10Metadata is not null && config.ColorSpace != ColorSpace.Hdr10)
            throw new ArgumentException("HDR10 metadata requires the HDR10 color space.", parameterName);
    }

    private ConformanceQueryPool RequireQueryPool(
        ConformanceCommandContext context,
        QueryPool pool,
        uint queryIndex)
    {
        ConformanceQueryPool native = RequireResource(pool) as ConformanceQueryPool
            ?? throw new ArgumentException("The QueryPool has the wrong backend type.", nameof(pool));
        RequireSameDevice((ConformanceDevice)context.Device, native, nameof(pool));
        if (native.Description.QueueType != context.QueueType || queryIndex >= native.Description.Count)
            throw new ArgumentOutOfRangeException(nameof(queryIndex));
        return native;
    }

    private sealed class ConformanceSwapchain : Swapchain, IConformanceObject
    {
        private readonly object _gate = new();
        private ConformanceTexture[] _textures;
        private ConformanceSwapchainImageLease[] _leases;
        private ulong _nextSequence = 1;
        private int _nextImage;

        internal ConformanceSwapchain(
            StrictConformanceBackend owner,
            ConformanceDevice device,
            ConformanceSurface surface,
            in SwapchainDesc desc)
            : base(
                device,
                surface,
                new SwapchainInfo(
                    desc.Config,
                    desc.ImageCount,
                    1,
                    [new SwapchainSupport(
                        desc.Config.Format,
                        desc.Config.ColorSpace,
                        desc.Config.PresentType,
                        TearingSupported: true)]),
                desc.ImageUsages,
                desc.Label)
        {
            Owner = owner;
            (_textures, _leases) = CreateImages(desc.Config, desc.ImageCount);
        }

        public StrictConformanceBackend Owner { get; }

        internal SwapchainAcquireStatus Acquire(out SwapchainImage image)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                for (int attempt = 0; attempt < _leases.Length; attempt++)
                {
                    int index = (_nextImage + attempt) % _leases.Length;
                    ConformanceSwapchainImageLease lease = _leases[index];
                    if (lease.IsOutstanding)
                        continue;
                    ulong sequence = _nextSequence++;
                    if (sequence is 0 or ulong.MaxValue)
                        throw new InvalidOperationException("The acquisition sequence is exhausted.");
                    lease.Acquire(
                        sequence,
                        Info.Generation,
                        _textures[index],
                        PipelineSync.None,
                        ResourceAccess.NoAccess,
                        TextureLayout.Present);
                    _nextImage = (index + 1) % _leases.Length;
                    image = new SwapchainImage(lease, sequence);
                    return SwapchainAcquireStatus.Success;
                }
                image = default;
                return SwapchainAcquireStatus.Timeout;
            }
        }

        internal ReconfigureStatus Reconfigure(in SwapchainConfig config)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_leases.Any(static lease => lease.IsOutstanding))
                    return ReconfigureStatus.Busy;
                foreach (ConformanceSwapchainImageLease lease in _leases)
                    lease.Invalidate(deviceLost: false);
                foreach (ConformanceTexture texture in _textures)
                    texture.DisposeFromParent();
                Info.Config = config;
                Info.Generation = checked(Info.Generation + 1);
                (_textures, _leases) = CreateImages(config, Info.ImageCount);
                _nextImage = 0;
                return ReconfigureStatus.Success;
            }
        }

        internal override void Release(bool fromParent)
        {
            lock (_gate)
            {
                foreach (ConformanceSwapchainImageLease lease in _leases)
                    lease.Invalidate(deviceLost: false);
                foreach (ConformanceTexture texture in _textures)
                    texture.DisposeFromParent();
                _textures = [];
                _leases = [];
            }
            ((ConformanceDevice)Device).Unregister(this);
        }

        private (ConformanceTexture[] Textures, ConformanceSwapchainImageLease[] Leases)
            CreateImages(in SwapchainConfig config, uint imageCount)
        {
            var textures = new ConformanceTexture[checked((int)imageCount)];
            var leases = new ConformanceSwapchainImageLease[textures.Length];
            for (int index = 0; index < textures.Length; index++)
            {
                TextureDesc textureDesc = new(
                    TextureDimension.Texture2D,
                    config.Width,
                    config.Height,
                    1,
                    1,
                    1,
                    1,
                    config.Format,
                    ImageUsages,
                    label: $"{Label ?? "conformance swapchain"} image {index}");
                textures[index] = (ConformanceTexture)Owner.CreateTexture(Device, textureDesc);
                leases[index] = new ConformanceSwapchainImageLease(this);
            }
            return (textures, leases);
        }
    }

    private sealed class ConformanceSwapchainImageLease : SwapchainImageLease
    {
        private readonly object _gate = new();
        private ConformanceQueue? _submittedQueue;
        private ulong _completion;
        private bool _outstanding;

        internal ConformanceSwapchainImageLease(ConformanceSwapchain swapchain)
            : base(swapchain)
        {
        }

        internal bool IsOutstanding
        {
            get
            {
                lock (_gate)
                    return _outstanding;
            }
        }

        internal void Acquire(
            ulong sequence,
            ulong generation,
            Texture texture,
            PipelineSync initialSync,
            ResourceAccess initialAccess,
            TextureLayout initialLayout)
        {
            lock (_gate)
            {
                _submittedQueue = null;
                _completion = 0;
                BeginAcquire(sequence, generation, texture, initialSync, initialAccess, initialLayout);
                _outstanding = true;
            }
        }

        internal void MarkSubmission(ConformanceQueue queue, ulong completion)
        {
            lock (_gate)
            {
                _submittedQueue = queue;
                _completion = completion;
            }
        }

        internal PresentStatus Present(ConformanceQueue queue, ulong sequence)
        {
            lock (_gate)
            {
                Validate(sequence);
                if (!ReferenceEquals(queue, _submittedQueue) ||
                    _completion == 0 || queue.CompletedValue < _completion ||
                    !TryBeginPresent(sequence))
                {
                    throw new InvalidOperationException(
                        "Present requires the accepted submission for this acquisition.");
                }
                _outstanding = false;
                return PresentStatus.Success;
            }
        }
    }

    private sealed class ConformanceQueryPool : QueryPool, IConformanceObject
    {
        private readonly object _gate = new();
        private readonly ulong[] _values;
        private readonly bool[] _active;

        internal ConformanceQueryPool(
            StrictConformanceBackend owner,
            ConformanceDevice device,
            in QueryPoolDesc desc,
            in QueryResultInfo info)
            : base(device, desc, info)
        {
            Owner = owner;
            _values = new ulong[checked((int)desc.Count)];
            _active = new bool[_values.Length];
        }

        public StrictConformanceBackend Owner { get; }

        internal void Begin(uint index)
        {
            lock (_gate)
            {
                int slot = checked((int)index);
                if (_active[slot])
                    throw new InvalidOperationException("The query is already active.");
                _active[slot] = true;
            }
        }

        internal void End(uint index)
        {
            lock (_gate)
            {
                int slot = checked((int)index);
                if (!_active[slot])
                    throw new InvalidOperationException("The query is not active.");
                _active[slot] = false;
                _values[slot] = 1;
            }
        }

        internal void Write(uint index, ulong value)
        {
            lock (_gate)
                _values[checked((int)index)] = value;
        }

        internal ulong Read(uint index)
        {
            lock (_gate)
                return _values[checked((int)index)];
        }

        internal override void Release(bool fromParent) =>
            ((ConformanceDevice)Device).Unregister(this);
    }
}
