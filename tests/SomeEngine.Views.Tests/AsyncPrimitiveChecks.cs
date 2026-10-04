using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

/// <summary>Public async behavior shared by the managed and NativeAOT consumers.</summary>
internal static partial class AsyncPrimitiveChecks
{
    internal static void VerifyConcurrentBuild()
    {
        using var frames = new HeadlessFrameDriver();
        var work = new Work();
        var result = new TaskCompletionSource();
        Entity target = frames.World.CreateEntity(new Number());
        Entity root = Views.Spawn(frames.World, Entity.Null, new ConcurrentContent(work, target, result.Task));
        frames.Frame();
        frames.Frame();
        Require(frames.World.Read<Number>(target).Value == 42, "The child task commits its next-frame write.");
        Require(work.Thread == Environment.CurrentManagedThreadId, "The child task resumes on the rebuild thread.");
        Require(Text(frames, root, 0) == "waiting", "The view keeps its waiting content while its child tasks run.");
        Finish(result.SetResult);
        frames.Frame();
        Require(Text(frames, root, 0) == "complete", "The view completes alongside its child tasks.");

        Hierarchy<ElementTree>.DestroySubtree(frames.World, root);
        Require(work.First.IsCancellationRequested && work.Second.IsCancellationRequested,
            "Releasing the view cancels both child tasks.");
        frames.Frame();
        Require(work.FirstCleaned && work.SecondCleaned, "Both child tasks finish their cleanup.");
    }

    internal static void VerifyResourceRecovery()
    {
        using var frames = new HeadlessFrameDriver();
        var source = new State<int>(1);
        var requests = new Requests();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ResourceContent(source, requests));
        frames.Frame();
        Finish(() => requests.Results[1].SetResult("old"));
        frames.Frame();
        Require(Text(frames, root, 0) == "old: False", "The resource displays its first completed value.");
        source.Value = 2;
        frames.Frame();
        Require(Text(frames, root, 0) == "old: True", "A changed source retains the value while loading.");
        var error = new InvalidOperationException("request failed");
        Finish(() => requests.Results[2].SetException(error));
        frames.Frame();
        Require(ReferenceEquals(requests.Resource!.Error, error), "The resource exposes its request error.");
        Require(Text(frames, root, 1) == "request failed", "An error-only reader displays the failed request.");
        Require(Text(frames, root, 0) == "old: False", "A failed request retains the previous value.");
        Finish(() => requests.Results[3].SetResult("new"));
        source.Value = 3;
        frames.Frame();
        Require(Text(frames, root, 0) == "new: False", "A synchronously completed request displays the new value.");
        Require(Text(frames, root, 1) == "ready", "The successful request restores the error-only reader.");
    }

    private static string Text(HeadlessFrameDriver frames, Entity root, int index) =>
        frames.World.Read<Label>(Hierarchy<ElementTree>.GetChildren(frames.World, root)[index]).Value;

    private static void Finish(Action complete)
    {
        var worker = new Thread(() => complete());
        worker.Start();
        worker.Join();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Work
    {
        internal CancellationToken First;
        internal CancellationToken Second;
        internal bool FirstCleaned;
        internal bool SecondCleaned;
        internal int Thread;
    }

    private readonly record struct ConcurrentContent(Work Work, Entity Target, Task Result) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Scope.Launch((Work, Target), static async (data, cancel) =>
            {
                data.Work.First = cancel;
                try
                {
                    await Scope.NextFrame();
                    data.Work.Thread = Environment.CurrentManagedThreadId;
                    Scope.Write<Number>(data.Target) = new Number(42);
                    await Task.Delay(Timeout.Infinite, cancel);
                }
                finally
                {
                    data.Work.FirstCleaned = true;
                }
            });
            Scope.Launch(Work, static async (work, cancel) =>
            {
                work.Second = cancel;
                try
                {
                    await Task.Delay(Timeout.Infinite, cancel);
                }
                finally
                {
                    work.SecondCleaned = true;
                }
            });
            Entity label = Scope.View(new Label("waiting"), 1);
            await Result;
            Scope.Write<Label>(label) = new Label("complete");
        }
    }

    private sealed class Requests
    {
        internal TaskCompletionSource<string>[] Results { get; } = [new(), new(), new(), new()];
        internal Resource<string>? Resource;
    }

    private readonly record struct ResourceContent(State<int> Source, Requests Requests) : IView, IComponent
    {
        public BuildTask Build()
        {
            Resource<string> resource = Scope.Resource((Source.Value, Requests),
                static (data, cancel) => new ValueTask<string>(data.Requests.Results[data.Value].Task));
            Requests.Resource = resource;
            Scope.View(new Label($"{resource.Value}: {resource.Loading}"), 1);
            Scope.View(new ErrorContent(resource), 2);
            return default;
        }
    }

    private readonly record struct ErrorContent(Resource<string> Resource) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Write<Label>(Scope.Entity) = new Label(Resource.Error?.Message ?? "ready");
            return default;
        }
    }

    private readonly record struct Number(int Value) : IComponent;

    private readonly record struct Label(string Value) : IView, IComponent
    {
        public BuildTask Build() => default;
    }
}
