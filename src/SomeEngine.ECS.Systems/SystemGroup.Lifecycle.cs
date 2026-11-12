namespace SomeEngine.ECS.Systems;

using SomeEngine.Diagnostics;
using SomeEngine.Job;

/// <summary>
/// Registration-order system container for a single concrete context type.
/// </summary>
public sealed class SystemGroup<TContext> : IDisposable, ISystemVersions
    where TContext : allows ref struct
{
    private const int LifecycleControlIdle = 0;
    private const int LifecycleControlActive = 1;

    private readonly ISystemDriver<TContext> _driver;
    private readonly object _gate = new();
    private readonly List<SystemNode<TContext>> _nodes = new();
    private readonly List<SystemState> _states = new();
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
            lock (_gate)
            {
                ThrowIfDisposed();
                return _nodes.Count;
            }
        }
    }

    public int VersionCount => Count;

    public void CopyVersions(Span<ulong> versions)
    {
        lock (_gate)
            for (int index = 0; index < _states.Count; index++)
            {
                versions[index * 2] = _states[index].LastSystemVersion;
                versions[index * 2 + 1] = _states[index].CurrentSystemVersion;
            }
    }

    public void RestoreVersions(ReadOnlySpan<ulong> versions)
    {
        lock (_gate)
            for (int index = 0; index < _states.Count; index++)
            {
                _states[index].LastSystemVersion = versions[index * 2];
                _states[index].CurrentSystemVersion = versions[index * 2 + 1];
            }
    }

    public int Add<TSystem>()
        where TSystem : struct, ISystem<TContext>
    {
        return Add(default(TSystem));
    }

    /// <summary>Describes the registered system types in their execution order.</summary>
    public Type[] DescribeSystems()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _nodes.Select(node => node.SystemType).ToArray();
        }
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
            var state = new SystemState
            {
                Index = index,
                Enabled = true,
            };
            state.InitializeJobLifetime();
            _states.Add(state);

            return index;
        }
    }

    public SystemState GetState(int index)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateIndex(index);
            return _states[index];
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
        SystemState closingState = null!;
        lock (_gate)
        {
            WaitForLifecycleControlUnderGate();
            ThrowIfDisposed();
            ValidateIndex(index);
            var state = _states[index];
            if (state.Enabled == enabled)
                return;

            if (enabled)
            {
                state.ResetJobLifetime();
                state.Enabled = true;
                _states[index] = state;
                return;
            }

            state.Enabled = false;
            _states[index] = state;
            closingState = state;
            Volatile.Write(ref _lifecycleControlState, LifecycleControlActive);
        }

        var exceptions = new List<Exception>();
        try
        {
            closingState.RequireJobLifetime().CloseAndComplete(exceptions);
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

    /// <summary>
    /// Creates enabled systems without running their update callbacks. This is used when a
    /// system must publish long-lived structure before the first update boundary.
    /// </summary>
    public void Initialize()
    {
        TContext template = default!;
        InitializeCore(useTemplate: false, ref template);
    }

    public void Initialize(ref TContext template) =>
        InitializeCore(useTemplate: true, ref template);

    private void InitializeCore(bool useTemplate, ref TContext template)
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
                SystemState state = _states[i];
                if (!state.Enabled || state.Created)
                    continue;

                using JobSubmissionScope submissions =
                    state.RequireJobLifetime().EnterSubmissionScope();
                EnterLifecycleCallback();
                try
                {
                    state.CurrentSystemVersion = _driver.AcquireSystemVersion(ref state);
                    using World.BatchVersionScope version = _driver.World?.BeginBatchVersion(state.CurrentSystemVersion) ?? default;
                    TContext context;
                    if (useTemplate)
                    {
                        context = template;
                        _driver.CreateContext(ref state, ref context);
                    }
                    else
                    {
                        context = _driver.CreateContext(ref state);
                    }
                    _driver.BeforeUpdate(ref state, ref context);
                    _nodes[i].OnCreate(ref context);
                    state.Created = true;
                    _driver.AfterUpdate(ref state, ref context);
                    state.LastSystemVersion = state.CurrentSystemVersion;
                    _states[i] = state;
                }
                finally
                {
                    ExitLifecycleCallback();
                }
            }
        }
    }

    /// <summary>
    /// Completes work submitted by previous updates, including child tasks. Systems remain
    /// enabled and may run again. Use this at a data-visibility boundary, not after each system.
    /// </summary>
    public async ValueTask CompleteAsync()
    {
        ThrowIfLifecycleCallbackReentry();
        ThrowIfJobCallbackTeardown("completed");
        lock (_gate)
        {
            WaitForLifecycleControlUnderGate();
            ThrowIfDisposed();
            Volatile.Write(ref _lifecycleControlState, LifecycleControlActive);
        }

        List<Exception>? failures = null;
        try
        {
            // Callback submission has ended and lifecycle control keeps this set of
            // systems unchanged while their existing roots reach scope completion.
            foreach (SystemState state in _states)
            {
                if (!state.Enabled) continue;
                Exception[] errors = await state.RequireJobLifetime().WaitCurrentRootsAsync().ConfigureAwait(false);
                if (errors.Length != 0) (failures ??= []).AddRange(errors);
            }
        }
        finally
        {
            CompleteLifecycleControl();
        }
        if (failures is not null) ThrowIfTeardownFailed(failures, "System work failed.");
    }

    /// <summary>Waits current system roots and their descendants at a data visibility boundary.</summary>
    public void Complete()
    {
        ThrowIfLifecycleCallbackReentry();
        ThrowIfJobCallbackTeardown("completed");
        lock (_gate)
        {
            WaitForLifecycleControlUnderGate();
            ThrowIfDisposed();
            Volatile.Write(ref _lifecycleControlState, LifecycleControlActive);
        }

        List<Exception> failures = [];
        try
        {
            foreach (SystemState state in _states)
            {
                if (!state.Enabled)
                    continue;
                try { state.RequireJobLifetime().CompleteCurrentRoots(failures); }
                catch (Exception failure) { failures.Add(failure); }
            }
        }
        finally
        {
            CompleteLifecycleControl();
        }
        ThrowIfTeardownFailed(failures, "System work failed.");
    }

    private unsafe void UpdateCore(bool useTemplate, ref TContext template)
    {
        ThrowIfLifecycleCallbackReentry();
        ThrowIfJobCallbackWouldWaitForLifecycleControl();
        lock (_gate)
        {
            WaitForLifecycleControlUnderGate();
            ThrowIfDisposed();
            Volatile.Write(ref _lifecycleControlState, LifecycleControlActive);
        }

        try
        {
            // Registration and teardown wait for this execution boundary. The collection cannot
            // change, but no lock is held while drivers or user systems run or wait for Jobs.
            for (int i = 0; i < _nodes.Count; i++)
            {
                SystemState state;
                lock (_gate)
                    state = _states[i];
                if (!state.Enabled)
                    continue;

                SystemNode<TContext> node = _nodes[i];
                using var trace = EngineTrace.Begin(node.UpdateTrace);
                using JobSubmissionScope submissions = state.RequireJobLifetime().EnterSubmissionScope();
                EnterLifecycleCallback();
                try
                {
                    state.CurrentSystemVersion = _driver.AcquireSystemVersion(ref state);
                    using World.BatchVersionScope version = _driver.World?.BeginBatchVersion(state.CurrentSystemVersion) ?? default;
                    TContext context;
                    if (useTemplate)
                    {
                        context = template;
                        _driver.CreateContext(ref state, ref context);
                    }
                    else
                    {
                        context = _driver.CreateContext(ref state);
                    }
                    _driver.BeforeUpdate(ref state, ref context);
                    if (!state.Created)
                    {
                        node.OnCreate(ref context);
                        state.Created = true;
                        lock (_gate)
                            _states[i] = state;
                    }
                    node.Update(node, ref context);
                    _driver.AfterUpdate(ref state, ref context);
                    state.LastSystemVersion = state.CurrentSystemVersion;
                    lock (_gate)
                        _states[i] = state;
                }
                finally
                {
                    ExitLifecycleCallback();
                }
            }
        }
        finally
        {
            CompleteLifecycleControl();
        }
    }

    /// <summary>
    /// Removes one system after draining update work, running OnDestroy with live submission
    /// admission, and waiting every cleanup root through full scope completion. Later state indices
    /// shift down by one.
    /// </summary>
    public void Remove(int index)
    {
        ThrowIfLifecycleCallbackReentry();
        ThrowIfJobCallbackTeardown("removed");
        ThrowIfJobCallbackWouldWaitForLifecycleControl();
        ThrowIfDisposed();
        var exceptions = new List<Exception>();
        SystemState state;
        lock (_gate)
        {
            WaitForLifecycleControlUnderGate();
            ThrowIfDisposed();
            ValidateIndex(index);

            state = _states[index];
            state.Enabled = false;
            state.PrepareJobLifetimeForDestroy();
            _states[index] = state;
            Volatile.Write(ref _lifecycleControlState, LifecycleControlActive);
        }

        try
        {
            state.RequireJobLifetime().CompleteCurrentRoots(exceptions);
        }
        catch (Exception exception)
        {
            exceptions.Add(exception);
        }

        try
        {
            DestroyState(index, ref state, exceptions);
            lock (_gate)
            {
                _nodes.RemoveAt(index);
                _states.RemoveAt(index);
                for (int i = index; i < _states.Count; i++)
                {
                    SystemState shifted = _states[i];
                    shifted.Index = i;
                    _states[i] = shifted;
                }
            }
        }
        finally
        {
            CompleteLifecycleControl();
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
                for (int i = 0; i < _states.Count; i++)
                {
                    SystemState state = _states[i];
                    state.Enabled = false;
                    state.PrepareJobLifetimeForDestroy();
                    _states[i] = state;
                }
            }

            for (int i = 0; i < _states.Count; i++)
            {
                try
                {
                    _states[i].RequireJobLifetime().CompleteCurrentRoots(exceptions);
                }
                catch (Exception exception)
                {
                    exceptions.Add(exception);
                }
            }

            for (int i = 0; i < _nodes.Count; i++)
            {
                SystemState state = _states[i];
                DestroyState(i, ref state, exceptions);
                lock (_gate)
                    _states[i] = state;
            }
            lock (_gate)
            {
                _nodes.Clear();
                _states.Clear();
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

    public void ValidateDisposePreconditions(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        ValidateDisposePreconditions();
        world.ValidateDisposePreconditions();
    }

    private void DestroyState(
        int index,
        ref SystemState state,
        List<Exception> exceptions)
    {
        if (state.Created)
        {
            try
            {
                using JobSubmissionScope submissions =
                    state.RequireJobLifetime().EnterSubmissionScope();
                EnterLifecycleCallback();
                try
                {
                    TContext context = _driver.CreateContext(ref state);
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
            // cleanup root scheduled by that callback is captured and drained before state state is
            // released. Faults never skip this boundary.
            state.RequireJobLifetime().CloseAndComplete(exceptions);
        }
        catch (Exception exception)
        {
            exceptions.Add(exception);
        }
        finally
        {
            state.Created = false;
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
        if ((ulong)index >= (ulong)_states.Count)
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
            if (Volatile.Read(ref _lifecycleControlState) != LifecycleControlActive)
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
        if (JobThreads.IsActive &&
            (Volatile.Read(ref _lifecycleControlState) != LifecycleControlIdle ||
             Volatile.Read(ref _disposeState) == 1))
        {
            ThrowIfJobCallbackLifecycleControlWait();
        }
    }

    private static void ThrowIfJobCallbackLifecycleControlWait()
    {
        if (JobThreads.IsActive)
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
        if (JobThreads.IsActive)
        {
            throw new InvalidOperationException(
                $"A system cannot be {operation} from a running Job callback.");
        }
    }
}
