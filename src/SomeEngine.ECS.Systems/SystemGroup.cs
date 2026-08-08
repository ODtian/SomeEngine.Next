namespace SomeEngine.ECS.Systems;

using SomeEngine.Job;

/// <summary>
/// Registration-order system container for a single concrete context type.
/// </summary>
public sealed class SystemGroup<TContext> : IDisposable
    where TContext : allows ref struct
{
    private const int LifecycleControlIdle = 0;
    private const int LifecycleControlPending = 1;

    private readonly ISystemDriver<TContext> _driver;
    private readonly object _gate = new();
    private readonly List<SystemNode<TContext>> _nodes = new();
    private readonly List<SystemSlot> _slots = new();
    private int _disposeState;
    private int _lifecycleControlState;
    private int _activeLifecycleCallbackThreadId;

    public SystemGroup(ISystemDriver<TContext> driver)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
    }

    public int Count
    {
        get
        {
            ThrowIfJobCallbackWouldWaitForLifecycleControl();
            ThrowIfDisposed();
            lock (_gate)
            {
                WaitForLifecycleControlUnderGate();
                ThrowIfDisposed();
                return _nodes.Count;
            }
        }
    }

    internal bool IsLifecycleControlPending =>
        Volatile.Read(ref _lifecycleControlState) != LifecycleControlIdle;

    public int Add<TSystem>()
        where TSystem : struct, ISystem<TContext>
    {
        return Add(default(TSystem));
    }

    public int Add<TSystem>(TSystem system)
        where TSystem : ISystem<TContext>
    {
        ThrowIfLifecycleCallbackReentry();
        ThrowIfJobCallbackWouldWaitForLifecycleControl();
        ThrowIfDisposed();
        lock (_gate)
        {
            WaitForLifecycleControlUnderGate();
            ThrowIfDisposed();

            int index = _nodes.Count;
            _nodes.Add(new SystemNode<TSystem, TContext>(system));
            var slot = new SystemSlot
            {
                Index = index,
                Enabled = true,
            };
            slot.InitializeJobLifetime();
            _slots.Add(slot);

            return index;
        }
    }

    public SystemSlot GetSlot(int index)
    {
        ThrowIfJobCallbackWouldWaitForLifecycleControl();
        ThrowIfDisposed();
        lock (_gate)
        {
            WaitForLifecycleControlUnderGate();
            ThrowIfDisposed();
            ValidateIndex(index);
            return _slots[index];
        }
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
        ThrowIfLifecycleCallbackReentry();
        if (!enabled)
            ThrowIfJobCallbackTeardown("disabled");
        ThrowIfJobCallbackWouldWaitForLifecycleControl();
        ThrowIfDisposed();
        SystemSlot closingSlot = default;
        lock (_gate)
        {
            WaitForLifecycleControlUnderGate();
            ThrowIfDisposed();
            ValidateIndex(index);
            var slot = _slots[index];
            if (slot.Enabled == enabled)
                return;

            if (enabled)
            {
                slot.ResetJobLifetime();
                slot.Enabled = true;
                _slots[index] = slot;
                return;
            }

            slot.Enabled = false;
            _slots[index] = slot;
            closingSlot = slot;
            Volatile.Write(ref _lifecycleControlState, LifecycleControlPending);
        }

        var exceptions = new List<Exception>();
        try
        {
            closingSlot.RequireJobLifetime().CloseAndComplete(exceptions);
        }
        catch (Exception exception)
        {
            exceptions.Add(exception);
        }
        finally
        {
            CompleteLifecycleControl();
        }

        ThrowIfTeardownFailed(exceptions, "System disable failed.");
    }

    public void Update()
    {
        TContext template = default!;
        UpdateCore(useTemplate: false, ref template);
    }

    /// <summary>
    /// Runs the group with one scoped context template. A fresh copy is refreshed for every
    /// system, so a callback cannot corrupt the context observed by a later system.
    /// </summary>
    public void Update(ref TContext template) =>
        UpdateCore(useTemplate: true, ref template);

    private void UpdateCore(bool useTemplate, ref TContext template)
    {
        ThrowIfLifecycleCallbackReentry();
        ThrowIfJobCallbackWouldWaitForLifecycleControl();
        ThrowIfDisposed();
        lock (_gate)
        {
            WaitForLifecycleControlUnderGate();
            ThrowIfDisposed();

            for (int i = 0; i < _nodes.Count; i++)
            {
                var slot = _slots[i];
                if (!slot.Enabled)
                    continue;

                using JobSubmissionScope submissions =
                    slot.RequireJobLifetime().EnterSubmissionScope();
                EnterLifecycleCallback();
                try
                {
                    slot.CurrentSystemVersion = _driver.AcquireSystemVersion(ref slot);
                }
                finally
                {
                    ExitLifecycleCallback();
                }
                EnterLifecycleCallback();
                try
                {
                    TContext context;
                    if (useTemplate)
                    {
                        context = template;
                        _driver.CreateContext(ref slot, ref context);
                    }
                    else
                    {
                        context = _driver.CreateContext(ref slot);
                    }
                    _driver.BeforeUpdate(ref slot, ref context);

                    if (!slot.Created)
                    {
                        _nodes[i].OnCreate(ref context);
                        slot.Created = true;
                        _slots[i] = slot;
                    }

                    _nodes[i].OnUpdate(ref context);
                    _driver.AfterUpdate(ref slot, ref context);
                }
                finally
                {
                    ExitLifecycleCallback();
                }

                slot.LastSystemVersion = slot.CurrentSystemVersion;
                _slots[i] = slot;
            }
        }
    }

    /// <summary>
    /// Removes one system after draining update work, running OnDestroy with live submission
    /// admission, and waiting every cleanup root through full scope completion. Later slot indices
    /// shift down by one.
    /// </summary>
    public void Remove(int index)
    {
        ThrowIfLifecycleCallbackReentry();
        ThrowIfJobCallbackTeardown("removed");
        ThrowIfJobCallbackWouldWaitForLifecycleControl();
        ThrowIfDisposed();
        var exceptions = new List<Exception>();
        SystemSlot slot;
        lock (_gate)
        {
            WaitForLifecycleControlUnderGate();
            ThrowIfDisposed();
            ValidateIndex(index);

            slot = _slots[index];
            slot.Enabled = false;
            slot.PrepareJobLifetimeForDestroy();
            _slots[index] = slot;
            Volatile.Write(ref _lifecycleControlState, LifecycleControlPending);
        }

        try
        {
            slot.RequireJobLifetime().CompleteCurrentRoots(exceptions);
        }
        catch (Exception exception)
        {
            exceptions.Add(exception);
        }

        lock (_gate)
        {
            try
            {
                DestroySlot(index, ref slot, exceptions);
                _nodes.RemoveAt(index);
                _slots.RemoveAt(index);
                for (int i = index; i < _slots.Count; i++)
                {
                    SystemSlot shifted = _slots[i];
                    shifted.Index = i;
                    _slots[i] = shifted;
                }
            }
            finally
            {
                Volatile.Write(ref _lifecycleControlState, LifecycleControlIdle);
                Monitor.PulseAll(_gate);
            }
        }

        ThrowIfTeardownFailed(exceptions, "System removal failed.");
    }

    public void Dispose()
    {
        ValidateDisposePreconditions();
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            lock (_gate)
            {
                while (Volatile.Read(ref _disposeState) == 1)
                    Monitor.Wait(_gate);
                return;
            }
        }

        var exceptions = new List<Exception>();
        try
        {
            lock (_gate)
            {
                WaitForLifecycleControlUnderGate();
                for (int i = 0; i < _slots.Count; i++)
                {
                    SystemSlot slot = _slots[i];
                    slot.Enabled = false;
                    slot.PrepareJobLifetimeForDestroy();
                    _slots[i] = slot;
                }
            }

            for (int i = 0; i < _slots.Count; i++)
            {
                try
                {
                    _slots[i].RequireJobLifetime().CompleteCurrentRoots(exceptions);
                }
                catch (Exception exception)
                {
                    exceptions.Add(exception);
                }
            }

            lock (_gate)
            {
                for (int i = 0; i < _nodes.Count; i++)
                {
                    SystemSlot slot = _slots[i];
                    DestroySlot(i, ref slot, exceptions);
                    _slots[i] = slot;
                }

                _nodes.Clear();
                _slots.Clear();
            }
        }
        finally
        {
            lock (_gate)
            {
                Volatile.Write(ref _disposeState, 2);
                Monitor.PulseAll(_gate);
            }
        }

        ThrowIfTeardownFailed(exceptions, "System group disposal failed.");
    }

    internal void ValidateDisposePreconditions()
    {
        ThrowIfLifecycleCallbackReentry();
        ThrowIfJobCallbackTeardown("disposed");
    }

    internal void ValidateDisposePreconditions(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        ValidateDisposePreconditions();
        world.ValidateDisposePreconditions();
    }

    private void DestroySlot(
        int index,
        ref SystemSlot slot,
        List<Exception> exceptions)
    {
        if (slot.Created)
        {
            try
            {
                using JobSubmissionScope submissions =
                    slot.RequireJobLifetime().EnterSubmissionScope();
                EnterLifecycleCallback();
                try
                {
                    TContext context = _driver.CreateContext(ref slot);
                    _nodes[index].OnDestroy(ref context);
                }
                finally
                {
                    ExitLifecycleCallback();
                }
            }
            catch (Exception exception)
            {
                exceptions.Add(exception);
            }
        }

        try
        {
            // Closing happens only after OnDestroy has left its ambient submission scope, so every
            // cleanup root scheduled by that callback is captured and drained before slot state is
            // released. Faults never skip this boundary.
            slot.RequireJobLifetime().CloseAndComplete(exceptions);
        }
        catch (Exception exception)
        {
            exceptions.Add(exception);
        }
        finally
        {
            slot.Created = false;
        }
    }

    private static void ThrowIfTeardownFailed(
        List<Exception>? exceptions,
        string message)
    {
        if (exceptions is { Count: > 0 })
            throw new AggregateException(message, exceptions);
    }

    private void ValidateIndex(int index)
    {
        if ((uint)index >= (uint)_slots.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeState) != 0)
            throw new ObjectDisposedException(nameof(SystemGroup<TContext>));
    }

    private void WaitForLifecycleControlUnderGate()
    {
        while (Volatile.Read(ref _lifecycleControlState) != LifecycleControlIdle)
        {
            if (IsLifecycleCallbackThread())
                return;
            ThrowIfJobCallbackLifecycleControlWait();
            Monitor.Wait(_gate);
        }
    }

    private void CompleteLifecycleControl()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _lifecycleControlState) != LifecycleControlPending)
            {
                throw new InvalidOperationException(
                    "System lifecycle control completion is unbalanced.");
            }

            Volatile.Write(ref _lifecycleControlState, LifecycleControlIdle);
            Monitor.PulseAll(_gate);
        }
    }

    private void ThrowIfJobCallbackWouldWaitForLifecycleControl()
    {
        if (JobExecutionContext.IsActive &&
            (Volatile.Read(ref _lifecycleControlState) != LifecycleControlIdle ||
             Volatile.Read(ref _disposeState) == 1))
        {
            ThrowIfJobCallbackLifecycleControlWait();
        }
    }

    private static void ThrowIfJobCallbackLifecycleControlWait()
    {
        if (JobExecutionContext.IsActive)
        {
            throw new InvalidOperationException(
                "A running Job callback cannot wait for SystemGroup lifecycle control.");
        }
    }

    private bool IsLifecycleCallbackThread() =>
        Volatile.Read(ref _activeLifecycleCallbackThreadId) ==
        Environment.CurrentManagedThreadId;

    private void EnterLifecycleCallback()
    {
        int threadId = Environment.CurrentManagedThreadId;
        if (Interlocked.CompareExchange(
                ref _activeLifecycleCallbackThreadId,
                threadId,
                comparand: 0) != 0)
        {
            throw new InvalidOperationException(
                "System lifecycle callback execution is already active.");
        }
    }

    private void ExitLifecycleCallback()
    {
        int threadId = Environment.CurrentManagedThreadId;
        if (Interlocked.CompareExchange(
                ref _activeLifecycleCallbackThreadId,
                0,
                comparand: threadId) != threadId)
        {
            throw new InvalidOperationException(
                "System lifecycle callback execution is unbalanced or exited on another thread.");
        }
    }

    private void ThrowIfLifecycleCallbackReentry()
    {
        if (Volatile.Read(ref _activeLifecycleCallbackThreadId) ==
            Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                "System registration and lifecycle control cannot be re-entered from " +
                "a system or driver lifecycle callback on the same thread.");
        }
    }

    private static void ThrowIfJobCallbackTeardown(string operation)
    {
        if (JobExecutionContext.IsActive)
        {
            throw new InvalidOperationException(
                $"A system cannot be {operation} from a running Job callback.");
        }
    }
}

