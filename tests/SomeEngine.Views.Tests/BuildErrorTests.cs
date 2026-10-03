using System.Collections.Concurrent;
using System.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed partial class BuildErrorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedCallerContentRestoresTheContainerChildren(bool insideContent)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity(new Content(1));
        var error = new InvalidOperationException("caller content failed");
        Entity root = Views.Spawn(frames.World, Entity.Null, new ChangingCallerContent(source, insideContent, error));
        frames.Frame();
        Entity group = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, group).ToArray();
        frames.World.Replace(source, new Content(2));

        frames.Frame();

        Assert.Same(error, frames.World.ReadSparse<BuildError>(root).Exception);
        Assert.Equal(group, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        Assert.Equal(children, Hierarchy<ElementTree>.GetChildren(frames.World, group).ToArray());
        Assert.Equal(new[] { "first", "second" }, children.Select(child => frames.World.Read<Text>(child).Value));
        Entity tip = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, children[1]));
        Assert.Equal("second", frames.World.Read<Tip>(tip).Value);
    }

    [Fact]
    public void FailedParentRestoresThePreviousCallbackInEqualChildParameters()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity(new Content(1));
        Entity root = Views.Spawn(frames.World, Entity.Null, new CallbackGroup(source));
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        frames.World.Replace(source, new Content(2));

        frames.Frame();

        Assert.Equal(child, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        Assert.Equal("before", frames.World.ReadSparse<CallbackText>(child).TextCallback());
        Assert.Equal(2UL, frames.World.ReadSparse<BuildError>(root).Frame);
    }

    [Fact]
    public void CompletedAsyncFailureRetainsTheOriginalExceptionAndReportsItInDevelopment()
    {
        using var frames = new HeadlessFrameDriver();
        var error = new InvalidOperationException("completed async content failed");
        Entity source = frames.World.CreateEntity(new Content(2));
        Entity root = Views.Spawn(frames.World, Entity.Null, new AsyncContent(source, error));
        using var messages = new ErrorMessages();
        System.Diagnostics.Trace.Listeners.Add(messages);
        try
        {
            frames.Frame();
        }
        finally
        {
            System.Diagnostics.Trace.Listeners.Remove(messages);
        }

        Assert.Same(error, frames.World.ReadSparse<BuildError>(root).Exception);
        Assert.Equal(1UL, frames.World.ReadSparse<BuildError>(root).Frame);
        Assert.Empty(Hierarchy<ElementTree>.GetChildren(frames.World, root));
#if DEBUG
        Assert.Contains(messages.Values, message => message.Contains(error.Message, StringComparison.Ordinal));
#else
        Assert.DoesNotContain(messages.Values, message => message.Contains(error.Message, StringComparison.Ordinal));
#endif

        frames.World.Replace(source, new Content(3));
        frames.Frame();
        Assert.False(frames.World.HasSparse<BuildError>(root));
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal("3", frames.World.Read<Text>(child).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AncestorDisplaysTheChildErrorAndRestoresContentAfterTheChildRecovers(bool firstBuildFailure)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity(new Content(firstBuildFailure ? 2 : 1));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ErrorDisplay(source));
        frames.Frame();
        Entity child = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        if (!firstBuildFailure)
            frames.World.Replace(source, new Content(2));

        frames.Frame();
        frames.Frame();

        Entity fallback = Hierarchy<ElementTree>.GetChildren(frames.World, root)[1];
        Assert.Equal("fallback: content failed", frames.World.Read<Text>(fallback).Value);
        Assert.Equal(firstBuildFailure ? 1UL : 2UL, frames.World.ReadSparse<BuildError>(child).Frame);
        Assert.False(frames.World.HasSparse<BuildError>(root));

        frames.World.Replace(source, new Content(3));
        frames.Frame();
        frames.Frame();

        Entity content = Hierarchy<ElementTree>.GetChildren(frames.World, root)[1];
        Assert.Equal(child, Hierarchy<ElementTree>.GetChildren(frames.World, root)[0]);
        Assert.Equal("content", frames.World.Read<Text>(content).Value);
        Assert.False(frames.World.HasSparse<BuildError>(child));
        Assert.False(frames.World.IsAlive(fallback));
    }

    [Fact]
    public void FailedBuildRecordsItsExceptionAndFrameAndRecoversWhenItsDependencyChanges()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity(new Content(2));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ChangingContent(source, 1));

        frames.Frame();

        BuildError error = frames.World.ReadSparse<BuildError>(root);
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Equal("content failed", error.Exception.Message);
        Assert.Equal(1UL, error.Frame);
        Assert.Empty(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        frames.Frame();
        frames.Frame();
        Assert.Equal(error, frames.World.ReadSparse<BuildError>(root));

        frames.World.Replace(source, new Content(3));
        frames.Frame();

        Assert.False(frames.World.HasSparse<BuildError>(root));
        Assert.Equal(new[] { "first", "second", "third" },
            Hierarchy<ElementTree>.GetChildren(frames.World, root)
                .Select(child => frames.World.Read<Text>(child).Value).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedChildDeclarationsKeepThePreviousTreeAndTheSiblingBuildsWithinTheFrame(bool inline)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity(new Content(1));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ContentGroup(source, 1));
        frames.Frame();
        Entity[] branches = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, branches[0]).ToArray();
        Entity tip = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, children[1]));
        Entity changedTip = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, children[2]));
        int entityCount = frames.World.EntityCount;
        frames.World.Replace(source, new Content(2));
        if (inline)
            frames.World.Replace(root, new ContentGroup(source, 2));

        frames.Frame();

        Assert.Equal(branches, Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray());
        Assert.Equal(children, Hierarchy<ElementTree>.GetChildren(frames.World, branches[0]).ToArray());
        Assert.Equal(new[] { "first", "second", "third" },
            children.Select(child => frames.World.Read<Text>(child).Value).ToArray());
        Assert.Equal(tip, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, children[1])));
        Assert.Equal("second", frames.World.Read<Tip>(tip).Value);
        Assert.Equal(changedTip, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, children[2])));
        Assert.Equal("third", frames.World.Read<Tip>(changedTip).Value);
        Assert.Equal(entityCount, frames.World.EntityCount);
        Assert.Equal(2UL, frames.World.ReadSparse<BuildError>(branches[0]).Frame);
        Entity siblingText = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, branches[1]));
        Assert.Equal("2", frames.World.Read<Text>(siblingText).Value);
    }

    private readonly record struct ChangingCallerContent(Entity Source, bool InsideContent, Exception Error) : IView, IComponent
    {
        public BuildTask Build()
        {
            int value = Scope.Read<Content>(Source).Value;
            using (Scope.View(new Group()).Content())
            {
                if (value == 2)
                {
                    Scope.View(new Text("changed second"), 2);
                    Scope.View(new Text("added"), 3);
                    if (InsideContent)
                        throw Error;
                }
                else
                {
                    Scope.View(new Text("first"), 1);
                    Scope.View(new Text("second"), 2);
                }
            }
            if (value == 2)
                throw Error;
            return default;
        }
    }

    private readonly record struct ErrorDisplay(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity child = Scope.View(new ChangingContent(Source, 1), 1);
            if (Scope.Has<BuildError>(child))
            {
                string message = Scope.Read<BuildError>(child).Exception.Message;
                Scope.View(new Text($"fallback: {message}"), 2);
            }
            else
            {
                Scope.View(new Text("content"), 3);
            }
            return default;
        }
    }

    private readonly record struct AsyncContent(Entity Source, Exception Error) : IView, ISparseComponent
    {
        public async BuildTask Build()
        {
            await Task.CompletedTask;
            int value = Scope.Read<Content>(Source).Value;
            Scope.View(new Text(value.ToString()));
            if (value == 2)
                throw Error;
        }
    }

    private readonly record struct CallbackGroup(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            int value = Scope.Read<Content>(Source).Value;
            Scope.View(new CallbackText(1, value == 1 ? ReadInitialText : ReadChangedText));
            if (value == 2)
                throw new InvalidOperationException("callback group failed");
            return default;
        }

        private static string ReadInitialText() => "before";
        private static string ReadChangedText() => "after";
    }

    private readonly struct CallbackText(int value, Func<string> textCallback) : IView, ISparseComponent, IEquatable<CallbackText>
    {
        public int Value { get; } = value;
        public Func<string> TextCallback { get; } = textCallback;
        public bool Equals(CallbackText other) => Value == other.Value;
        public BuildTask Build() => default;
    }

    private sealed class ErrorMessages : TraceListener
    {
        internal ConcurrentQueue<string> Values { get; } = new();
        public override void Write(string? message)
        {
            if (message is not null)
                Values.Enqueue(message);
        }
        public override void WriteLine(string? message) => Write(message);
    }

    private readonly record struct ContentGroup(Entity Source, int Value) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new ChangingContent(Source, Value), 1);
            Scope.View(new ContentDisplay(Source, Value), 2);
            return default;
        }
    }

    private readonly record struct ChangingContent(Entity Source, int Value) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (Scope.Read<Content>(Source).Value == 2)
            {
                Scope.View(new Text("changed third"), 3);
                Scope.View(new Text("added"), 4);
                Scope.View(new Replacement("replaced second"), 2);
                throw new InvalidOperationException("content failed");
            }
            Scope.View(new Text("first"), 1);
            Scope.View(new Text("second"), 2);
            Scope.View(new Text("third"), 3);
            return default;
        }
    }

    private readonly record struct ContentDisplay(Entity Source, int Value) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new Text(Scope.Read<Content>(Source).Value.ToString()), 1);
            return default;
        }
    }

    private readonly record struct Text(string Value) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new Tip(Value));
            return default;
        }
    }

    private readonly record struct Tip(string Value) : IView, IComponent
    {
        public BuildTask Build() => default;
    }

    private readonly record struct Replacement(string Value) : IView, ISparseComponent
    {
        public BuildTask Build() => default;
    }

    private readonly record struct Content(int Value) : IComponent;
}
