using System.Runtime.CompilerServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed partial class AsyncBuildTests
{
    [Fact]
    public void AsyncWritesCommitEachSegmentAndContentUsesTheCurrentComponentValues() =>
        AsyncIntegrationChecks.VerifyWrites();

    [Fact]
    public void MemoBindingsRefreshDuringSuspensionAndCalculationsContinueAfterResumption() =>
        AsyncIntegrationChecks.VerifyComputations();

    [Fact]
    public void ObjectReadsObserveTheResumedFrameAndSettleBeforeTheNextFrame() =>
        AsyncIntegrationChecks.VerifyObjectFrames();

    [Fact]
    public void RestoredOnceViewsReconstructTheirAsynchronousBuildAndKeepTheResult() =>
        AsyncIntegrationChecks.VerifyRestoredBuild();

    [Fact]
    public void LoadingContentIsVisibleUntilTheResultBuildsInTheNextFrame()
    {
        using var frames = new HeadlessFrameDriver();
        var completion = new TaskCompletionSource<string>();
        Entity root = Views.Spawn(frames.World, Entity.Null, new LoadingView(completion.Task));

        frames.Frame();

        Assert.Equal("加载中", frames.World.Read<DisplayText>(root).Value);
        Entity loading = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal("加载中", frames.World.Read<TextView>(loading).Value);
        completion.SetResult("结果");
        Assert.Equal("加载中", frames.World.Read<DisplayText>(root).Value);

        frames.Frame();

        Assert.Equal("结果", frames.World.Read<DisplayText>(root).Value);
        var children = Hierarchy<ElementTree>.GetChildren(frames.World, root);
        Assert.Equal(2, children.Count);
        Assert.Equal(loading, children[0]);
        Assert.Equal("结果", frames.World.Read<TextView>(children[1]).Value);
    }

    [Fact]
    public void NextFrameDisplaysEachOfThirtyStepsInItsOwnFrame()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new FrameSteps());
        frames.Frame();
        Assert.Equal(0, frames.World.Read<DisplayedStep>(root).Number);

        for (int step = 1; step <= 30; step++)
        {
            frames.Frame();
            Assert.Equal(step, frames.World.Read<DisplayedStep>(root).Number);
        }
        frames.Frame();
        Assert.Equal(30, frames.World.Read<DisplayedStep>(root).Number);
    }

    [Fact]
    public void WorkerCompletionWithConfigureAwaitFalseWritesContentOnTheRebuildThread()
    {
        using var frames = new HeadlessFrameDriver();
        var completion = new TaskCompletionSource<string>();
        int rebuildThread = Environment.CurrentManagedThreadId;
        Entity root = Views.Spawn(frames.World, Entity.Null, new ConfiguredView(completion.Task));
        frames.Frame();

        int workerThread = 0;
        var worker = new Thread(() =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            completion.SetResult("worker result");
        });
        worker.Start();
        worker.Join();
        Assert.Equal("加载中", frames.World.Read<DisplayText>(root).Value);

        frames.Frame();

        Assert.NotEqual(rebuildThread, workerThread);
        Assert.Equal(rebuildThread, frames.World.Read<DisplayedThread>(root).Id);
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal("worker result", frames.World.Read<TextView>(child).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadyViewsDisplayTheirResultsByDepthThenEntityRegardlessOfCompletionOrder(bool reverse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity history = frames.World.CreateEntity(new DisplayText(""));
        var parent = new TaskCompletionSource();
        var child = new TaskCompletionSource();
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        Views.Spawn(frames.World, Entity.Null, new OrderedGroup(parent.Task, child.Task, history));
        frames.Frame();
        Views.Spawn(frames.World, Entity.Null, new OrderedView("first", first.Task, history));
        Views.Spawn(frames.World, Entity.Null, new OrderedView("second", second.Task, history));
        frames.Frame();

        TaskCompletionSource[] completions = reverse
            ? [second, child, first, parent]
            : [parent, first, child, second];
        foreach (var completion in completions)
            completion.SetResult();
        Assert.Equal("", frames.World.Read<DisplayText>(history).Value);

        frames.Frame();

        Assert.Equal("parent;first;second;child", frames.World.Read<DisplayText>(history).Value);
    }

    [Fact]
    public void BuildTaskHelperRetainsKeysAndReturnsToTheCallingBuildInAFollowingPhase()
    {
        using var frames = new HeadlessFrameDriver();
        var completion = new TaskCompletionSource<string>();
        Entity root = Views.Spawn(frames.World, Entity.Null, new HelperView(completion.Task));
        frames.Frame();
        Entity first = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal(77ul, frames.World.Read<ElementKey>(first).Key);
        var worker = new Thread(() => completion.SetResult("helper result"));
        worker.Start();
        worker.Join();

        frames.Frame();

        var children = Hierarchy<ElementTree>.GetChildren(frames.World, root);
        Assert.Equal(2, children.Count);
        Assert.Equal("helper result", frames.World.Read<TextView>(children[1]).Value);
        Assert.Equal(77ul, frames.World.Read<ElementKey>(children[1]).Key);
        Assert.Equal(1, frames.World.Read<ElementKey>(children[1]).Occurrence);

        frames.Frame();

        children = Hierarchy<ElementTree>.GetChildren(frames.World, root);
        Assert.Equal(3, children.Count);
        Assert.Equal("done", frames.World.Read<TextView>(children[2]).Value);
        Assert.Equal(0ul, frames.World.Read<ElementKey>(children[2]).Key);
    }

    [Fact]
    public void PreviousChildrenRemainThroughSuspensionAndAreReleasedWhenTheWholeBuildCompletes()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ChangingGroup(null));
        frames.Frame();
        Entity[] previous = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Assert.Equal(3, previous.Length);
        var completion = new TaskCompletionSource();
        frames.World.Replace(root, new ChangingGroup(completion.Task));

        frames.Frame();

        Assert.Equal(previous, Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray());
        frames.Frame();
        Assert.Equal(previous, Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray());
        completion.SetResult();

        frames.Frame();

        var children = Hierarchy<ElementTree>.GetChildren(frames.World, root);
        Assert.Equal(2, children.Count);
        Assert.Equal(previous[0], children[0]);
        Assert.Equal("new", frames.World.Read<TextView>(children[1]).Value);
        Assert.False(frames.World.IsAlive(previous[1]));
        Assert.False(frames.World.IsAlive(previous[2]));
    }

    [Fact]
    public void NotifyCompletionAwaiterDisplaysItsResultInTheRebuildPhase()
    {
        using var frames = new HeadlessFrameDriver();
        var wait = new ManualWait();
        Entity root = Views.Spawn(frames.World, Entity.Null, new WaitingView(wait));
        frames.Frame();
        Assert.Equal("waiting", frames.World.Read<DisplayText>(root).Value);
        wait.Complete();
        Assert.Equal("waiting", frames.World.Read<DisplayText>(root).Value);

        frames.Frame();

        Assert.Equal("ready", frames.World.Read<DisplayText>(root).Value);
    }

#if !DEBUG
    [Fact]
    public void SynchronouslyCompletedBuildsUseZeroManagedBytesAfterWarmup()
    {
        var view = new CompletedView();
        for (int build = 0; build < 10_000; build++)
            Complete(view.Build());

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int build = 0; build < 10_000; build++)
            Complete(view.Build());
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }
#endif

    [Fact]
    public void FaultedBuildRecordsItsErrorWhileOtherReadyContentCompletesInTheSameFrame()
    {
        using var frames = new HeadlessFrameDriver();
        var failed = new TaskCompletionSource<string>();
        var ready = new TaskCompletionSource<string>();
        Entity first = Views.Spawn(frames.World, Entity.Null, new ResultView(failed.Task));
        Entity second = Views.Spawn(frames.World, Entity.Null, new ResultView(ready.Task));
        frames.Frame();
        var failure = new ApplicationException("failed result");
        failed.SetException(failure);
        ready.SetResult("ready result");

        frames.Frame();

        Assert.Same(failure, frames.World.ReadSparse<BuildError>(first).Exception);
        Assert.Equal(2ul, frames.World.ReadSparse<BuildError>(first).Frame);
        Assert.Equal("ready result", frames.World.Read<DisplayText>(second).Value);
        var recovered = new TaskCompletionSource<string>();
        Entity third = Views.Spawn(frames.World, Entity.Null, new ResultView(recovered.Task));
        frames.Frame();
        recovered.SetResult("recovered result");
        frames.Frame();
        Assert.Equal("recovered result", frames.World.Read<DisplayText>(third).Value);
    }

    [Fact]
    public void EachWorldDisplaysReadyContentInItsOwnFrame()
    {
        using var first = new HeadlessFrameDriver();
        using var second = new HeadlessFrameDriver();
        var firstResult = new TaskCompletionSource<string>();
        var secondResult = new TaskCompletionSource<string>();
        Entity firstRoot = Views.Spawn(first.World, Entity.Null, new ResultView(firstResult.Task));
        Entity secondRoot = Views.Spawn(second.World, Entity.Null, new ResultView(secondResult.Task));
        first.Frame();
        second.Frame();
        firstResult.SetResult("first world");

        second.Frame();
        Assert.Equal("waiting", first.World.Read<DisplayText>(firstRoot).Value);
        Assert.Equal("waiting", second.World.Read<DisplayText>(secondRoot).Value);
        first.Frame();
        Assert.Equal("first world", first.World.Read<DisplayText>(firstRoot).Value);
        Assert.Equal("waiting", second.World.Read<DisplayText>(secondRoot).Value);
        secondResult.SetResult("second world");
        second.Frame();
        Assert.Equal("second world", second.World.Read<DisplayText>(secondRoot).Value);
    }

    [Fact]
    public void AsyncHelperRetainsItsContentParentUntilTheCallingBuildCompletes()
    {
        using var frames = new HeadlessFrameDriver();
        var wait = new ManualWait();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ContentHelperView(wait));
        frames.Frame();
        Entity group = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity before = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, group));
        Assert.Equal("before", frames.World.Read<TextView>(before).Value);
        wait.Complete();

        frames.Frame();

        Assert.Equal(group, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        var content = Hierarchy<ElementTree>.GetChildren(frames.World, group);
        Assert.Equal(2, content.Count);
        Assert.Equal("result", frames.World.Read<TextView>(content[1]).Value);

        frames.Frame();

        var children = Hierarchy<ElementTree>.GetChildren(frames.World, root);
        Assert.Equal(2, children.Count);
        Assert.Equal(group, children[0]);
        Assert.Equal("done", frames.World.Read<TextView>(children[1]).Value);
        Assert.Equal(2, Hierarchy<ElementTree>.GetChildren(frames.World, group).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuspendedFailureRestoresPreviousChildParametersAndOrder(bool nested)
    {
        using var frames = new HeadlessFrameDriver();
        var error = new InvalidOperationException("async content failed");
        Entity root = nested
            ? Views.Spawn(frames.World, Entity.Null, new AsyncGroupParent(Task.CompletedTask, false, error))
            : Views.Spawn(frames.World, Entity.Null, new AsyncChangingGroup(Task.CompletedTask, false, error));
        frames.Frame();
        Entity group = nested ? Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)) : root;
        Entity[] previous = Hierarchy<ElementTree>.GetChildren(frames.World, group).ToArray();
        var completion = new TaskCompletionSource();
        if (nested)
            frames.World.Replace(root, new AsyncGroupParent(completion.Task, true, error));
        else
            frames.World.Replace(root, new AsyncChangingGroup(completion.Task, true, error));

        frames.Frame();

        Assert.Equal("changed third", frames.World.Read<TextView>(previous[2]).Value);
        Assert.Equal(4, Hierarchy<ElementTree>.GetChildren(frames.World, group).Count);
        completion.SetResult();
        frames.Frame();

        Assert.Same(error, frames.World.ReadSparse<BuildError>(group).Exception);
        Assert.Equal(3ul, frames.World.ReadSparse<BuildError>(group).Frame);
        Assert.Equal(previous, Hierarchy<ElementTree>.GetChildren(frames.World, group).ToArray());
        Assert.Equal("second", frames.World.Read<TextView>(previous[1]).Value);
        Assert.Equal("third", frames.World.Read<TextView>(previous[2]).Value);
    }

    private readonly record struct AsyncGroupParent(Task Result, bool Fail, Exception Error) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new AsyncChangingGroup(Result, Fail, Error), 0);
            return default;
        }
    }

    private readonly record struct AsyncChangingGroup(Task Result, bool Fail, Exception Error) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Scope.View(new TextView("first"), 1);
            if (Fail)
            {
                Scope.View(new TextView("changed third"), 3);
                Scope.View(new TextView("added"), 4);
                await Result;
                throw Error;
            }
            Scope.View(new TextView("second"), 2);
            Scope.View(new TextView("third"), 3);
        }
    }

    private readonly record struct ContentHelperView(ManualWait Wait) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Entity group = Scope.View(new Group(), 0);
            BuildTask content;
            using (group.Content())
                content = AddContent(Wait);
            await content;
            Scope.View(new TextView("done"), 0);
        }

        private static async BuildTask AddContent(ManualWait wait)
        {
            Scope.View(new TextView("before"), 0);
            await wait;
            Scope.View(new TextView("result"), 0);
        }
    }

    private readonly record struct ResultView(Task<string> Result) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public async BuildTask Build()
        {
            Scope.World.Add(Scope.Entity, new DisplayText("waiting"));
            string result = await Result;
            Scope.World.Replace(Scope.Entity, new DisplayText(result));
        }
    }

#if DEBUG
    [Fact]
    public void TaskHelperScopeUseAfterSuspensionReportsTheRequiredBuildScope()
    {
        using var frames = new HeadlessFrameDriver();
        var completion = new TaskCompletionSource();
        Task method = null!;
        Entity root = Views.Spawn(frames.World, Entity.Null,
            new TaskHelperView(completion.Task, result => method = result));
        frames.Frame();
        var worker = new Thread(completion.SetResult);
        worker.Start();
        worker.Join();

        Assert.True(method.IsFaulted);
        var error = Assert.Throws<InvalidOperationException>(() => Complete(method));
        Assert.Contains("active view build", error.Message, StringComparison.OrdinalIgnoreCase);
        frames.Frame();
        Assert.Same(error, frames.World.ReadSparse<BuildError>(root).Exception);
    }

    private readonly record struct TaskHelperView(Task Result, Action<Task> TaskOutput) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Task result = AddContent(Result);
            TaskOutput(result);
            await result;
        }

        private static async Task AddContent(Task result)
        {
            await result.ConfigureAwait(false);
            Scope.View(new TextView("helper"), 0);
        }
    }

    private static void Complete(Task task) => task.GetAwaiter().GetResult();
#endif

    private static void Complete(BuildTask task) => task.GetAwaiter().GetResult();

    private readonly record struct CompletedView : IView, IComponent
    {
        public async BuildTask Build()
        {
            await Task.CompletedTask;
            await ValueTask.CompletedTask;
            await default(BuildTask);
        }
    }

    private sealed class ManualWait : INotifyCompletion
    {
        private Action? _continuation;

        public ManualWait GetAwaiter() => this;
        public bool IsCompleted { get; private set; }
        public void GetResult() { }
        public void OnCompleted(Action continuation) => _continuation = continuation;

        public void Complete()
        {
            IsCompleted = true;
            _continuation!();
        }
    }

    private readonly record struct WaitingView(ManualWait Wait) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Scope.World.Add(Scope.Entity, new DisplayText("waiting"));
            await Wait;
            Scope.World.Replace(Scope.Entity, new DisplayText("ready"));
        }
    }

    private readonly record struct ChangingGroup(Task? Result) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Scope.View(new TextView("first"), 1);
            if (Result is { } result)
            {
                await result;
                Scope.View(new TextView("new"), 4);
            }
            else
            {
                Scope.View(new TextView("old second"), 2);
                Scope.View(new TextView("old third"), 3);
            }
        }
    }

    private readonly record struct HelperView(Task<string> Result) : IView, IComponent
    {
        public async BuildTask Build()
        {
            await AddContent(Result);
            Scope.View(new TextView("done"), 0);
        }

        private static async BuildTask AddContent(Task<string> result)
        {
            using (Scope.Key(77))
            {
                Scope.View(new TextView("before"), 0);
                string text = await result.ConfigureAwait(false);
                Scope.View(new TextView(text), 0);
            }
        }
    }

    private readonly record struct OrderedGroup(Task Result, Task ChildResult, Entity History) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Scope.View(new OrderedView("child", ChildResult, History), 0);
            await Result;
            Append(History, "parent");
        }
    }

    private readonly record struct OrderedView(string Text, Task Result, Entity History) : IView, IComponent
    {
        public async BuildTask Build()
        {
            await Result;
            Append(History, Text);
        }
    }

    private static void Append(Entity entity, string text)
    {
        string previous = Scope.World.Read<DisplayText>(entity).Value;
        Scope.World.Replace(entity, new DisplayText(previous.Length == 0 ? text : $"{previous};{text}"));
    }

    private readonly record struct ConfiguredView(Task<string> Result) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Scope.World.Add(Scope.Entity, new DisplayText("加载中"));
            string result = await Result.ConfigureAwait(false);
            Scope.World.Replace(Scope.Entity, new DisplayText(result));
            Scope.World.Add(Scope.Entity, new DisplayedThread(Environment.CurrentManagedThreadId));
            Scope.View(new TextView(result), 0);
        }
    }

    private readonly record struct DisplayedThread(int Id) : IComponent;

    private readonly record struct FrameSteps : IView, IComponent
    {
        public async BuildTask Build()
        {
            Scope.World.Add(Scope.Entity, new DisplayedStep(0));
            for (int step = 1; step <= 30; step++)
            {
                await Scope.NextFrame();
                Scope.World.Replace(Scope.Entity, new DisplayedStep(step));
            }
        }
    }

    private readonly record struct DisplayedStep(int Number) : IComponent;

    private readonly record struct LoadingView(Task<string> Result) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Scope.World.Add(Scope.Entity, new DisplayText("加载中"));
            Scope.View(new TextView("加载中"), 0);
            string result = await Result;
            Scope.World.Replace(Scope.Entity, new DisplayText(result));
            Scope.View(new TextView(result), 0);
        }
    }

    private readonly record struct TextView(string Value) : IView, IComponent
    {
        public BuildTask Build() => default;
    }

    private readonly record struct DisplayText(string Value) : IComponent;
}
