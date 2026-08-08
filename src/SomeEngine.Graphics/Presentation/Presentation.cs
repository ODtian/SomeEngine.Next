namespace SomeEngine.Graphics;

public enum ColorSpace : byte
{
    Srgb,
    ScRgb,
    Hdr10,
}

public enum PresentType : byte
{
    Immediate,
    Mailbox,
    Fifo,
}

public readonly record struct SwapchainConfig(
    uint Width,
    uint Height,
    Format Format,
    ColorSpace ColorSpace,
    PresentType PresentType,
    bool AllowTearing,
    uint MaximumFrameLatency);

public readonly record struct SwapchainDesc(
    Surface Surface,
    uint ImageCount,
    TextureUsages ImageUsages,
    SwapchainConfig Config,
    string? Label = null);

public readonly record struct SwapchainSupport(
    Format Format,
    ColorSpace ColorSpace,
    PresentType PresentType,
    bool TearingSupported);

public sealed class SwapchainInfo
{
    private readonly SwapchainSupport[] _support;

    internal SwapchainInfo(
        in SwapchainConfig config,
        uint imageCount,
        ulong generation,
        ReadOnlySpan<SwapchainSupport> support)
    {
        Config = config;
        ImageCount = imageCount;
        Generation = generation;
        _support = support.ToArray();
    }

    public SwapchainConfig Config { get; internal set; }
    public uint ImageCount { get; }
    public ulong Generation { get; internal set; }
    public ReadOnlySpan<SwapchainSupport> Support => _support;
}

public abstract class Swapchain : DeviceResource
{
    internal Swapchain(
        Device device,
        Surface surface,
        SwapchainInfo info,
        TextureUsages imageUsages,
        string? label)
        : base(device, label)
    {
        Surface = surface ?? throw new ArgumentNullException(nameof(surface));
        Info = info ?? throw new ArgumentNullException(nameof(info));
        ImageUsages = imageUsages;
    }

    public Surface Surface { get; }
    public SwapchainInfo Info { get; }
    public TextureUsages ImageUsages { get; }
}

public readonly record struct SwapchainAcquireOptions(
    TimeSpan Timeout,
    bool PreserveContents = true);

public enum SwapchainAcquireStatus : byte
{
    Success,
    Timeout,
    OutOfDate,
}

public enum PresentStatus : byte
{
    Success,
    Suboptimal,
    Occluded,
    OutOfDate,
}

public enum ReconfigureStatus : byte
{
    Success,
    Busy,
    Unsupported,
}

internal abstract class SwapchainImageLease
{
    private long _sequence;
    private long _generation;
    private int _status = (int)SwapchainImageStatus.Invalidated;
    private Texture? _texture;
    private PipelineSync _initialSync;
    private ResourceAccess _initialAccess;
    private TextureLayout _initialLayout;

    protected SwapchainImageLease(Swapchain swapchain)
    {
        Swapchain = swapchain;
    }

    internal Swapchain Swapchain { get; }
    internal ulong CurrentSequence => unchecked((ulong)Volatile.Read(ref _sequence));

    internal void BeginAcquire(
        ulong sequence,
        ulong generation,
        Texture texture,
        PipelineSync initialSync,
        ResourceAccess initialAccess,
        TextureLayout initialLayout)
    {
        if (sequence is 0 or ulong.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        if (generation == 0)
            throw new ArgumentOutOfRangeException(nameof(generation));

        _texture = texture;
        _initialSync = initialSync;
        _initialAccess = initialAccess;
        _initialLayout = initialLayout;
        Volatile.Write(ref _generation, unchecked((long)generation));
        Volatile.Write(ref _sequence, unchecked((long)sequence));
        Volatile.Write(ref _status, (int)SwapchainImageStatus.Acquired);
    }

    internal void Validate(ulong sequence)
    {
        ulong generation = unchecked((ulong)Volatile.Read(ref _generation));
        if (sequence == 0 ||
            CurrentSequence != sequence ||
            generation == 0 ||
            Swapchain.Info.Generation != generation)
            throw new InvalidOperationException("The swapchain image sequence is stale or invalid.");
    }

    internal SwapchainImageStatus GetStatus(ulong sequence)
    {
        Validate(sequence);
        return (SwapchainImageStatus)Volatile.Read(ref _status);
    }

    internal Texture GetTexture(ulong sequence)
    {
        Validate(sequence);
        SwapchainImageStatus status = (SwapchainImageStatus)Volatile.Read(ref _status);
        if (status is SwapchainImageStatus.Invalidated or SwapchainImageStatus.DeviceLost)
            throw new InvalidOperationException("The swapchain image is no longer usable.");
        return _texture ?? throw new InvalidOperationException("The swapchain image has no texture.");
    }

    internal PipelineSync GetInitialSync(ulong sequence)
    {
        Validate(sequence);
        return _initialSync;
    }

    internal ResourceAccess GetInitialAccess(ulong sequence)
    {
        Validate(sequence);
        return _initialAccess;
    }

    internal TextureLayout GetInitialLayout(ulong sequence)
    {
        Validate(sequence);
        return _initialLayout;
    }

    internal bool TryBeginSubmit(ulong sequence)
    {
        Validate(sequence);
        return Interlocked.CompareExchange(
            ref _status,
            (int)SwapchainImageStatus.Submitted,
            (int)SwapchainImageStatus.Acquired) == (int)SwapchainImageStatus.Acquired;
    }

    internal void RestoreAcquired(ulong sequence)
    {
        Validate(sequence);
        if (Interlocked.CompareExchange(
                ref _status,
                (int)SwapchainImageStatus.Acquired,
                (int)SwapchainImageStatus.Submitted) != (int)SwapchainImageStatus.Submitted)
            throw new InvalidOperationException("The image cannot be restored to Acquired.");
    }

    internal bool TryBeginPresent(ulong sequence)
    {
        Validate(sequence);
        return Interlocked.CompareExchange(
            ref _status,
            (int)SwapchainImageStatus.Presented,
            (int)SwapchainImageStatus.Submitted) == (int)SwapchainImageStatus.Submitted;
    }

    internal void Invalidate(bool deviceLost)
    {
        Volatile.Write(
            ref _status,
            deviceLost
                ? (int)SwapchainImageStatus.DeviceLost
                : (int)SwapchainImageStatus.Invalidated);
    }
}

public readonly struct SwapchainImage
{
    private readonly SwapchainImageLease? _lease;
    private readonly ulong _sequence;

    internal SwapchainImage(SwapchainImageLease lease, ulong sequence)
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _sequence = sequence;
    }

    internal SwapchainImageLease Lease => _lease
        ?? throw new InvalidOperationException("The default SwapchainImage is invalid.");

    internal ulong Sequence => _sequence;

    public Swapchain Swapchain
    {
        get
        {
            SwapchainImageLease lease = Lease;
            lease.Validate(_sequence);
            return lease.Swapchain;
        }
    }

    public Texture Texture => Lease.GetTexture(_sequence);
    public PipelineSync InitialSync => Lease.GetInitialSync(_sequence);
    public ResourceAccess InitialAccess => Lease.GetInitialAccess(_sequence);
    public TextureLayout InitialLayout => Lease.GetInitialLayout(_sequence);
    public SwapchainImageStatus Status => Lease.GetStatus(_sequence);
}
