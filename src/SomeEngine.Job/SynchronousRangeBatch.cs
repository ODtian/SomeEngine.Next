using System.Runtime.CompilerServices;

namespace SomeEngine.Job;

internal abstract unsafe class SynchronousRangeBatch
{
    private readonly delegate*<SynchronousRangeBatch, Scheduler, uint, int, void> _execute;

    protected SynchronousRangeBatch(delegate*<SynchronousRangeBatch, Scheduler, uint, int, void> execute)
        => _execute = execute;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Execute(Scheduler scheduler, uint epoch, int workerIndex)
        => _execute(this, scheduler, epoch, workerIndex);
}
