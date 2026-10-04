using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Views.Tests;

public static partial class BuildResumeChecks
{
    public static void VerifyCleanupHelper(bool jobWorld)
    {
        using var frames = new HeadlessFrameDriver(new World(execution:
            jobWorld ? new WorldJobs(SomeEngine.Job.JobSystem.Default) : null));
        var source = new State<int>(1);
        var output = new State<int>();
        var completion = new TaskCompletionSource();
        var cleanupCompletion = new TaskCompletionSource();
        var events = new HelperEvents();
        Entity target = frames.World.CreateEntity(new CleanupNumber());
        Entity root = Views.Spawn(frames.World, Entity.Null,
            new HelperFinallyContent(source, output, target, completion.Task, cleanupCompletion.Task, events));
        frames.Frame();
        source.Value = 2;
        frames.Frame();
        Require(output.Value == 2 && frames.World.Read<CleanupNumber>(target).Value == 2,
            "The replacement build publishes its state and component result.");
        completion.SetResult();
        Require(SpinWait.SpinUntil(() =>
        {
            frames.Frame();
            return events.Values.Contains("finally");
        }, TimeSpan.FromSeconds(5)), "The canceled build enters its finally helper.");
        cleanupCompletion.SetResult();
        Require(SpinWait.SpinUntil(() =>
        {
            frames.Frame();
            return events.Values.Contains("after helper");
        }, TimeSpan.FromSeconds(5)), "The helper and the following finally statements finish.");
        Require(events.Values.SequenceEqual(new[] { "finally", "helper finished", "after helper", "disposed", "canceled" }),
            $"The helper finishes before using disposal and cancellation completion; events: {string.Join(", ", events.Values)}.");
        Require(output.Value == 2 && frames.World.Read<CleanupNumber>(target).Value == 2,
            "The completed cleanup keeps the new state and component result.");
        Require(!frames.World.HasSparse<BuildError>(root), "The nested canceled cleanup completes successfully.");
    }

    private readonly record struct HelperFinallyContent(State<int> Source, State<int> Output, Entity Target,
        Task Result, Task CleanupResult, HelperEvents Events) : IView, IComponent
    {
        public async BuildTask Build()
        {
            int value = Source.Value;
            Scope.Write<CleanupNumber>(Target) = new(value);
            if (value == 2)
            {
                Output.Value = value;
                return;
            }
            try
            {
                using (Events)
                {
                    try
                    {
                        await Result.ConfigureAwait(false);
                        Output.Value = value;
                    }
                    finally
                    {
                        Events.Values.Add("finally");
                        await Cleanup(CleanupResult, Target, Events);
                        Events.Values.Add("after helper");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Events.Values.Add("canceled");
            }
        }
    }

    private static async BuildTask Cleanup(Task result, Entity target, HelperEvents events)
    {
        await result.ConfigureAwait(false);
        Scope.Write<CleanupNumber>(target) = new(99);
        events.Values.Add("helper finished");
    }

    private readonly record struct CleanupNumber(int Value) : IComponent;

    private sealed class HelperEvents : IDisposable
    {
        internal List<string> Values { get; } = new();
        public void Dispose() => Values.Add("disposed");
    }
}
