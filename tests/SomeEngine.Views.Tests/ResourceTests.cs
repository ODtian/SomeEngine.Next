using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed partial class ResourceTests
{
    [Fact]
    public void ANewSourceRecoversTheFailedResourceAndItsErrorOnlyReader() =>
        AsyncPrimitiveChecks.VerifyResourceRecovery();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheCurrentValueSurvivesALateResultOrErrorFromThePreviousSource(bool fail)
    {
        using var frames = new HeadlessFrameDriver();
        var source = new State<int>(1);
        var requests = new Requests();
        Entity root = Views.Spawn(frames.World, Entity.Null, new Content(source, requests));
        frames.Frame();
        CancellationToken previous = requests.Cancellation[1];
        source.Value = 2;
        frames.Frame();
        Assert.True(previous.IsCancellationRequested);
        Complete(requests.Results[2], "current");
        frames.Frame();
        var worker = new Thread(() =>
        {
            if (fail)
                requests.Results[1].SetException(new InvalidOperationException("previous"));
            else
                requests.Results[1].SetResult("previous");
        });
        worker.Start();
        worker.Join();

        frames.Frame();

        Assert.Equal("current: False", Text(frames, root));
        Assert.Equal("current", requests.Resource!.Value);
        Assert.Null(requests.Resource.Error);
    }

    [Fact]
    public void AOnceViewsCalculationKeepsTheResourceValueWhenItsSourceChanges()
    {
        using var frames = new HeadlessFrameDriver();
        var source = new State<int>(1);
        var requests = new Requests();
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceContent(source, requests));
        frames.Frame();
        Complete(requests.Results[1], "old");
        frames.Frame();
        Assert.Equal("old: False", Text(frames, root));

        source.Value = 2;
        frames.Frame();

        Assert.Equal("old: True", Text(frames, root));
        Complete(requests.Results[2], "new");
        frames.Frame();
        Assert.Equal("new: False", Text(frames, root));
    }

    [Fact]
    public void AFailedRequestUpdatesAViewThatReadsOnlyTheError()
    {
        using var frames = new HeadlessFrameDriver();
        var requests = new Requests();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ErrorContent(requests));
        frames.Frame();
        Assert.Equal("ready", Text(frames, root));
        var error = new InvalidOperationException("request failed");
        var worker = new Thread(() => requests.Results[1].SetException(error));
        worker.Start();
        worker.Join();

        frames.Frame();

        Assert.Same(error, requests.Resource!.Error);
        Assert.False(requests.Resource.Loading);
        Assert.Equal("request failed", Text(frames, root));
    }

    [Fact]
    public void ChangingTheSourceKeepsTheValueWhileLoadingAndThenDisplaysTheNewValue()
    {
        using var frames = new HeadlessFrameDriver();
        var source = new State<int>(1);
        var requests = new Requests();
        Entity root = Views.Spawn(frames.World, Entity.Null, new Content(source, requests));
        frames.Frame();
        Assert.True(requests.Resource!.Loading);
        Complete(requests.Results[1], "old");
        frames.Frame();
        Assert.Equal("old: False", Text(frames, root));

        source.Value = 2;
        frames.Frame();

        Assert.Equal("old", requests.Resource.Value);
        Assert.True(requests.Resource.Loading);
        Assert.Equal("old: True", Text(frames, root));
        Complete(requests.Results[2], "new");
        frames.Frame();
        Assert.Equal("new", requests.Resource.Value);
        Assert.False(requests.Resource.Loading);
        Assert.Equal("new: False", Text(frames, root));
    }

    private static string Text(HeadlessFrameDriver frames, Entity root) =>
        frames.World.Read<Label>(Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root))).Value;

    private static void Complete(TaskCompletionSource<string> result, string value)
    {
        var worker = new Thread(() => result.SetResult(value));
        worker.Start();
        worker.Join();
    }

    private sealed class Requests
    {
        internal TaskCompletionSource<string>[] Results { get; } =
            [new(), new(), new(), new()];
        internal Resource<string>? Resource;
        internal Dictionary<int, CancellationToken> Cancellation { get; } = new();

        internal ValueTask<string> Fetch(int source, CancellationToken cancel)
        {
            Cancellation[source] = cancel;
            return new(Results[source].Task);
        }
    }

    private readonly record struct Content(State<int> Source, Requests Requests) : IView, IComponent
    {
        public BuildTask Build()
        {
            Resource<string> resource = Scope.Resource(Source.Value, Requests.Fetch);
            Requests.Resource = resource;
            Scope.View(new Label($"{resource.Value}: {resource.Loading}"), 1);
            return default;
        }
    }

    private readonly record struct ErrorContent(Requests Requests) : IView, IComponent
    {
        public BuildTask Build()
        {
            Resource<string> resource = Scope.Resource(1, Requests.Fetch);
            Requests.Resource = resource;
            Scope.View(new Label(resource.Error?.Message ?? "ready"), 1);
            return default;
        }
    }

    private readonly record struct OnceContent(State<int> Source, Requests Requests) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            Entity label = Scope.View(new Label(""), 1);
            State<int> source = Source;
            Requests requests = Requests;
            Scope.Compute(() =>
            {
                Resource<string> resource = Scope.Resource(source.Value, requests.Fetch);
                requests.Resource = resource;
                Scope.Write<Label>(label) = new Label($"{resource.Value}: {resource.Loading}");
            });
            return default;
        }
    }

    private readonly record struct Label(string Value) : IView, IComponent
    {
        public BuildTask Build() => default;
    }
}
