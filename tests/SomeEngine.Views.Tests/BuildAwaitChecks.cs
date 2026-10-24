using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using System.Runtime.CompilerServices;

namespace SomeEngine.Views.Tests;

public static partial class BuildResumeChecks
{

    public static void VerifyCanceledTask(bool jobWorld)
    {
        using var frames = new HeadlessFrameDriver(new World(execution:
            jobWorld ? new WorldJobs(SomeEngine.Job.JobSystem.Default) : null));
        var source = new State<int>(1);
        var output = new State<int>();
        var completion = new TaskCompletionSource();
        var cleanup = new CleanupData();
        Entity root = Views.Spawn(frames.World, Entity.Null, new TaskContent(source, output, completion.Task, cleanup));
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

    public static void VerifyCanceledConfiguredTask(bool jobWorld)
    {
        using var frames = new HeadlessFrameDriver(new World(execution:
            jobWorld ? new WorldJobs(SomeEngine.Job.JobSystem.Default) : null));
        var source = new State<int>(1);
        var output = new State<int>();
        var completion = new TaskCompletionSource();
        var cleanup = new CleanupData();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ConfiguredTaskContent(source, output, completion.Task, cleanup));
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

    public static void VerifyCanceledValueTask(bool jobWorld)
    {
        using var frames = new HeadlessFrameDriver(new World(execution:
            jobWorld ? new WorldJobs(SomeEngine.Job.JobSystem.Default) : null));
        var source = new State<int>(1);
        var output = new State<int>();
        var completion = new TaskCompletionSource();
        var cleanup = new CleanupData();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ValueTaskContent(source, output, completion.Task, cleanup));
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

    public static void VerifyCanceledConfiguredValueTask(bool jobWorld)
    {
        using var frames = new HeadlessFrameDriver(new World(execution:
            jobWorld ? new WorldJobs(SomeEngine.Job.JobSystem.Default) : null));
        var source = new State<int>(1);
        var output = new State<int>();
        var completion = new TaskCompletionSource();
        var cleanup = new CleanupData();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ConfiguredValueTaskContent(source, output, completion.Task, cleanup));
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

    public static void VerifyCanceledHelper(bool jobWorld)
    {
        using var frames = new HeadlessFrameDriver(new World(execution:
            jobWorld ? new WorldJobs(SomeEngine.Job.JobSystem.Default) : null));
        var source = new State<int>(1);
        var output = new State<int>();
        var completion = new TaskCompletionSource();
        var cleanup = new CleanupData();
        Entity root = Views.Spawn(frames.World, Entity.Null, new HelperContent(source, output, completion.Task, cleanup));
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

    public static void VerifyCanceledCustomAwait(bool jobWorld)
    {
        using var frames = new HeadlessFrameDriver(new World(execution:
            jobWorld ? new WorldJobs(SomeEngine.Job.JobSystem.Default) : null));
        var source = new State<int>(1);
        var output = new State<int>();
        var completion = new TaskCompletionSource();
        var cleanup = new CleanupData();
        Entity root = Views.Spawn(frames.World, Entity.Null, new CustomAwaitContent(source, output, completion.Task, cleanup));
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

    public static void VerifyCanceledAsyncCleanup(bool jobWorld)
    {
        using var frames = new HeadlessFrameDriver(new World(execution:
            jobWorld ? new WorldJobs(SomeEngine.Job.JobSystem.Default) : null));
        var source = new State<int>(1);
        var output = new State<int>();
        var completion = new TaskCompletionSource();
        var cleanup = new CleanupData();
        Entity root = Views.Spawn(frames.World, Entity.Null, new AsyncCleanupContent(source, output, completion.Task, cleanup));
        frames.Frame();
        source.Value = 2;
        frames.Frame();
        Require(output.Value == 2, "The changed dependency starts and presents the new build.");
        completion.SetResult();
        Require(SpinWait.SpinUntil(() =>
        {
            frames.Frame();
            return cleanup.Started;
        }, TimeSpan.FromSeconds(5)), "The canceled build reaches its asynchronous cleanup.");
        Require(cleanup.Disposed, "The using block is released before the asynchronous finally completes.");
        Require(output.Value == 2, "The new result remains visible while cleanup awaits its completion.");
        cleanup.Completion.SetResult();
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

    public static void VerifyCanceledWaitCleanup(bool jobWorld)
    {
        using var frames = new HeadlessFrameDriver(new World(execution:
            jobWorld ? new WorldJobs(SomeEngine.Job.JobSystem.Default) : null));
        var source = new State<int>(1);
        var output = new State<int>();
        var completion = new TaskCompletionSource();
        var cleanup = new CleanupData();
        Entity root = Views.Spawn(frames.World, Entity.Null, new CanceledWaitCleanupContent(source, output, completion.Task, cleanup));
        frames.Frame();
        source.Value = 2;
        frames.Frame();
        Require(output.Value == 2, "The changed dependency starts and presents the new build.");
        completion.SetResult();
        Require(SpinWait.SpinUntil(() =>
        {
            frames.Frame();
            return cleanup.Started;
        }, TimeSpan.FromSeconds(5)), "The canceled build reaches its asynchronous cleanup.");
        Require(cleanup.Disposed, "The using block is released before the asynchronous finally completes.");
        Require(output.Value == 2, "The new result remains visible while cleanup awaits its completion.");
        cleanup.Completion.SetResult();
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

    private readonly record struct TaskContent(State<int> Source, State<int> Output, Task Result, CleanupData Cleanup)
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
                        await Result;
                        Output.Value = value;
                    }
                }
                catch (OperationCanceledException)
                {
                    Cleanup.Canceled = true;
                }
                finally
                {
                    Cleanup.Finally = true;
                }
            }
            else
                Output.Value = value;
        }
    }

    private readonly record struct ConfiguredTaskContent(State<int> Source, State<int> Output, Task Result, CleanupData Cleanup)
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
                        await Result.ConfigureAwait(false);
                        Output.Value = value;
                    }
                }
                catch (OperationCanceledException)
                {
                    Cleanup.Canceled = true;
                }
                finally
                {
                    Cleanup.Finally = true;
                }
            }
            else
                Output.Value = value;
        }
    }

    private readonly record struct ValueTaskContent(State<int> Source, State<int> Output, Task Result, CleanupData Cleanup)
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
                        await new ValueTask(Result);
                        Output.Value = value;
                    }
                }
                catch (OperationCanceledException)
                {
                    Cleanup.Canceled = true;
                }
                finally
                {
                    Cleanup.Finally = true;
                }
            }
            else
                Output.Value = value;
        }
    }

    private readonly record struct ConfiguredValueTaskContent(State<int> Source, State<int> Output, Task Result, CleanupData Cleanup)
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
                        await new ValueTask(Result).ConfigureAwait(false);
                        Output.Value = value;
                    }
                }
                catch (OperationCanceledException)
                {
                    Cleanup.Canceled = true;
                }
                finally
                {
                    Cleanup.Finally = true;
                }
            }
            else
                Output.Value = value;
        }
    }

    private readonly record struct HelperContent(State<int> Source, State<int> Output, Task Result, CleanupData Cleanup)
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
                        await WaitForTask(Result, Output);
                        Output.Value = value;
                    }
                }
                catch (OperationCanceledException)
                {
                    Cleanup.Canceled = true;
                }
                finally
                {
                    Cleanup.Finally = true;
                }
            }
            else
                Output.Value = value;
        }
    }

    private readonly record struct CustomAwaitContent(State<int> Source, State<int> Output, Task Result, CleanupData Cleanup)
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
                        await new TaskWait(Result);
                        Output.Value = value;
                    }
                }
                catch (OperationCanceledException)
                {
                    Cleanup.Canceled = true;
                }
                finally
                {
                    Cleanup.Finally = true;
                }
            }
            else
                Output.Value = value;
        }
    }

    private readonly record struct AsyncCleanupContent(State<int> Source, State<int> Output, Task Result, CleanupData Cleanup)
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
                        await Result;
                        Output.Value = value;
                    }
                }
                catch (OperationCanceledException)
                {
                    Cleanup.Canceled = true;
                }
                finally
                {
                    Cleanup.Started = true;
                    await Cleanup.Completion.Task;
                    Cleanup.Finally = true;
                }
            }
            else
                Output.Value = value;
        }
    }

    private readonly record struct CanceledWaitCleanupContent(State<int> Source, State<int> Output, Task Result, CleanupData Cleanup)
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
                        await Result.WaitAsync(Scope.Cancel);
                        Output.Value = value;
                    }
                }
                catch (OperationCanceledException)
                {
                    Cleanup.Canceled = true;
                }
                finally
                {
                    Cleanup.Started = true;
                    await Cleanup.Completion.Task;
                    Cleanup.Finally = true;
                }
            }
            else
                Output.Value = value;
        }
    }

    private static async BuildTask WaitForTask(Task result, State<int> output)
    {
        await result.ConfigureAwait(false);
        output.Value = 99;
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
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
        internal bool Started;
        internal TaskCompletionSource Completion { get; } = new();

        public void Dispose() => Disposed = true;
    }
}
