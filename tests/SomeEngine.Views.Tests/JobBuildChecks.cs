using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Job;

namespace SomeEngine.Views.Tests;

/// <summary>Job results displayed through public view APIs in managed and native builds.</summary>
internal static partial class JobBuildChecks
{
    internal static void VerifyResult(bool completed)
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using var frames = new HeadlessFrameDriver();
        var number = new NumberData();
        JobHandle handle = scheduler.Schedule(new WriteNumberJob(number));
        int rebuildThread = Environment.CurrentManagedThreadId;
        if (completed)
            CompleteOnWorker(handle);

        Entity root = Views.Spawn(frames.World, Entity.Null, new NumberView(handle, number));
        frames.Frame();
        var children = Hierarchy<ElementTree>.GetChildren(frames.World, root);
        Require(children.Count == 1, "The view declares its number label in the first frame.");
        Entity label = children[0];
        Require(frames.World.Read<NumberLabel>(label).Value == (completed ? 42 : 0),
            "The first frame displays the available Job result or its loading value.");

        if (!completed)
        {
            CompleteOnWorker(handle);
            Require(frames.World.Read<NumberLabel>(label).Value == 0,
                "The loading value remains visible until the rebuild phase.");
            frames.Frame();
        }

        Require(number.ThreadId != rebuildThread, "The Job writes its data on a worker thread.");
        Require(frames.World.Read<NumberLabel>(label).Value == 42,
            "The rebuilt label displays the value written by the Job.");
        Require(frames.World.Read<DisplayedThread>(root).Id == rebuildThread,
            "The result is displayed on the thread driving the rebuild phase.");
        Require(Hierarchy<ElementTree>.GetChildren(frames.World, root)[0] == label,
            "The result updates the label declared before the await.");
    }

    private static void CompleteOnWorker(JobHandle handle)
    {
        var worker = new Thread(handle.Complete);
        worker.Start();
        Require(worker.Join(TimeSpan.FromSeconds(10)), "The worker completes the Job.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class NumberData
    {
        internal int Value;
        internal int ThreadId;
    }

    private readonly record struct WriteNumberJob(NumberData Number) : IJob
    {
        public void Execute()
        {
            Number.Value = 42;
            Number.ThreadId = Environment.CurrentManagedThreadId;
        }
    }

    private readonly record struct NumberView(JobHandle Handle, NumberData Number) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Entity label = Scope.View(new NumberLabel(0));
            await Handle;
            Scope.Write<NumberLabel>(label) = new NumberLabel(Number.Value);
            Scope.Write<DisplayedThread>(Scope.Entity) = new DisplayedThread(Environment.CurrentManagedThreadId);
        }
    }

    private readonly record struct NumberLabel(int Value) : IView, IComponent
    {
        public BuildTask Build() => default;
    }

    private readonly record struct DisplayedThread(int Id) : IComponent;
}
