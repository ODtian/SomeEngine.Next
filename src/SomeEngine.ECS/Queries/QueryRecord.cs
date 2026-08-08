namespace SomeEngine.ECS.Queries;

internal sealed class QueryRecord
{
    private int _acquisitionCount;

    public QueryRecord(
        QueryHandle handle,
        QueryDefinition definition,
        QueryState state,
        int acquisitionCount = 1,
        bool hasGeneratedPin = false)
    {
        if (acquisitionCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(acquisitionCount));
        Handle = handle;
        Definition = definition;
        State = state;
        _acquisitionCount = acquisitionCount;
        HasGeneratedPin = hasGeneratedPin;
    }

    private QueryRecord(QueryHandle nextHandle)
    {
        Handle = nextHandle;
    }

    public QueryHandle Handle { get; }

    public QueryDefinition Definition { get; } = null!;

    public QueryState State { get; } = null!;

    internal int AcquisitionCount => Volatile.Read(ref _acquisitionCount);

    internal bool IsActive => AcquisitionCount > 0;

    internal bool HasGeneratedPin { get; private set; }

    internal void PinGenerated()
    {
        if (HasGeneratedPin)
            throw new InvalidOperationException("Query record already has its generated lifetime pin.");
        Retain();
        HasGeneratedPin = true;
    }

    internal void Retain()
    {
        int next = checked(AcquisitionCount + 1);
        Volatile.Write(ref _acquisitionCount, next);
    }

    internal bool Release()
    {
        int current = AcquisitionCount;
        if (current <= 0)
            throw new InvalidOperationException("Query acquisition count is not active.");

        int next = current - 1;
        Volatile.Write(ref _acquisitionCount, next);
        return next == 0;
    }

    internal static QueryRecord Released(QueryHandle nextHandle) => new(nextHandle);
}

