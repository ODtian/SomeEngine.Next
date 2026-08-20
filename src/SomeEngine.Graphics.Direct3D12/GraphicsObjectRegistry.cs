namespace SomeEngine.Graphics.Direct3D12;

/// <summary>
/// Thread-safe child storage used by backend, Device, and presentation parents.
/// </summary>
internal sealed class GraphicsObjectRegistry
{
    private readonly object _gate;
    private readonly HashSet<GraphicsObject> _children =
        new(ReferenceEqualityComparer.Instance);
    private bool _accepting = true;
    private bool _retainedFailure;

    internal GraphicsObjectRegistry(object gate)
    {
        _gate = gate;
    }

    internal void Add(GraphicsObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            if (!_accepting)
                throw new ObjectDisposedException(nameof(GraphicsObjectRegistry));
            if (!_children.Add(value))
                throw new InvalidOperationException("The graphics object is already registered.");
        }
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
            _accepting = false;
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
