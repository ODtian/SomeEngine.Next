using System.Collections.Concurrent;
using System.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed partial class BuildCancellationTests
{
    [Fact]
    public void ChangedDependencyRestartsTheBuildAndReleasesItsPreviousWait()
    {
        using var frames = new HeadlessFrameDriver();
        var value = new State<string>("old");
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        string? cleaned = null;
        CancellationToken previous = default;
        Entity root = Views.Spawn(frames.World, Entity.Null,
            new ChangingContent(value, first.Task, second.Task,
                token => previous = token, text => cleaned = text));
        frames.Frame();
        Assert.Equal("waiting old", Text(frames, root));

        value.Value = "new";
        frames.Frame();

        Assert.True(previous.IsCancellationRequested);
        Assert.Equal("waiting new", Text(frames, root));
        frames.Frame();
        Assert.Equal("old", cleaned);
        second.SetResult();
        frames.Frame();
        Assert.Equal("new", Text(frames, root));
        Assert.False(frames.World.HasSparse<BuildError>(root));
        first.SetResult();
        frames.Frame();
        Assert.Equal("new", Text(frames, root));
    }

    private static string Text(HeadlessFrameDriver frames, Entity root) =>
        frames.World.Read<Label>(Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root))).Value;

    [Fact]
    public void RemovingAWaitingOnceViewCancelsItsIoAndRunsFinallyAndUsing()
    {
        using var frames = new HeadlessFrameDriver();
        var visible = new State<bool>(true);
        var completion = new TaskCompletionSource();
        var cleanup = new CleanupData();
        Entity root = Views.Spawn(frames.World, Entity.Null,
            new ConditionalContent(visible, completion.Task, cleanup));
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));

        visible.Value = false;
        frames.Frame();

        Assert.False(frames.World.IsAlive(child));
        Assert.True(cleanup.Cancel.IsCancellationRequested);
        frames.Frame();
        Assert.True(cleanup.Finally);
        Assert.True(cleanup.Disposed);
        Assert.Empty(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.False(frames.World.HasSparse<BuildError>(root));
    }

    [Fact]
    public void ClosingTheViewSystemsCancelsAndReleasesAFrameWait()
    {
        var cleanup = new CleanupData();
        using (var frames = new HeadlessFrameDriver())
        {
            Views.Spawn(frames.World, Entity.Null, new FrameCleanup(cleanup));
            frames.Frame();
        }

        Assert.True(cleanup.Cancel.IsCancellationRequested);
        Assert.True(cleanup.Finally);
        Assert.True(cleanup.Disposed);
    }

    private readonly record struct FrameCleanup(CleanupData Cleanup) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Cleanup.Cancel = Scope.Cancel;
            try
            {
                using (Cleanup)
                    await Scope.NextFrame();
            }
            finally
            {
                Cleanup.Finally = true;
            }
        }
    }

    [Fact]
    public void CanceledBuildTaskHelperKeepsTheNewOutputAndReleasesItsCallingBuild()
    {
        using var frames = new HeadlessFrameDriver();
        var value = new State<string>("old");
        var completion = new TaskCompletionSource();
        var cleanup = new CleanupData();
        Entity target = frames.World.CreateEntity(new Number());
        Entity root = Views.Spawn(frames.World, Entity.Null, new HelperContent(value, completion.Task, target, cleanup));
        frames.Frame();
        value.Value = "new";
        frames.Frame();
        Assert.True(cleanup.Cancel.IsCancellationRequested);
        Assert.Equal("new", Text(frames, root));
        var worker = new Thread(completion.SetResult);
        worker.Start();
        worker.Join();

        frames.Frame();

        Assert.Equal(42, frames.World.Read<Number>(target).Value);
        frames.Frame();
        Assert.True(cleanup.Finally);
        Assert.True(cleanup.Disposed);
        Assert.Equal("new", Text(frames, root));
        Assert.False(frames.World.HasSparse<BuildError>(root));
    }

    private readonly record struct HelperContent(State<string> Value, Task Result, Entity Target, CleanupData Cleanup) : IView, IComponent
    {
        public async BuildTask Build()
        {
            string text = Value.Value;
            CancellationToken cancel = Scope.Cancel;
            Entity label = Scope.View(new Label($"waiting {text}"), 1);
            try
            {
                if (text == "old")
                {
                    Cleanup.Cancel = cancel;
                    using (Cleanup)
                        await WriteFromTask(Result, Target);
                }
                Scope.Write<Number>(Target) = new Number(42);
                Scope.Write<Label>(label) = new Label(text);
            }
            finally
            {
                if (cancel.IsCancellationRequested)
                    Cleanup.Finally = true;
            }
        }

        private static async BuildTask WriteFromTask(Task result, Entity target)
        {
            await result.ConfigureAwait(false);
            Scope.Write<Number>(target) = new Number(99);
        }
    }

    private readonly record struct Number(int Value) : IComponent;

    [Fact]
    public void ReadsFromCanceledCleanupKeepTheNewViewsDependenciesSeparate()
    {
        using var frames = new HeadlessFrameDriver();
        var value = new State<string>("old");
        var display = new State<string>("original");
        var source = new State<int>();
        var completion = new TaskCompletionSource();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ReadingHelperContent(value, display, source, completion.Task));
        frames.Frame();
        value.Value = "new";
        frames.Frame();
        var worker = new Thread(completion.SetResult);
        worker.Start();
        worker.Join();
        frames.Frame();
        frames.Frame();
        Assert.Equal("new: original", Text(frames, root));

        display.Value = "updated";
        source.Value = 1;
        frames.Frame();

        Assert.Equal("new: original", Text(frames, root));
        Assert.False(frames.World.HasSparse<BuildError>(root));
    }

    private readonly record struct ReadingHelperContent(State<string> Value, State<string> Display,
        State<int> Source, Task Result) : IView, IComponent
    {
        public async BuildTask Build()
        {
            string value = Value.Value;
            string text;
            using (Scope.Untrack())
                text = Display.Value;
            Scope.View(new Label($"{value}: {text}"), 1);
            if (value == "old")
                await ReadFromTask(Result, Source);
        }

        private static async BuildTask ReadFromTask(Task result, State<int> source)
        {
            await result.ConfigureAwait(false);
            _ = source.Value;
        }
    }

    [Fact]
    public void OnceContentKeepsItsBuildWhileTheReadStateChanges()
    {
        using var frames = new HeadlessFrameDriver();
        var value = new State<string>("old");
        var completion = new TaskCompletionSource();
        var cleanup = new CleanupData();
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceContent(value, completion.Task, cleanup));
        frames.Frame();

        value.Value = "new";
        frames.Frame();

        Assert.False(cleanup.Cancel.IsCancellationRequested);
        Assert.Equal("waiting old", Text(frames, root));
        var worker = new Thread(completion.SetResult);
        worker.Start();
        worker.Join();
        frames.Frame();
        Assert.Equal("old", Text(frames, root));
        Assert.True(cleanup.Finally);
        Assert.True(cleanup.Disposed);
        Assert.False(frames.World.HasSparse<BuildError>(root));
    }

    private readonly record struct OnceContent(State<string> Value, Task Result, CleanupData Cleanup) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public async BuildTask Build()
        {
            Cleanup.Cancel = Scope.Cancel;
            string text = Value.Value;
            Entity label = Scope.View(new Label($"waiting {text}"), 1);
            try
            {
                using (Cleanup)
                    await Result.WaitAsync(Cleanup.Cancel).ConfigureAwait(false);
                Scope.Write<Label>(label) = new Label(text);
            }
            finally
            {
                Cleanup.Finally = true;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedParametersRestartTheWaitingRootOrChild(bool child)
    {
        using var frames = new HeadlessFrameDriver();
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        var previous = new CleanupData();
        var current = new CleanupData();
        Entity root = child
            ? Views.Spawn(frames.World, Entity.Null, new ParameterGroup("old", first.Task, previous))
            : Views.Spawn(frames.World, Entity.Null, new ParameterContent("old", first.Task, previous));
        frames.Frame();
        Entity content = child ? Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)) : root;

        if (child)
            frames.World.Replace(root, new ParameterGroup("new", second.Task, current));
        else
            frames.World.Replace(root, new ParameterContent("new", second.Task, current));
        frames.Frame();

        Assert.True(previous.Cancel.IsCancellationRequested);
        Assert.Equal("waiting new", Text(frames, content));
        Assert.True(SpinWait.SpinUntil(() =>
        {
            frames.Frame();
            return previous.Finally && previous.Disposed;
        }, TimeSpan.FromSeconds(5)), "The canceled external wait completes cleanup in following rebuild frames.");
        Assert.False(current.Cancel.IsCancellationRequested);
        var worker = new Thread(second.SetResult);
        worker.Start();
        worker.Join();
        frames.Frame();
        Assert.Equal("new", Text(frames, content));
        Assert.False(frames.World.HasSparse<BuildError>(content));
    }

    private readonly record struct ParameterGroup(string Value, Task Result, CleanupData Cleanup) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new ParameterContent(Value, Result, Cleanup), 1);
            return default;
        }
    }

    private readonly record struct ParameterContent(string Value, Task Result, CleanupData Cleanup) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Cleanup.Cancel = Scope.Cancel;
            Entity label = Scope.View(new Label($"waiting {Value}"), 1);
            try
            {
                using (Cleanup)
                    await Result.WaitAsync(Cleanup.Cancel).ConfigureAwait(false);
                Scope.Write<Label>(label) = new Label(Value);
            }
            finally
            {
                Cleanup.Finally = true;
            }
        }
    }

    private sealed class CleanupData : IDisposable
    {
        internal CancellationToken Cancel;
        internal bool Finally;
        internal bool Disposed;

        public void Dispose() => Disposed = true;
    }

#if DEBUG
    [Fact]
    public void DeclaringAViewDuringCanceledFinallyReportsADevelopmentCheck()
    {
        using var frames = new HeadlessFrameDriver();
        var waiting = new State<bool>(true);
        var completion = new TaskCompletionSource();
        Entity root = Views.Spawn(frames.World, Entity.Null, new FinallyDeclaration(waiting, completion.Task));
        frames.Frame();
        using var messages = new CheckMessages();
        System.Diagnostics.Trace.Listeners.Add(messages);
        try
        {
            waiting.Value = false;
            frames.Frame();
            frames.Frame();
        }
        finally
        {
            System.Diagnostics.Trace.Listeners.Remove(messages);
        }

        Assert.Contains(messages.Values, message => message.Contains("canceled build", StringComparison.OrdinalIgnoreCase));
        Assert.False(frames.World.HasSparse<BuildError>(root));
        Assert.Equal("ready", Text(frames, root));
    }

    private sealed class CheckMessages : TraceListener
    {
        internal ConcurrentQueue<string> Values { get; } = new();

        public override void Write(string? message)
        {
            if (message is not null)
                Values.Enqueue(message);
        }

        public override void WriteLine(string? message) => Write(message);
    }

    private readonly record struct FinallyDeclaration(State<bool> Waiting, Task Result) : IView, IComponent
    {
        public async BuildTask Build()
        {
            CancellationToken cancel = Scope.Cancel;
            try
            {
                if (Waiting.Value)
                    await Result.WaitAsync(cancel);
                Scope.View(new Label("ready"), 1);
            }
            finally
            {
                if (cancel.IsCancellationRequested)
                    Scope.View(new Label("cleanup"), 2);
            }
        }
    }
#endif

    private readonly record struct ConditionalContent(State<bool> Visible, Task Result, CleanupData Cleanup) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (Visible.Value)
                Scope.View(new WaitingOnce(Result, Cleanup), 1);
            return default;
        }
    }

    private readonly record struct WaitingOnce(Task Result, CleanupData Cleanup) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public async BuildTask Build()
        {
            Cleanup.Cancel = Scope.Cancel;
            try
            {
                using (Cleanup)
                    await Result.WaitAsync(Cleanup.Cancel);
                Scope.View(new Label("ready"), 1);
            }
            finally
            {
                Cleanup.Finally = true;
            }
        }
    }

    private readonly record struct ChangingContent(State<string> Value, Task First, Task Second,
        Action<CancellationToken> TokenOutput, Action<string> Cleaned) : IView, IComponent
    {
        public async BuildTask Build()
        {
            string text = Value.Value;
            CancellationToken cancel = Scope.Cancel;
            if (text == "old")
                TokenOutput(cancel);
            Entity label = Scope.View(new Label($"waiting {text}"), 1);
            try
            {
                await (text == "old" ? First : Second).WaitAsync(cancel);
                Scope.Write<Label>(label) = new Label(text);
            }
            finally
            {
                if (cancel.IsCancellationRequested)
                    Cleaned(text);
            }
        }
    }

    private readonly record struct Label(string Value) : IView, IComponent
    {
        public BuildTask Build() => default;
    }
}
