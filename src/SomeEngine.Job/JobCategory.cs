using System.Collections.Concurrent;

namespace SomeEngine.Job;

public sealed class JobCategory
{
    private readonly Lock _sync = new();
    private readonly LinkedList<Scheduler.CategoryWork> _waiting = new();
    private readonly ConcurrentDictionary<Type, WorkStream> _streams = new();
    private int _inFlight;
    private int _executing;

    internal JobCategory(Scheduler scheduler, JobCategoryConfig config)
    {
        Scheduler = scheduler;
        Config = config;
    }

    public string Name => Config.Name;
    internal Scheduler Scheduler { get; }
    internal JobCategoryConfig Config { get; }
    internal bool CanStartExecution => Volatile.Read(ref _executing) < Config.MaxExecuting;

    internal bool TryStartExecution()
    {
        int count = Volatile.Read(ref _executing);
        while (count < Config.MaxExecuting)
        {
            int observed = Interlocked.CompareExchange(ref _executing, count + 1, count);
            if (observed == count)
                return true;
            count = observed;
        }
        return false;
    }

    internal void CompleteExecution()
    {
        Interlocked.Decrement(ref _executing);
        Scheduler.CategoryExecutionFinished();
    }

    internal WorkStream<TItem> GetWorkStream<TItem>()
        where TItem : struct, IWorkStreamItem<TItem> =>
        (WorkStream<TItem>)_streams.GetOrAdd(typeof(TItem),
            static (_, category) => new WorkStream<TItem> { Category = category }, this);

    internal bool TryStartRequest()
    {
        lock (_sync)
        {
            if (_inFlight == Config.MaxInFlight)
                return false;
            _inFlight++;
            return true;
        }
    }

    internal void QueueRequest(Scheduler.CategoryWork work)
    {
        bool canceled;
        lock (_sync)
        {
            canceled = work.CancellationToken.IsCancellationRequested;
            if (!canceled)
            {
                if (_inFlight == Config.MaxInFlight)
                {
                    work.WaitingNode = _waiting.AddLast(work);
                    return;
                }
                _inFlight++;
            }
        }
        if (canceled)
            work.CancelWaiting();
        else
            work.Start();
    }

    internal bool CancelRequest(Scheduler.CategoryWork work)
    {
        lock (_sync)
        {
            if (work.WaitingNode is not { } node)
                return false;
            _waiting.Remove(node);
            work.WaitingNode = null;
            return true;
        }
    }

    internal void CompleteRequest()
    {
        Scheduler.CategoryWork? next;
        lock (_sync)
        {
            if (_waiting.First is { } node)
            {
                next = node.Value;
                _waiting.RemoveFirst();
                next.WaitingNode = null;
            }
            else
            {
                next = null;
                _inFlight--;
            }
        }
        next?.Start();
    }
}
