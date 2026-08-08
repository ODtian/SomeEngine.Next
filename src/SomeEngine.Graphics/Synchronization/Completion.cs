namespace SomeEngine.Graphics;

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

public readonly record struct TimelinePoint(ExternalTimeline Timeline, ulong Value);
public readonly record struct TimelineSignal(ExternalTimeline Timeline, ulong Value);

public abstract class ExternalTimeline : DeviceResource
{
    internal ExternalTimeline(Device device, string? label)
        : base(device, label)
    {
    }
}
