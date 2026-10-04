using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Views;

/// <summary>A view build whose suspended awaits resume in the World's rebuild phase.</summary>
/// <remarks>Consume each suspended result once, either by returning it from Build or by awaiting it.</remarks>
[AsyncMethodBuilder(typeof(BuildTaskBuilder))]
public readonly struct BuildTask
{
    private readonly BuildStateMachine? _stateMachine;
    private readonly int _version;
    private readonly ExceptionDispatchInfo? _error;

    internal BuildTask(BuildStateMachine stateMachine)
    {
        _stateMachine = stateMachine;
        _version = stateMachine.Version;
    }

    internal BuildTask(ExceptionDispatchInfo error) => _error = error;

    public BuildTaskAwaiter GetAwaiter() => new(this);

    internal bool IsCompleted => _stateMachine?.IsCompleted ?? true;
    internal void Continue(Action continuation) => _stateMachine!.Continue(continuation);

    internal void Complete()
    {
        if (_stateMachine is { } stateMachine)
            stateMachine.Complete(_version);
        else
            _error?.Throw();
    }
}

/// <summary>Observes the completion of a BuildTask.</summary>
public readonly struct BuildTaskAwaiter : ICriticalNotifyCompletion
{
    private readonly BuildTask _task;

    internal BuildTaskAwaiter(BuildTask task) => _task = task;

    public bool IsCompleted => _task.IsCompleted;
    public void GetResult()
    {
        _task.Complete();
        Scope.ThrowIfCanceled();
    }
    public void OnCompleted(Action continuation) => _task.Continue(continuation);
    public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
}

/// <summary>Runs completed awaits inline and queues suspended state machines for view rebuilding.</summary>
public struct BuildTaskBuilder
{
    private BuildStateMachine? _stateMachine;
    private ExceptionDispatchInfo? _error;
    private bool _cancellationObserved;
    private bool _canceledAwait;

    public static BuildTaskBuilder Create() => default;
    /// <summary>Observes cancellation before consuming this awaiter's result.</summary>
    public void BeginAwait()
    {
        _canceledAwait = !_cancellationObserved && Scope.IsCanceled;
        _cancellationObserved |= _canceledAwait;
    }

    /// <summary>Checks the current build after an await result has been consumed.</summary>
    /// <remarks>The SomeEngine.Views compiler task inserts this call at await recovery points.</remarks>
    public void CheckCancellation()
    {
        if (_canceledAwait)
        {
            _canceledAwait = false;
            Scope.ThrowIfCanceled();
        }
    }

    public BuildTask Task => _stateMachine is { } stateMachine ? new(stateMachine) :
        _error is { } error ? new(error) : default;

    public void SetResult() => _stateMachine?.SetResult(null);

    public void SetException(Exception exception)
    {
        if (_stateMachine is { } stateMachine)
            stateMachine.SetResult(exception);
        else
            _error = ExceptionDispatchInfo.Capture(exception);
    }

    public void SetStateMachine(IAsyncStateMachine stateMachine) { }

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine => stateMachine.MoveNext();

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine =>
        awaiter.OnCompleted(GetStateMachine(ref stateMachine).ReadyCallback);

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine =>
        awaiter.UnsafeOnCompleted(GetStateMachine(ref stateMachine).ReadyCallback);

    private BuildStateMachine GetStateMachine<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        if (_stateMachine is { } existing)
            return existing;
        var stored = BuildStateMachine<TStateMachine>.Take(Scope.Data);
        _stateMachine = stored;
        // Copy after setting the builder reference so the stored machine uses this same result.
        stored.StateMachine = stateMachine;
        return stored;
    }
}

internal abstract class BuildStateMachine
{
    private ExceptionDispatchInfo? _error;
    private Action? _continuation;

    protected BuildStateMachine() => ReadyCallback = Enqueue;

    internal Action ReadyCallback { get; }
    internal Scope.ScopeData Data { get; private set; } = null!;
    internal int Version { get; private set; }
    internal bool IsCompleted { get; private set; }

    protected void Begin(Scope.ScopeData data)
    {
        Data = data;
        data.ViewData.StateMachines++;
        Scope.RetainContent(data);
        Version++;
    }

    private void Enqueue() => Data.Queue.Enqueue(this);

    internal void SetResult(Exception? error)
    {
        if (error is { })
            _error = ExceptionDispatchInfo.Capture(error);
        IsCompleted = true;
    }

    internal void Continue(Action continuation) => _continuation = continuation;

    // Completion callbacks run after leaving the scope, including callbacks supplied by Task methods.
    internal void NotifyCompletion()
    {
        if (IsCompleted)
        {
            Action? continuation = _continuation;
            _continuation = null;
            continuation?.Invoke();
        }
    }

    internal void Complete(int version)
    {
        DevelopmentCheck.Require(version == Version && IsCompleted,
            "A suspended BuildTask result must complete before it is consumed, and must be consumed once.");
        try
        {
            _error?.Throw();
            Data.ViewData.Cancellation?.Token.ThrowIfCancellationRequested();
        }
        finally
        {
            Data.ViewData.StateMachines--;
            _error = null;
            _continuation = null;
            IsCompleted = false;
            Data = null!;
            Return();
        }
    }

    internal abstract void Resume();
    protected abstract void Return();
}

internal sealed class BuildStateMachine<TStateMachine> : BuildStateMachine
    where TStateMachine : IAsyncStateMachine
{
    [ThreadStatic]
    private static Stack<BuildStateMachine<TStateMachine>>? s_instances;

    internal TStateMachine StateMachine = default!;

    internal static BuildStateMachine<TStateMachine> Take(Scope.ScopeData data)
    {
        Stack<BuildStateMachine<TStateMachine>> instances = s_instances ??= new();
        BuildStateMachine<TStateMachine> instance = instances.TryPop(out var available) ? available : new();
        instance.Begin(data);
        return instance;
    }

    internal override void Resume() => StateMachine.MoveNext();

    protected override void Return()
    {
        StateMachine = default!;
        (s_instances ??= new()).Push(this);
    }
}
