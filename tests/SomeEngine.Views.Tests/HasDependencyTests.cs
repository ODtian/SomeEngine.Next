using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views.Tests;

public sealed class HasDependencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComponentPresenceUpdatesTheDisplayWhileValueWritesRetainIt(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity();
        Entity root = Views.Spawn(frames.World, Entity.Null, new PresenceDisplay(source, sparse));

        frames.Frame();
        Assert.Equal("absent", frames.World.Read<DisplayText>(root).Value);

        if (sparse)
            frames.World.AddSparse(source, new SparseValue(3));
        else
            frames.World.Add(source, new Value(3));
        frames.Frame();
        Assert.Equal("3", frames.World.Read<DisplayText>(root).Value);

        if (sparse)
            frames.World.ReplaceSparse(source, new SparseValue(7));
        else
        {
            frames.World.Replace(source, new Value(7));
            frames.World.Disable<Value>(source);
        }
        frames.World.AddTag<Selected>(source);
        frames.Frame();
        Assert.Equal("3", frames.World.Read<DisplayText>(root).Value);

        if (sparse)
            frames.World.RemoveSparse<SparseValue>(source);
        else
            frames.World.Remove<Value>(source);
        frames.Frame();
        Assert.Equal("absent", frames.World.Read<DisplayText>(root).Value);

        if (sparse)
            frames.World.AddSparse(source, new SparseValue(11));
        else
            frames.World.Add(source, new Value(11));
        frames.Frame();
        Assert.Equal("11", frames.World.Read<DisplayText>(root).Value);
    }

    [Fact]
    public void PresenceAndValueReadsOnOneComponentBothSupplyTheDisplay()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity(new Value(3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ValueDisplay(source));

        frames.Frame();
        Assert.Equal("3", frames.World.Read<DisplayText>(root).Value);
        frames.World.Replace(source, new Value(7));
        frames.Frame();
        Assert.Equal("7", frames.World.Read<DisplayText>(root).Value);
        frames.World.Remove<Value>(source);
        frames.Frame();
        Assert.Equal("absent", frames.World.Read<DisplayText>(root).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OncePresenceReadsRetainTheirFirstDisplay(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity();
        Entity root = Views.Spawn(frames.World, Entity.Null, new OncePresenceDisplay(source, sparse));
        frames.Frame();

        if (sparse)
            frames.World.AddSparse(source, new SparseValue(3));
        else
            frames.World.Add(source, new Value(3));
        frames.Frame();
        Assert.Equal("absent", frames.World.Read<DisplayText>(root).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DestroyedSourceDisplaysAbsenceAfterEntityStorageIsReused(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity();
        if (sparse)
            frames.World.AddSparse(source, new SparseValue(3));
        else
            frames.World.Add(source, new Value(3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new PresenceDisplay(source, sparse));
        frames.Frame();
        Assert.Equal("3", frames.World.Read<DisplayText>(root).Value);

        frames.World.DestroyEntity(source);
        Entity replacement = frames.World.CreateEntity();
        if (sparse)
            frames.World.AddSparse(replacement, new SparseValue(7));
        else
            frames.World.Add(replacement, new Value(7));
        frames.Frame();
        Assert.Equal("absent", frames.World.Read<DisplayText>(root).Value);
    }

#if DEBUG
    [Fact]
    public void HasOutsideBuildReportsTheRequiredScope()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Scope.Has<Value>(Entity.Null));
        Assert.Contains("active view build", error.Message, StringComparison.OrdinalIgnoreCase);
    }
#endif

    private readonly record struct OncePresenceDisplay(Entity Source, bool Sparse) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;
        public BuildTask Build() => new PresenceDisplay(Source, Sparse).Build();
    }

    private readonly record struct ValueDisplay(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            WriteDisplay(Scope.Has<Value>(Source) ? Scope.Read<Value>(Source).Number.ToString() : "absent");
            return default;
        }
    }

    private readonly record struct PresenceDisplay(Entity Source, bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            bool present = Sparse ? Scope.Has<SparseValue>(Source) : Scope.Has<Value>(Source);
            string text = present
                ? (Sparse ? Scope.World.ReadSparse<SparseValue>(Source).Number : Scope.World.Read<Value>(Source).Number).ToString()
                : "absent";
            WriteDisplay(text);
            return default;
        }
    }

    private static void WriteDisplay(string text)
    {
        if (Scope.World.Has<DisplayText>(Scope.Entity))
            Scope.World.Replace(Scope.Entity, new DisplayText(text));
        else
            Scope.World.Add(Scope.Entity, new DisplayText(text));
    }

    private readonly record struct Value(int Number) : IComponent, IEnableableComponent;
    private readonly record struct SparseValue(int Number) : ISparseComponent;
    private readonly record struct DisplayText(string Value) : IComponent;
    private readonly struct Selected : ITag;
}
