namespace SomeEngine.Graphics;

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly struct QueueCompletion : IEquatable<QueueCompletion>
{
    private readonly Queue? _queue;
    private readonly ulong _value;

    internal QueueCompletion(Queue queue, ulong value)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        if (value is 0 or ulong.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value));
        _value = value;
    }

    public Queue Queue => _queue
        ?? throw new InvalidOperationException("The default QueueCompletion is invalid.");

    public ulong Value
    {
        get
        {
            _ = Queue;
            return _value;
        }
    }

    internal bool IsInitialized => _queue is not null && _value is > 0 and < ulong.MaxValue;

    public bool Equals(QueueCompletion other) =>
        ReferenceEquals(_queue, other._queue) && _value == other._value;

    public override bool Equals(object? obj) =>
        obj is QueueCompletion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_queue, _value);

    public static bool operator ==(QueueCompletion left, QueueCompletion right) => left.Equals(right);
    public static bool operator !=(QueueCompletion left, QueueCompletion right) => !left.Equals(right);
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct TimelinePoint(ExternalTimeline Timeline, ulong Value);
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct TimelineSignal(ExternalTimeline Timeline, ulong Value);

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public abstract class ExternalTimeline : DeviceResource
{
    internal ExternalTimeline(Device device, string? label)
        : base(device, label)
    {
    }
}
