namespace SomeEngine.Graphics;

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum ColorSpace : byte
{
    Srgb,
    ScRgb,
    Hdr10,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum PresentType : byte
{
    Immediate,
    Mailbox,
    Fifo,
}

/// <summary>Static HDR10 mastering and content-light metadata.</summary>
/// <remarks>
/// <para>Chromaticity coordinates use the CTA-861 integer encoding where 50,000 represents 1.0.
/// <see cref="MaximumMasteringLuminance"/> is expressed in nits and
/// <see cref="MinimumMasteringLuminance"/> in 0.0001-nit units, matching HDR10 transport metadata.</para>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; copied values remain readable.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct Hdr10Metadata(
    ushort RedPrimaryX,
    ushort RedPrimaryY,
    ushort GreenPrimaryX,
    ushort GreenPrimaryY,
    ushort BluePrimaryX,
    ushort BluePrimaryY,
    ushort WhitePointX,
    ushort WhitePointY,
    uint MaximumMasteringLuminance,
    uint MinimumMasteringLuminance,
    ushort MaximumContentLightLevel,
    ushort MaximumFrameAverageLightLevel);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct SwapchainConfig(
    uint Width,
    uint Height,
    Format Format,
    ColorSpace ColorSpace,
    PresentType PresentType,
    bool AllowTearing,
    uint MaximumFrameLatency,
    Hdr10Metadata? Hdr10Metadata = null);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>The Device's Graphics Queue at index zero is the immutable presentation owner. Submit and Present for every SwapchainImage must use that exact Queue.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct SwapchainDesc(
    Surface Surface,
    uint ImageCount,
    TextureUsages ImageUsages,
    SwapchainConfig Config,
    string? Label = null);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct SwapchainSupport(
    Format Format,
    ColorSpace ColorSpace,
    PresentType PresentType,
    bool TearingSupported);

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. This type has no Dispose operation.</para>
/// <para><b>Ownership:</b> Borrowed or caller-supplied managed identity; it owns no independent native lifetime unless a member explicitly says otherwise.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; associated RHI objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct SwapchainAcquireOptions(
    TimeSpan Timeout,
    bool PreserveContents = false);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum SwapchainAcquireStatus : byte
{
    Success,
    Timeout,
    OutOfDate,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum PresentStatus : byte
{
    Success,
    Suboptimal,
    Occluded,
    OutOfDate,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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

    internal void RestoreSubmittedAfterPresentFailure(ulong sequence)
    {
        Validate(sequence);
        if (Interlocked.CompareExchange(
                ref _status,
                (int)SwapchainImageStatus.Submitted,
                (int)SwapchainImageStatus.Presented) != (int)SwapchainImageStatus.Presented)
        {
            throw new InvalidOperationException(
                "The image cannot be restored to Submitted after a failed Present.");
        }
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

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. This type has no Dispose operation.</para>
/// <para><b>Ownership:</b> Borrowed, generation-scoped acquisition right owned by its Swapchain; copies share the same Submit and Present rights.</para>
/// <para><b>After Dispose:</b> This value has no Dispose operation; invalidation preserves only the exact Status branch and prevents payload reuse.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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
