using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

public sealed partial class Scheduler
{
    /// <summary>
    /// An exception propagated along dependencies is one failure. Each handle may observe its
    /// result, but shutdown and enclosing task groups must not report that same failure again.
    /// </summary>
    private sealed class JobFailure(ExceptionDispatchInfo dispatchInfo)
    {
        private int _reported;
        internal ExceptionDispatchInfo DispatchInfo { get; } = dispatchInfo;
        internal bool Reported => Volatile.Read(ref _reported) != 0;
        internal void Report() => Volatile.Write(ref _reported, 1);
    }

    private sealed partial class CompletionStore
    {
        private readonly ConditionalWeakTable<ExceptionDispatchInfo, JobFailure> _failures = new();

        internal JobFailure Failure(ExceptionDispatchInfo dispatchInfo) =>
            _failures.GetValue(dispatchInfo, static failure => new JobFailure(failure));
    }
}
