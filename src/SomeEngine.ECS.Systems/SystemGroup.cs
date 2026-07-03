namespace SomeEngine.ECS.Systems;

/// <summary>
/// Registration-order system container for a single concrete context type.
/// </summary>
public sealed class SystemGroup<TContext> : IDisposable
{
    private readonly ISystemDriver<TContext> _driver;
    private readonly List<SystemNode<TContext>> _nodes = new();
    private readonly List<SystemSlot> _slots = new();
    private bool _disposed;

    public SystemGroup(ISystemDriver<TContext> driver)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
    }

    public int Count => _nodes.Count;

    public int Add<TSystem>()
        where TSystem : struct, ISystem<TContext>
    {
        return Add(default(TSystem));
    }

    public int Add<TSystem>(TSystem system)
        where TSystem : ISystem<TContext>
    {
        ThrowIfDisposed();

        int index = _nodes.Count;
        _nodes.Add(new SystemNode<TSystem, TContext>(system));
        _slots.Add(new SystemSlot
        {
            Index = index,
            Enabled = true,
        });

        return index;
    }

    public SystemSlot GetSlot(int index)
    {
        ValidateIndex(index);
        return _slots[index];
    }

    public void Enable(int index)
    {
        WriteEnabled(index, true);
    }

    public void Disable(int index)
    {
        WriteEnabled(index, false);
    }

    private void WriteEnabled(int index, bool enabled)
    {
        ValidateIndex(index);
        var slot = _slots[index];
        slot.Enabled = enabled;
        _slots[index] = slot;
    }

    public void Update()
    {
        ThrowIfDisposed();

        for (int i = 0; i < _nodes.Count; i++)
        {
            var slot = _slots[i];
            if (!slot.Enabled)
                continue;

            slot.CurrentSystemVersion = _driver.AcquireSystemVersion(ref slot);
            var context = _driver.CreateContext(ref slot);
            _driver.BeforeUpdate(ref slot, ref context);

            if (!slot.Created)
            {
                _nodes[i].OnCreate(ref context);
                slot.Created = true;
                _slots[i] = slot;
            }

            _nodes[i].OnUpdate(ref context);
            _driver.AfterUpdate(ref slot, ref context);

            slot.LastSystemVersion = slot.CurrentSystemVersion;
            _driver.Complete(ref slot, ref context);
            _slots[i] = slot;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        for (int i = 0; i < _nodes.Count; i++)
        {
            var slot = _slots[i];
            if (!slot.Created)
                continue;

            var context = _driver.CreateContext(ref slot);
            _nodes[i].OnDestroy(ref context);
            slot.Created = false;
            _driver.Complete(ref slot, ref context);
            _slots[i] = slot;
        }

        _disposed = true;
    }

    private void ValidateIndex(int index)
    {
        if ((uint)index >= (uint)_slots.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SystemGroup<TContext>));
    }
}

