using System.Runtime.CompilerServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Views.Tests;

public static partial class BuildResumeChecks
{
    public static void VerifyInheritedAwaiter(bool jobWorld)
    {
        using var frames = new HeadlessFrameDriver(new World(execution:
            jobWorld ? new WorldJobs(SomeEngine.Job.JobSystem.Default) : null));
        var source = new State<int>(1);
        var output = new State<int>();
        var completion = new TaskCompletionSource();
        var events = new AwaitEvents();
        using var ready = new ManualResetEventSlim();
        Entity root = Views.Spawn(frames.World, Entity.Null,
            new InheritedWaitContent(source, output, completion.Task, events, ready));
        frames.Frame();
        source.Value = 2;
        frames.Frame();
        Require(output.Value == 2, "The current build publishes the changed source.");
        completion.SetResult();
        Require(ready.Wait(TimeSpan.FromSeconds(5)), "The completed await registers its rebuild continuation.");
        frames.Frame();
        Require(output.Value == 2, "The inherited await result delivers cancellation before the old state write.");
        Require(events.Values.SequenceEqual(new[] { "disposed", "canceled", "finally" }),
            $"The inherited await finishes cancellation cleanup; observed events: {string.Join(", ", events.Values)}.");
        Require(!frames.World.HasSparse<BuildError>(root), "The canceled inherited await completes as cleanup.");
    }

    private readonly record struct InheritedWaitContent(State<int> Source, State<int> Output, Task Result,
        AwaitEvents Events, ManualResetEventSlim Ready)
        : IView, IComponent
    {
        public async BuildTask Build()
        {
            int value = Source.Value;
            if (value == 2)
            {
                Output.Value = value;
                return;
            }
            try
            {
                using (Events)
                {
                    await new InheritedWait(Result, Ready);
                    Output.Value = value;
                    Events.Values.Add("business");
                }
            }
            catch (OperationCanceledException)
            {
                Events.Values.Add("canceled");
            }
            finally
            {
                Events.Values.Add("finally");
            }
        }
    }

    private sealed class AwaitEvents : IDisposable
    {
        internal List<string> Values { get; } = new();
        public void Dispose() => Values.Add("disposed");
    }

    private readonly record struct InheritedWait(Task Result, ManualResetEventSlim Ready)
    {
        public DerivedAwaiter GetAwaiter() => new(Result, Ready);
    }

    private class AwaiterResult(Task result)
    {
        protected Task Result { get; } = result;
        public void GetResult() => Result.GetAwaiter().GetResult();
    }

    private sealed class DerivedAwaiter(Task result, ManualResetEventSlim ready) : AwaiterResult(result), INotifyCompletion
    {
        public bool IsCompleted => Result.IsCompleted;
        public void OnCompleted(Action continuation) => Result.ConfigureAwait(false).GetAwaiter().OnCompleted(() =>
        {
            continuation();
            ready.Set();
        });
    }
}
