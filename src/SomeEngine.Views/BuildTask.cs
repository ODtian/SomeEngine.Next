using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Views;

/// <summary>The result of a view build that completes in its current scope.</summary>
[AsyncMethodBuilder(typeof(BuildTaskBuilder))]
public readonly struct BuildTask
{
    private readonly ExceptionDispatchInfo? _error;

    internal BuildTask(Exception error) => _error = ExceptionDispatchInfo.Capture(error);

    public Awaiter GetAwaiter() => new(this);

    internal void Complete() => _error?.Throw();

    public readonly struct Awaiter(BuildTask task) : ICriticalNotifyCompletion
    {
        public bool IsCompleted => true;
        public void GetResult() => task.Complete();
        public void OnCompleted(Action continuation) =>
            DevelopmentCheck.Fail("BuildTask awaiters complete within the current build scope.");
        public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
    }
}

/// <summary>Runs completed awaits inline and reports a suspended build through development checks.</summary>
public struct BuildTaskBuilder
{
    private BuildTask _task;

    public static BuildTaskBuilder Create() => default;
    public BuildTask Task => _task;
    public void SetResult() { }
    public void SetException(Exception exception) => _task = new BuildTask(exception);
    public void SetStateMachine(IAsyncStateMachine stateMachine) { }

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine => stateMachine.MoveNext();

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine => ReportSuspension();

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine => ReportSuspension();

    private static void ReportSuspension() =>
        DevelopmentCheck.Fail("View builds currently require synchronous completion; BuildTask must complete synchronously (#20).");
}
