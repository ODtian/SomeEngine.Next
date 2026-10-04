using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using System.Runtime.CompilerServices;

namespace SomeEngine.Views.Tests;

public static partial class BuildResumeChecks
{
    public static void VerifyAll()
    {
        foreach (bool jobWorld in new[] { false, true })
        {
            foreach (BuildWait wait in new[] { BuildWait.Task, BuildWait.ConfiguredTask, BuildWait.ValueTask,
                BuildWait.ConfiguredValueTask, BuildWait.Helper, BuildWait.Custom, BuildWait.AsyncCleanup, BuildWait.CanceledWaitCleanup })
                VerifyCanceledTask(jobWorld, wait);
            VerifyCancelCallbackFailure(jobWorld);
            VerifyLaunchRecovery(jobWorld);
            VerifyLaunchErrors(jobWorld);
            VerifyLaunchCallbackRecovery(jobWorld);
        }
    }

    public static void VerifyLaunchCallbackRecovery(bool jobWorld)
    {
        using var frames = new HeadlessFrameDriver(new World(execution:
            jobWorld ? new SomeEngine.ECS.Systems.WorldJobs(SomeEngine.Job.JobSystem.Default) : null));
        var key = new State<int>(1);
        var error = new InvalidOperationException("launch cancellation callback failed");
        Entity root = Views.Spawn(frames.World, Entity.Null, new LaunchCallbacks(key, error));
        frames.Frame();
        key.Value = 2;
        frames.Frame();
        Require(frames.World.ReadSparse<BuildError>(root).Exception is AggregateException aggregate &&
            aggregate.InnerExceptions.Contains(error), "The child cancellation callback reaches the view error boundary.");
        Require(frames.World.ReadSparse<BuildError>(root).Frame == 2, "The callback error records the restart frame.");
        frames.Frame();
        Require(!frames.World.HasSparse<BuildError>(root), "The successful replacement child clears its cancellation callback error.");
    }

    private readonly record struct LaunchCallbacks(State<int> Key, Exception Error) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            State<int> key = Key;
            Exception error = Error;
            Scope.Compute(() => Scope.Launch(key.Value, (value, cancel) => Run(value, cancel, error), 1));
            return default;
        }

        private static async BuildTask Run(int value, CancellationToken cancel, Exception error)
        {
            if (value == 1)
            {
                using var registration = cancel.Register(() => throw error);
                await Task.Delay(Timeout.Infinite, cancel);
            }
            else
                await Scope.NextFrame();
        }
    }

    public static void VerifyLaunchErrors(bool jobWorld)
    {
        using var frames = new HeadlessFrameDriver(new World(execution:
            jobWorld ? new SomeEngine.ECS.Systems.WorldJobs(SomeEngine.Job.JobSystem.Default) : null));
        var first = new State<int>(1);
        var second = new State<int>(1);
        var firstError = new InvalidOperationException("first launch failed");
        var secondError = new InvalidOperationException("second launch failed");
        Entity root = Views.Spawn(frames.World, Entity.Null, new LaunchErrors(first, second, firstError, secondError));
        frames.Frame();
        frames.Frame();
        Require(ReferenceEquals(frames.World.ReadSparse<BuildError>(root).Exception, secondError),
            "The view presents the latest asynchronous child error.");
        second.Value = 2;
        frames.Frame();
        Require(ReferenceEquals(frames.World.ReadSparse<BuildError>(root).Exception, secondError),
            "The previous error remains visible while the replacement child is suspended.");
        frames.Frame();
        Require(ReferenceEquals(frames.World.ReadSparse<BuildError>(root).Exception, firstError),
            "A successful child preserves the other child's error.");
        first.Value = 2;
        frames.Frame();
        frames.Frame();
        Require(!frames.World.HasSparse<BuildError>(root), "Successful children clear their corresponding errors.");
    }

    private readonly record struct LaunchErrors(State<int> First, State<int> Second, Exception FirstError, Exception SecondError)
        : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            State<int> first = First;
            State<int> second = Second;
            Exception firstError = FirstError;
            Exception secondError = SecondError;
            Scope.Compute(() => Scope.Launch(first.Value, (value, _) => Run(value, firstError), 1));
            Scope.Compute(() => Scope.Launch(second.Value, (value, _) => Run(value, secondError), 2));
            return default;
        }

        private static async BuildTask Run(int value, Exception error)
        {
            await Scope.NextFrame();
            if (value == 1)
                throw error;
        }
    }

    public static void VerifyLaunchRecovery(bool jobWorld)
    {
        using var frames = new HeadlessFrameDriver(new World(execution:
            jobWorld ? new SomeEngine.ECS.Systems.WorldJobs(SomeEngine.Job.JobSystem.Default) : null));
        var key = new State<int>(1);
        var output = new State<int>();
        var error = new InvalidOperationException("launch failed");
        Entity root = Views.Spawn(frames.World, Entity.Null, new LaunchContent(key, output, error));
        frames.Frame();
        frames.Frame();
        Require(ReferenceEquals(frames.World.ReadSparse<BuildError>(root).Exception, error),
            "The failed asynchronous child computation reports its original error.");
        key.Value = 2;
        frames.Frame();
        frames.Frame();
        Require(frames.World.Read<Number>(root).Value == 2, "The successful child result updates its bound display.");
        Require(!frames.World.HasSparse<BuildError>(root), "The successful child computation clears its previous error.");
    }

    private readonly record struct LaunchContent(State<int> Key, State<int> Output, Exception Error) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            State<int> key = Key;
            State<int> output = Output;
            Exception error = Error;
            Entity node = Scope.Entity;
            Scope.Compute(() => Scope.Launch(key.Value, async (value, _) =>
            {
                await Scope.NextFrame();
                if (value == 1)
                    throw error;
                output.Value = value;
            }, 10));
            Scope.Compute(() => Scope.Write<Number>(node) = new Number(output.Value));
            return default;
        }
    }

    public static void VerifyCancelCallbackFailure(bool jobWorld)
    {
        using var frames = new HeadlessFrameDriver(new World(execution:
            jobWorld ? new SomeEngine.ECS.Systems.WorldJobs(SomeEngine.Job.JobSystem.Default) : null));
        var source = new State<int>(1);
        var output = new State<int>();
        var completion = new TaskCompletionSource();
        var error = new InvalidOperationException("cancel callback failed");
        Entity root = Views.Spawn(frames.World, Entity.Null, new CallbackContent(source, output, completion.Task, error));
        Entity sibling = Views.Spawn(frames.World, Entity.Null, new NumberContent(source));
        frames.Frame();
        source.Value = 2;
        frames.Frame();
        Require(output.Value == 2, "The callback failure reaches the view error boundary and the new build starts.");
        Require(frames.World.Read<Number>(sibling).Value == 2, "The frame continues updating subsequent views.");
        Require(frames.World.ReadSparse<BuildError>(root).Exception is AggregateException aggregate &&
            aggregate.InnerExceptions.Contains(error), "The view error contains the failing cancellation callback.");
        completion.SetResult();
        frames.Frame();
        Require(!frames.World.HasSparse<BuildError>(root), "The successful restarted build clears its previous error.");
    }

    private readonly record struct CallbackContent(State<int> Source, State<int> Output, Task Result, Exception Error)
        : IView, IComponent
    {
        public async BuildTask Build()
        {
            int value = Source.Value;
            Output.Value = value;
            if (value == 1)
            {
                Exception error = Error;
                using var registration = Scope.Cancel.Register(() => throw error);
                await Scope.NextFrame();
            }
            else
                await Result;
        }
    }

    private readonly record struct NumberContent(State<int> Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Write<Number>(Scope.Entity) = new Number(Source.Value);
            return default;
        }
    }

    private readonly record struct Number(int Value) : IComponent;

    public static void VerifyCanceledTask(bool jobWorld, BuildWait wait = BuildWait.Task)
    {
        using var frames = new HeadlessFrameDriver(new World(execution:
            jobWorld ? new SomeEngine.ECS.Systems.WorldJobs(SomeEngine.Job.JobSystem.Default) : null));
        var source = new State<int>(1);
        var output = new State<int>();
        var completion = new TaskCompletionSource();
        var cleanup = new CleanupData();
        Entity root = Views.Spawn(frames.World, Entity.Null, new TaskContent(source, output, completion.Task, cleanup, wait));
        frames.Frame();
        source.Value = 2;
        frames.Frame();
        Require(output.Value == 2, "The changed dependency starts and presents the new build.");
        completion.SetResult();
        Require(SpinWait.SpinUntil(() =>
        {
            frames.Frame();
            return cleanup.Finally;
        }, TimeSpan.FromSeconds(5)), "The ready canceled task finishes cleanup in the rebuild phase.");
        Require(output.Value == 2, "The canceled task resumes through cancellation before writing shared state.");
        Require(cleanup.Canceled && cleanup.Finally && cleanup.Disposed,
            "The canceled task delivers OperationCanceledException and runs finally and using cleanup.");
        Require(!frames.World.HasSparse<BuildError>(root), "Cancellation completes as build cleanup.");
    }

    private readonly record struct TaskContent(State<int> Source, State<int> Output, Task Result, CleanupData Cleanup, BuildWait Wait)
        : IView, IComponent
    {
        public async BuildTask Build()
        {
            int value = Source.Value;
            if (value == 1)
            {
                try
                {
                    using (Cleanup)
                    {
                        switch (Wait)
                        {
                            case BuildWait.Task: await Result; break;
                            case BuildWait.AsyncCleanup: await Result; break;
                            case BuildWait.CanceledWaitCleanup: await Result.WaitAsync(Scope.Cancel); break;
                            case BuildWait.ConfiguredTask: await Result.ConfigureAwait(false); break;
                            case BuildWait.ValueTask: await new ValueTask(Result); break;
                            case BuildWait.ConfiguredValueTask: await new ValueTask(Result).ConfigureAwait(false); break;
                            case BuildWait.Helper: await WaitForTask(Result, Output); break;
                            case BuildWait.Custom: await new TaskWait(Result); break;
                        }
                        Output.Value = value;
                    }
                }
                catch (OperationCanceledException)
                {
                    Cleanup.Canceled = true;
                }
                finally
                {
                    if (Wait is BuildWait.AsyncCleanup or BuildWait.CanceledWaitCleanup)
                        await Task.CompletedTask;
                    Cleanup.Finally = true;
                }
            }
            else
                Output.Value = value;
        }

        private static async BuildTask WaitForTask(Task result, State<int> output)
        {
            await result.ConfigureAwait(false);
            output.Value = 99;
        }
    }

    private readonly struct TaskWait(Task result) : INotifyCompletion
    {
        public TaskWait GetAwaiter() => this;
        public bool IsCompleted => result.IsCompleted;
        public void GetResult() => result.GetAwaiter().GetResult();
        public void OnCompleted(Action continuation) => result.GetAwaiter().OnCompleted(continuation);
    }

    private sealed class CleanupData : IDisposable
    {
        internal bool Canceled;
        internal bool Finally;
        internal bool Disposed;

        public void Dispose() => Disposed = true;
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }
}

public enum BuildWait { Task, ConfiguredTask, ValueTask, ConfiguredValueTask, Helper, Custom, AsyncCleanup, CanceledWaitCleanup }
