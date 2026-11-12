using System.Runtime.CompilerServices;

namespace SomeEngine.Job;

internal sealed class WorkQueueThread
{
    private static readonly Lock FreeLock = new();
    private static readonly Stack<WorkQueueThread> Free = new();
    private static int s_threadCount;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _finished = new(false);
    private readonly Thread _thread;
    private WorkQueue? _queue;
    private int _workerIndex;
    private bool _stop;

    private WorkQueueThread()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"SomeEngine.Job Worker {Interlocked.Increment(ref s_threadCount)}"
        };
        _thread.UnsafeStart();
    }

    internal static WorkQueueThread Rent(WorkQueue queue, int workerIndex)
    {
        WorkQueueThread worker;
        lock (FreeLock) worker = Free.Count == 0 ? new() : Free.Pop();
        worker._queue = queue;
        worker._workerIndex = workerIndex;
        worker._finished.Reset();
        worker._wake.Set();
        return worker;
    }

    internal void Return()
    {
        _finished.Wait();
        lock (FreeLock)
            if (Free.Count < JobConfiguration.DefaultWorkerCount)
            {
                Free.Push(this);
                return;
            }
        _stop = true;
        _wake.Set();
        _thread.Join();
        _wake.Dispose();
        _finished.Dispose();
    }

    private void Run()
    {
        while (true)
        {
            _wake.WaitOne();
            if (_stop) return;
            RunQueue();
            _finished.Set();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RunQueue()
    {
        WorkQueue queue = _queue!;
        _queue = null;
        queue.WorkerLoop(_workerIndex);
    }
}
