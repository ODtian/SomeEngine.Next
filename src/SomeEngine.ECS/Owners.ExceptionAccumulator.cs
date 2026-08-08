using System.Runtime.ExceptionServices;

namespace SomeEngine.ECS.Owners;

/// <summary>
/// Lets invariant-owning teardown finish its no-longer-failable publication
/// before user callback faults are rethrown to the caller.
/// </summary>
internal struct ExceptionAccumulator
{
    private List<Exception>? _exceptions;

    internal void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Add(exception);
        }
    }

    internal void Add(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        (_exceptions ??= new List<Exception>()).Add(exception);
    }

    internal void ThrowIfAny()
    {
        if (_exceptions is not { Count: > 0 } exceptions)
            return;
        if (exceptions.Count == 1)
            ExceptionDispatchInfo.Capture(exceptions[0]).Throw();

        throw new AggregateException(exceptions);
    }
}
