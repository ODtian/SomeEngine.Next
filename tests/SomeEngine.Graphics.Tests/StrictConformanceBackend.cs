using System.Diagnostics.CodeAnalysis;

namespace SomeEngine.Graphics.Tests;

/// <summary>
/// CPU-only strict backend used to prove that the public RHI contract is not defined by D3D12.
/// It implements the portable core and rejects every unsupported optional capability explicitly.
/// </summary>
internal sealed partial class StrictConformanceBackend : IGraphicsBackend
{
    private static readonly AdapterInfo Adapter = new(
        new AdapterId(0x434F4E464F524D41UL, 0x4E43454241434B45UL),
        AdapterType.Cpu,
        "SomeEngine strict conformance backend",
        0,
        0,
        0,
        0,
        1UL << 30,
        "managed-reference",
        HardwareAccelerated: false);

    private readonly object _gate = new();
    private readonly HashSet<ConformanceDevice> _devices =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ConformanceSurface> _surfaces =
        new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    public bool TryEnumerateAdapters(
        in AdapterEnumerationOptions options,
        Span<AdapterInfo> destination,
        out int requiredCount)
    {
        ThrowIfDisposed();
        requiredCount = options.IncludeSoftware ? 1 : 0;
        if (destination.Length < requiredCount)
            return false;
        if (requiredCount != 0)
            destination[0] = Adapter;
        return true;
    }

    public Device CreateDevice(in DeviceDesc desc)
    {
        ThrowIfDisposed();
        if (desc.AdapterId != Adapter.Id)
            throw new ArgumentException("The adapter does not belong to this backend.", nameof(desc));
        if (desc.Queues.IsEmpty)
            throw new ArgumentException("A Device requires at least one Queue.", nameof(desc));
        if (desc.EnabledNodeMask != 1)
            throw new NotSupportedException("The conformance backend exposes one device node.");
        DeviceFeatures supported = DeviceFeatures.Presentation;
        DeviceFeatures missing = desc.RequiredFeatures & ~supported;
        if (missing != DeviceFeatures.None)
            throw new NotSupportedException($"Required Device features are unavailable: {missing}.");

        var device = new ConformanceDevice(this, desc);
        lock (_gate)
        {
            ThrowIfDisposedUnderGate();
            _devices.Add(device);
        }
        return device;
    }

    public Surface CreateSurface(in SurfaceDesc desc)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(desc.Type) || desc.WindowHandle == 0)
            throw new ArgumentException("A valid native window identity is required.", nameof(desc));
        var surface = new ConformanceSurface(this, desc);
        lock (_gate)
        {
            ThrowIfDisposedUnderGate();
            _surfaces.Add(surface);
        }
        return surface;
    }

    public Queue GetQueue(Device device, QueueType type, uint index = 0) =>
        RequireDevice(device).GetQueue(type, index);

    public bool TryGetCapability<TCapability>(
        Device device,
        [NotNullWhen(true)] out TCapability? capability)
        where TCapability : DeviceCapability
    {
        ConformanceDevice native = RequireDevice(device);
        if (typeof(TCapability) == typeof(Presentation) && native.Presentation is not null)
        {
            capability = (TCapability)(object)native.Presentation;
            return true;
        }
        capability = null;
        return false;
    }

    public void CollectCompleted(Device device) => RequireDevice(device).ThrowIfUnavailable();

    public bool IsComplete(in QueueCompletion completion)
    {
        ConformanceQueue queue = RequireQueue(completion.Queue);
        return queue.CompletedValue >= completion.Value;
    }

    public WaitStatus WaitCpu(in QueueCompletion completion, TimeSpan timeout)
    {
        _ = Timeouts.ToMilliseconds(timeout, nameof(timeout));
        return IsComplete(completion) ? WaitStatus.Completed : WaitStatus.Timeout;
    }

    public void Dispose()
    {
        ConformanceDevice[] devices;
        ConformanceSurface[] surfaces;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            devices = [.. _devices];
            surfaces = [.. _surfaces];
        }
        foreach (ConformanceDevice device in devices)
            device.DisposeFromParent();
        foreach (ConformanceSurface surface in surfaces)
            surface.DisposeFromParent();
    }

    private ConformanceDevice RequireDevice(Device? device)
    {
        ArgumentNullException.ThrowIfNull(device);
        ThrowIfDisposed();
        if (device is not ConformanceDevice native || !ReferenceEquals(native.Owner, this))
            throw new ArgumentException("The Device does not belong to this backend.", nameof(device));
        native.ThrowIfUnavailable();
        return native;
    }

    private ConformanceQueue RequireQueue(Queue? queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ConformanceDevice device = RequireDevice(queue.Device);
        if (queue is not ConformanceQueue native || !ReferenceEquals(native.Device, device))
            throw new ArgumentException("The Queue does not belong to this backend.", nameof(queue));
        return native;
    }

    private T RequireResource<T>(T? value)
        where T : DeviceResource
    {
        ArgumentNullException.ThrowIfNull(value);
        _ = RequireDevice(value.Device);
        value.ThrowIfDisposed();
        if (value is not IConformanceObject owned || !ReferenceEquals(owned.Owner, this))
            throw new ArgumentException("The graphics object does not belong to this backend.", nameof(value));
        return value;
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
            ThrowIfDisposedUnderGate();
    }

    private void ThrowIfDisposedUnderGate() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private void Unregister(ConformanceDevice device)
    {
        lock (_gate)
            _devices.Remove(device);
    }

    private void Unregister(ConformanceSurface surface)
    {
        lock (_gate)
            _surfaces.Remove(surface);
    }

    private interface IConformanceObject
    {
        StrictConformanceBackend Owner { get; }
    }

    private sealed class ConformanceDevice : Device, IConformanceObject
    {
        private readonly object _gate = new();
        private readonly Dictionary<(QueueType Type, uint Index), ConformanceQueue> _queues = [];
        private readonly HashSet<GraphicsObject> _children =
            new(ReferenceEqualityComparer.Instance);

        internal ConformanceDevice(StrictConformanceBackend owner, in DeviceDesc desc)
            : base(StrictConformanceBackend.Adapter, CreateCapabilities(), desc.EnabledNodeMask, desc.Label)
        {
            Owner = owner;
            BackendOwner = owner;
            DeviceFeatures enabled =
                desc.RequiredFeatures | (desc.OptionalFeatures & DeviceFeatures.Presentation);
            Presentation = (enabled & DeviceFeatures.Presentation) != 0
                ? new Presentation(this)
                : null;
            var next = new Dictionary<QueueType, uint>();
            foreach (ref readonly DeviceQueueDesc queue in desc.Queues)
            {
                if (!Enum.IsDefined(queue.Type) || queue.Count == 0 || queue.NodeIndex != 0 ||
                    !float.IsFinite(queue.Priority) || queue.Priority is < 0 or > 1)
                {
                    throw new ArgumentException("The Queue description is invalid.", nameof(desc));
                }
                next.TryGetValue(queue.Type, out uint first);
                for (uint offset = 0; offset < queue.Count; offset++)
                {
                    uint index = checked(first + offset);
                    _queues.Add(
                        (queue.Type, index),
                        new ConformanceQueue(this, queue.Type, index, queue.Priority));
                }
                next[queue.Type] = checked(first + queue.Count);
            }
        }

        public StrictConformanceBackend Owner { get; }
        internal Presentation? Presentation { get; }

        internal ConformanceQueue GetQueue(QueueType type, uint index)
        {
            ThrowIfUnavailable();
            if (!_queues.TryGetValue((type, index), out ConformanceQueue? queue))
                throw new ArgumentOutOfRangeException(nameof(index));
            return queue;
        }

        internal void Register(GraphicsObject child)
        {
            lock (_gate)
            {
                ThrowIfUnavailable();
                _children.Add(child);
            }
        }

        internal void Unregister(GraphicsObject child)
        {
            lock (_gate)
                _children.Remove(child);
        }

        internal override void Release(bool fromParent)
        {
            GraphicsObject[] children;
            lock (_gate)
            {
                MarkDisposed();
                children = [.. _children];
                _children.Clear();
            }
            foreach (GraphicsObject child in children)
                child.DisposeFromParent();
            Owner.Unregister(this);
        }

        private static DeviceCapabilities CreateCapabilities()
        {
            Format[] formats = Enum.GetValues<Format>();
            var support = new FormatSupport[formats.Length];
            const FormatFeatures features =
                FormatFeatures.Buffer |
                FormatFeatures.VertexBuffer |
                FormatFeatures.IndexBuffer |
                FormatFeatures.Texture1D |
                FormatFeatures.Texture2D |
                FormatFeatures.Texture3D |
                FormatFeatures.TextureCube |
                FormatFeatures.ShaderLoad |
                FormatFeatures.ShaderSample |
                FormatFeatures.Mipmaps |
                FormatFeatures.ColorAttachment |
                FormatFeatures.Storage |
                FormatFeatures.StorageLoad |
                FormatFeatures.StorageStore;
            for (int index = 0; index < formats.Length; index++)
                support[index] = new FormatSupport(formats[index], features, SampleCounts.One, SampleCounts.None);
            DeviceLimits limits = new(
                MaximumBufferSize: 1UL << 32,
                MaximumTextureDimension1D: 16_384,
                MaximumTextureDimension2D: 16_384,
                MaximumTextureDimension3D: 2_048,
                MaximumTextureArrayLayers: 2_048,
                MaximumColorAttachments: 8,
                MaximumViewports: 16,
                ResourceDescriptorCapacity: 1_000_000,
                SamplerDescriptorCapacity: 2_048,
                ConstantBufferAlignment: 256,
                TextureDataPitchAlignment: 1,
                TextureDataPlacementAlignment: 1);
            return new DeviceCapabilities(
                limits,
                supportsBundles: true,
                supportsPipelineStatistics: false,
                supportsStreamOutputStatistics: false,
                supportsDepthBounds: false,
                supportedDynamicStates:
                    DynamicStates.Viewport |
                    DynamicStates.Scissor |
                    DynamicStates.BlendConstants |
                    DynamicStates.StencilReference |
                    DynamicStates.DepthBias |
                    DynamicStates.PrimitiveTopology |
                    DynamicStates.StripCut,
                support);
        }
    }

    private sealed class ConformanceQueue(
        ConformanceDevice device,
        QueueType type,
        uint index,
        float priority) : Queue(device, type, index, priority, 0)
    {
        private long _completedValue;

        internal ulong CompletedValue => unchecked((ulong)Volatile.Read(ref _completedValue));

        internal ulong CompleteNext()
        {
            long value = Interlocked.Increment(ref _completedValue);
            if (value <= 0 || value == long.MaxValue)
                throw new InvalidOperationException("The Queue completion domain is exhausted.");
            return unchecked((ulong)value);
        }
    }

    private sealed class ConformanceSurface : Surface, IConformanceObject
    {
        internal ConformanceSurface(StrictConformanceBackend owner, in SurfaceDesc desc)
            : base(desc.Type, desc.WindowHandle, desc.DisplayHandle, owner, desc.Label) =>
            Owner = owner;

        public StrictConformanceBackend Owner { get; }

        internal override void Release(bool fromParent) => Owner.Unregister(this);
    }
}
