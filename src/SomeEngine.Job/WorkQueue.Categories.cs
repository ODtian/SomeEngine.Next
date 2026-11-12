using System.Collections.Concurrent;

namespace SomeEngine.Job;

internal sealed partial class WorkQueue
{
    private readonly Lock _categoryLock = new();
    private readonly ConcurrentDictionary<JobCategory, CategoryReadyQueues> _categoryQueues = new();
    private readonly List<CategoryReadyQueues> _categoryOrder = [];
    private int _categoryCount;
    private int _serviceIndex;
    private int _serviceRemaining;

    public void RegisterCategory(JobCategory category)
    {
        var queues = new CategoryReadyQueues(category);
        lock (_categoryLock)
        {
            _categoryQueues[category] = queues;
            _categoryOrder.Add(queues);
            _serviceRemaining = 0;
            Volatile.Write(ref _categoryCount, _categoryOrder.Count);
        }
    }

    public void CategoryExecutionFinished() => WakeWorkers(1);

    private bool TryDequeueByCategory(int workerIndex, out ReadyTicket ticket,
        out bool stole, out int priorityIndex, out JobCategory? category)
    {
        ticket = default;
        stole = false;
        priorityIndex = 0;
        category = null;
        lock (_categoryLock)
        {
            int choices = _categoryOrder.Count + 1;
            for (int attempt = 0; attempt < choices; attempt++)
            {
                bool defaultCategory = _serviceIndex == _categoryOrder.Count;
                if (_serviceRemaining == 0)
                    _serviceRemaining = defaultCategory ? 1 : _categoryOrder[_serviceIndex].Category.Config.ServiceShare;
                bool found;
                if (defaultCategory) found = TryDequeueDefault(workerIndex, out ticket, out stole, out priorityIndex);
                else
                {
                    CategoryReadyQueues queues = _categoryOrder[_serviceIndex];
                    found = TryDequeueCategory(queues, out ticket, out priorityIndex);
                    if (found) category = queues.Category;
                }
                if (found)
                {
                    if (--_serviceRemaining == 0) _serviceIndex = (_serviceIndex + 1) % choices;
                    return true;
                }
                _serviceRemaining = 0;
                _serviceIndex = (_serviceIndex + 1) % choices;
            }
        }
        return false;
    }

    private bool TryDequeueCategory(CategoryReadyQueues queues,
        out ReadyTicket ticket, out int priorityIndex)
    {
        ticket = default;
        priorityIndex = 0;
        for (int priority = 0; priority < JobPriorityOrder.Count; priority++)
        {
            if (queues.Priorities[priority].IsEmpty)
                continue;
            if (!_scheduler.IsExecutingCategory(queues.Category) && !queues.Category.TryStartExecution())
                return false;
            queues.Priorities[priority].TryDequeue(out ticket);
            priorityIndex = priority;
            return true;
        }
        return false;
    }

    private bool HasExecutableTickets()
    {
        if (Volatile.Read(ref _categoryCount) == 0)
            return Volatile.Read(ref _readyTicketCount) > 0;
        for (int priority = 0; priority < JobPriorityOrder.Count; priority++)
        {
            if (Volatile.Read(ref _injectors[priority]) is { IsEmpty: false } || !_injectorOverflow[priority].IsEmpty)
                return true;
            for (int worker = 0; worker < _workers.Length; worker++)
                if (Volatile.Read(ref _localQueues[worker, priority]) is { IsEmpty: false })
                    return true;
        }
        lock (_categoryLock)
        {
            foreach (CategoryReadyQueues queues in _categoryOrder)
                if (queues.Category.CanStartExecution && queues.Priorities.Any(queue => !queue.IsEmpty))
                    return true;
        }
        return false;
    }

    private sealed class CategoryReadyQueues(JobCategory category)
    {
        internal JobCategory Category { get; } = category;
        internal ConcurrentQueue<ReadyTicket>[] Priorities { get; } =
            Enumerable.Range(0, JobPriorityOrder.Count).Select(_ => new ConcurrentQueue<ReadyTicket>()).ToArray();
    }
}
