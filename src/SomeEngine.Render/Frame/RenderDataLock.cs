using System.Diagnostics.CodeAnalysis;

namespace SomeEngine.Render.Frame;

/// <summary>Shares render-data reads and grants one exclusive prepare scope.</summary>
public sealed class RenderDataLock
{
    private int _accessCount;

    public bool TryBeginPrepare([NotNullWhen(true)] out RenderPrepareScope? scope)
    {
        if (Interlocked.CompareExchange(ref _accessCount, -1, 0) != 0)
        {
            scope = null;
            return false;
        }
        scope = new RenderPrepareScope(this);
        return true;
    }

    public bool TryBeginRead([NotNullWhen(true)] out RenderReadScope? scope)
    {
        int count = Volatile.Read(ref _accessCount);
        while (count >= 0)
        {
            int previous = Interlocked.CompareExchange(ref _accessCount, checked(count + 1), count);
            if (previous == count)
            {
                scope = new RenderReadScope(this);
                return true;
            }
            count = previous;
        }
        scope = null;
        return false;
    }

    public RenderDataAccessDiagnostics CaptureDiagnostics()
    {
        int count = Volatile.Read(ref _accessCount);
        return new RenderDataAccessDiagnostics(count < 0, Math.Max(count, 0));
    }

    internal void EndPrepare() => Volatile.Write(ref _accessCount, 0);
    internal void EndRead() => Interlocked.Decrement(ref _accessCount);
}

public readonly record struct RenderDataAccessDiagnostics(bool PrepareOpen, int OpenReaderCount);

/// <summary>Shared render-data access for frame recording, diagnostics, or capture.</summary>
public sealed class RenderReadScope : IDisposable
{
    private RenderDataLock? _dataLock;

    internal RenderReadScope(RenderDataLock dataLock) => _dataLock = dataLock;

    public bool IsClosed => Volatile.Read(ref _dataLock) is null;

    public void Dispose() => Interlocked.Exchange(ref _dataLock, null)?.EndRead();
}
