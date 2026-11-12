using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views.Tests;

public sealed partial class HasDependencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComponentPresenceUpdatesTheDisplayWhileValueWritesRetainIt(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create();
        Entity root = Views.Spawn(frames.World, Entity.Null, new PresenceDisplay(source, sparse));

        frames.Frame();
        Assert.Equal("absent", frames.World.ReadComponentData<DisplayText>(root).Value);

        if (sparse)
            frames.World.SetSparse(source, new SparseValue(3));
        else
            frames.World.Add(source, new Value(3));
        frames.Frame();
        Assert.Equal("3", frames.World.ReadComponentData<DisplayText>(root).Value);

        if (sparse)
            frames.World.SetSparse(source, new SparseValue(7));
        else
        {
            frames.World.Set(source, new Value(7));
            frames.World.Disable<Value>(source);
        }
        frames.World.Set(source, default(Selected));
        frames.Frame();
        Assert.Equal("3", frames.World.ReadComponentData<DisplayText>(root).Value);

        if (sparse)
            frames.World.RemoveSparse<SparseValue>(source);
        else
            frames.World.Remove<Value>(source);
        frames.Frame();
        Assert.Equal("absent", frames.World.ReadComponentData<DisplayText>(root).Value);

        if (sparse)
            frames.World.SetSparse(source, new SparseValue(11));
        else
            frames.World.Add(source, new Value(11));
        frames.Frame();
        Assert.Equal("11", frames.World.ReadComponentData<DisplayText>(root).Value);
    }

    [Fact]
    public void PresenceAndValueReadsOnOneComponentBothSupplyTheDisplay()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create(new Value(3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ValueDisplay(source));

        frames.Frame();
        Assert.Equal("3", frames.World.ReadComponentData<DisplayText>(root).Value);
        frames.World.Set(source, new Value(7));
        frames.Frame();
        Assert.Equal("7", frames.World.ReadComponentData<DisplayText>(root).Value);
        frames.World.Remove<Value>(source);
        frames.Frame();
        Assert.Equal("absent", frames.World.ReadComponentData<DisplayText>(root).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OncePresenceReadsRetainTheirFirstDisplay(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create();
        Entity root = Views.Spawn(frames.World, Entity.Null, new OncePresenceDisplay(source, sparse));
        frames.Frame();

        if (sparse)
            frames.World.SetSparse(source, new SparseValue(3));
        else
            frames.World.Add(source, new Value(3));
        frames.Frame();
        Assert.Equal("absent", frames.World.ReadComponentData<DisplayText>(root).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DestroyedSourceDisplaysAbsenceAfterEntityStorageIsReused(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create();
        if (sparse)
            frames.World.SetSparse(source, new SparseValue(3));
        else
            frames.World.Add(source, new Value(3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new PresenceDisplay(source, sparse));
        frames.Frame();
        Assert.Equal("3", frames.World.ReadComponentData<DisplayText>(root).Value);

        frames.World.Destroy(source);
        Entity replacement = frames.World.Create();
        if (sparse)
            frames.World.SetSparse(replacement, new SparseValue(7));
        else
            frames.World.Add(replacement, new Value(7));
        frames.Frame();
        Assert.Equal("absent", frames.World.ReadComponentData<DisplayText>(root).Value);
    }

#if DEBUG
    [Fact]
    public void HasOutsideBuildReportsTheRequiredScope()
    {
        Assert.Throws<InvalidOperationException>(() => Scope.Has<Value>(Entity.Null));
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
                ? (Sparse ? Scope.World.ReadComponentData<SparseValue>(Source).Number : Scope.World.ReadComponentData<Value>(Source).Number).ToString()
                : "absent";
            WriteDisplay(text);
            return default;
        }
    }

    private static void WriteDisplay(string text)
    {
        if (Scope.World.Has<DisplayText>(Scope.Entity))
            Scope.World.Set(Scope.Entity, new DisplayText(text));
        else
            Scope.World.Add(Scope.Entity, new DisplayText(text));
    }

    private readonly record struct Value(int Number) : ITrackedComponent, IEnableableComponent;
    private readonly record struct SparseValue(int Number) : ISparseComponent, ITrackedComponent;
    private readonly record struct DisplayText(string Value) : ITrackedComponent;
    private readonly struct Selected : ITag;
}
