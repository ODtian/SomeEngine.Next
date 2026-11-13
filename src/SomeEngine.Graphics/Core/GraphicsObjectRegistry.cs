namespace SomeEngine.Graphics;

/// <summary>
/// Thread-safe child storage used by backends, Devices, and presentation parents.
/// Draining only builds an intrusive work list while holding the registry gate;
/// callers dispose the returned children after the gate has been released.
/// </summary>
internal sealed class GraphicsObjectRegistry
{
    private readonly object _gate;
    private readonly HashSet<GraphicsObject> _children =
        new(ReferenceEqualityComparer.Instance);
    private bool _retainedFailure;

    internal GraphicsObjectRegistry(object gate)
    {
        _gate = gate;
    }

    internal int Count { get { lock (_gate) return _children.Count; } }

    internal void Add(GraphicsObject value)
    {
        lock (_gate)
            _children.Add(value);
    }

    internal void Remove(GraphicsObject value)
    {
        lock (_gate)
            _children.Remove(value);
    }

    internal GraphicsObject? CloseAndBuildDrainList(bool secondaryLink = false)
    {
        lock (_gate)
        {
            GraphicsObject? head = null;
            foreach (GraphicsObject child in _children)
            {
                if (secondaryLink)
                    child.SecondaryRegistryDrainNext = head;
                else
                    child.RegistryDrainNext = head;
                head = child;
            }
            return head;
        }
    }

    internal bool CompleteDrain(GraphicsObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            if (!_children.Remove(value))
                return false;
            _retainedFailure = true;
            return true;
        }
    }

    internal bool HasRetainedFailures
    {
        get
        {
            lock (_gate)
                return _retainedFailure;
        }
    }

    internal GraphicsObject? BuildWorkList(Predicate<GraphicsObject> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_gate)
        {
            GraphicsObject? head = null;
            foreach (GraphicsObject child in _children)
            {
                if (!predicate(child))
                    continue;
                child.DeviceLossWorkNext = head;
                head = child;
            }
            return head;
        }
    }
}
