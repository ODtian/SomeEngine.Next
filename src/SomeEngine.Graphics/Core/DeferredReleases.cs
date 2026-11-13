namespace SomeEngine.Graphics;

/// <summary>Releases disposed device objects after the queue values captured at disposal finish.</summary>
internal sealed class DeferredReleases(Device device)
{
    private readonly object _gate = new();
    private readonly List<(GraphicsObject Object, QueueCompletion[] Uses)> _objects = [];

    internal void Add(GraphicsObject value)
    {
        QueueCompletion[] uses = device.DeviceQueues.Where(queue => queue.LastSubmittedValue != 0)
            .Select(queue => new QueueCompletion(queue, queue.LastSubmittedValue)).ToArray();
        if (uses.Length == 0) value.Release(fromParent: false);
        else lock (_gate) _objects.Add((value, uses));
    }

    internal void CollectCompleted()
    {
        List<GraphicsObject> completed = [];
        lock (_gate)
            for (int index = _objects.Count - 1; index >= 0; index--)
            {
                var entry = _objects[index];
                if (!entry.Uses.All(completion => device.BackendReceiver.IsComplete(completion))) continue;
                completed.Add(entry.Object);
                _objects.RemoveAt(index);
            }
        foreach (GraphicsObject value in completed) value.Release(fromParent: false);
    }

    internal void ReleaseAll()
    {
        GraphicsObject[] objects;
        lock (_gate)
        {
            objects = _objects.Select(entry => entry.Object).ToArray();
            _objects.Clear();
        }
        foreach (GraphicsObject value in objects) value.Release(fromParent: false);
    }
}
