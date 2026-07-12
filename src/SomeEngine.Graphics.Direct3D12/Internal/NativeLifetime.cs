namespace SomeEngine.Graphics.Direct3D12;

internal abstract class NativeLifetime : IDisposable
{
    private readonly object _gate = new();
    private readonly ulong[] _lastUse = new ulong[3];
    private int _pending;
    private bool _retiring;
    private bool _disposed;
    private string? _logicalName;

    public string? LogicalName
    {
        get { lock (_gate) return _logicalName; }
    }

    public void SetLogicalName(string? name)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _logicalName = name;
        }
    }

    public void PinPending()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_retiring) throw new InvalidOperationException("A retiring native object cannot be recorded again.");
            _pending++;
        }
    }

    public void CancelPending()
    {
        lock (_gate)
        {
            if (_pending <= 0) throw new InvalidOperationException("Native object pending-use count underflow.");
            _pending--;
        }
    }

    public void MarkSubmitted(QueueType queue, ulong value)
    {
        lock (_gate)
        {
            if (_pending <= 0) throw new InvalidOperationException("Native object was submitted without a pending pin.");
            _pending--;
            int index = (int)queue;
            _lastUse[index] = Math.Max(_lastUse[index], value);
        }
    }

    public RetirementPoint BeginRetirement()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_retiring) throw new InvalidOperationException("The native object is already retiring.");
            if (_pending != 0)
            {
                throw new InvalidOperationException("A resource referenced by an unsubmitted command list cannot be destroyed.");
            }
            _retiring = true;
            return new RetirementPoint(_lastUse[0], _lastUse[1], _lastUse[2]);
        }
    }

    /// <summary>
    /// Performs a read-only fence check for the last submitted use. Pending, unpublished command
    /// lists are intentionally not part of this query; it mirrors the public CPU-access contract,
    /// which guards submitted GPU work without mutating lifetime ownership.
    /// </summary>
    public bool HasCompletedLastUse(NativeContext context)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return context.Graphics.Fence.CompletedValue >= _lastUse[0] &&
                   context.Compute.Fence.CompletedValue >= _lastUse[1] &&
                   context.Copy.Fence.CompletedValue >= _lastUse[2];
        }
    }

    /// <summary>
    /// Reports whether native dependency objects may be released. Fence completion alone is not
    /// sufficient for parents such as a placed-resource heap whose child COM objects still exist.
    /// </summary>
    public virtual bool CanDisposeNative => true;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        DisposeNative();
    }

    protected abstract void DisposeNative();
}

internal readonly record struct RetirementPoint(ulong Graphics, ulong Compute, ulong Copy)
{
    public bool IsComplete(NativeContext context) =>
        context.Graphics.Fence.CompletedValue >= Graphics &&
        context.Compute.Fence.CompletedValue >= Compute &&
        context.Copy.Fence.CompletedValue >= Copy;
}
