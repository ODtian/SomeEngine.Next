using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Render.Frame;

/// <summary>Exclusive render-data write access. Release finishes preparation.</summary>
public sealed class RenderPrepareScope : IDisposable
{
    private readonly object _gate = new();
    private RenderDataLock? _dataLock;
    private int _activeOperations;

    internal RenderPrepareScope(RenderDataLock dataLock) => _dataLock = dataLock;

    public bool IsClosed => Volatile.Read(ref _dataLock) is null;

    internal void EnterOperation()
    {
        lock (_gate)
        {
            DevelopmentCheck.Require(!IsClosed, "Render preparation requires an open write scope.");
            _activeOperations++;
        }
    }

    internal void ExitOperation()
    {
        lock (_gate) _activeOperations--;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DevelopmentCheck.Require(_activeOperations == 0, "Render systems must finish before releasing prepare.");
            Interlocked.Exchange(ref _dataLock, null)?.EndPrepare();
        }
    }
}
