using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed partial class LaunchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulAsynchronousChildClearsItsCancellationCallbackError(bool jobWorld) =>
        BuildResumeChecks.VerifyLaunchCallbackRecovery(jobWorld);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulAsynchronousChildrenPreserveOtherChildErrors(bool jobWorld) =>
        BuildResumeChecks.VerifyLaunchErrors(jobWorld);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulAsynchronousChildClearsItsPreviousError(bool jobWorld) =>
        BuildResumeChecks.VerifyLaunchRecovery(jobWorld);

    [Fact]
    public void AnUnchangedKeyKeepsItsTaskUntilTheViewIsReleased()
    {
        using var frames = new HeadlessFrameDriver();
        var key = new State<int>(1);
        var work = new Work();
        Entity root = Views.Spawn(frames.World, Entity.Null, new Content(key, work));
        frames.Frame();
        CancellationToken cancel = work.Cancellation[1];
        work.Status.Value = "edited";

        frames.Frame();

        Assert.Equal("edited", Text(frames, root));
        Assert.False(cancel.IsCancellationRequested);
        Hierarchy<ElementTree>.DestroySubtree(frames.World, root);
        Assert.True(cancel.IsCancellationRequested);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            frames.Frame();
            return work.Finished.Contains(1);
        }, TimeSpan.FromSeconds(5)));
        Assert.Contains(1, work.Finished);
    }

    [Fact]
    public void ConcurrentTasksResumeAlongsideTheirViewAndFinishCleanupWhenReleased() =>
        AsyncPrimitiveChecks.VerifyConcurrentBuild();

    [Fact]
    public void ChangingTheKeyCancelsThePreviousTaskAndStartsTheNewTask()
    {
        using var frames = new HeadlessFrameDriver();
        var key = new State<int>(1);
        var work = new Work();
        Entity root = Views.Spawn(frames.World, Entity.Null, new Content(key, work));
        frames.Frame();
        CancellationToken first = work.Cancellation[1];
        Assert.Equal("running 1", Text(frames, root));

        key.Value = 2;
        frames.Frame();

        Assert.True(first.IsCancellationRequested);
        Assert.Equal("running 2", Text(frames, root));
        Assert.True(SpinWait.SpinUntil(() =>
        {
            frames.Frame();
            return work.Finished.Contains(1);
        }, TimeSpan.FromSeconds(5)));
        Assert.Contains(1, work.Finished);
    }

    private static string Text(HeadlessFrameDriver frames, Entity root) =>
        frames.World.Read<Label>(Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root))).Value;

    private sealed class Work
    {
        internal State<string> Status { get; } = new("");
        internal Dictionary<int, CancellationToken> Cancellation { get; } = new();
        internal HashSet<int> Finished { get; } = new();

        internal async BuildTask Run(int key, CancellationToken cancel)
        {
            Cancellation[key] = cancel;
            Status.Value = $"running {key}";
            var completion = new TaskCompletionSource();
            using var registration = cancel.Register(() => completion.TrySetCanceled(cancel));
            try
            {
                await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                Finished.Add(key);
            }
        }
    }

    private readonly record struct Content(State<int> Key, Work Work) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Launch(Key.Value, Work.Run);
            Scope.View(new Label(Work.Status.Value), 1);
            return default;
        }
    }

    private readonly record struct Label(string Value) : IView, IComponent
    {
        public BuildTask Build() => default;
    }
}
