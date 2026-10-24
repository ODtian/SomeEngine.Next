using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views.SwiftUI;

namespace SomeEngine.Views.Tests;

public sealed partial class SwiftUIStateTests
{
    [Fact]
    public void BindingCopiesReadAndWriteTheSameState()
    {
        var state = new State<int>(3);
        var binding = new Binding<int>(state);
        Binding<int> copy = binding;

        copy.Value = 7;

        Assert.Equal(7, state.Value);
        Assert.Equal(7, binding.Value);
        state.Value = 11;
        Assert.Equal(11, copy.Value);
    }

    [Fact]
    public void StatePropertiesKeepTheirValuesAcrossRerunsAndParentParameterChanges()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new CounterGroup("first"));
        frames.Frame();
        Entity counter = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal("first:0", frames.World.Read<CounterText>(counter).Value);

        frames.World.Replace(counter, new IncrementRequest(true));
        frames.Frame();
        Assert.Equal("first:1", frames.World.Read<CounterText>(counter).Value);
        frames.Frame();
        Assert.Equal("first:1", frames.World.Read<CounterText>(counter).Value);

        frames.World.Replace(root, new CounterGroup("second"));
        frames.Frame();

        Assert.Equal("second:1", frames.World.Read<CounterText>(
            Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root))).Value);
    }

    [Fact]
    public void AChildBindingStartsWithThePropertyInitialValueAndChangesTheParentDisplay()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new EditableCounter("first", 3));
        frames.Frame();
        Entity editor = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal("first:3", frames.World.Read<CounterText>(root).Value);
        Assert.Equal("editor:3", frames.World.Read<CounterText>(editor).Value);

        frames.World.Replace(editor, new EditRequest(true, 7));
        frames.Frame();

        Assert.Equal("first:7", frames.World.Read<CounterText>(root).Value);
        Assert.Equal("editor:7", frames.World.Read<CounterText>(editor).Value);
        frames.World.ReplaceSparse(root, new EditableCounter("second", 99));
        frames.Frame();
        Assert.Equal("second:7", frames.World.Read<CounterText>(root).Value);
        Assert.Equal("editor:7", frames.World.Read<CounterText>(
            Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root))).Value);
    }

    [Fact]
    public void BranchCallsKeepSeparateStateForEachViewsLifetime()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new BranchGroup("left", false));
        frames.Frame();
        Entity left = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        frames.World.Replace(left, new IncrementRequest(true));
        frames.Frame();
        frames.World.Replace(root, new BranchGroup("left updated", false));
        frames.Frame();
        Assert.Equal("left updated:1", frames.World.Read<CounterText>(left).Value);

        frames.World.Replace(root, new BranchGroup("right", true));
        frames.Frame();
        Entity right = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal("right:0", frames.World.Read<CounterText>(right).Value);
        frames.World.Replace(right, new IncrementRequest(true));
        frames.Frame();
        frames.World.Replace(right, new IncrementRequest(true));
        frames.Frame();
        frames.World.Replace(root, new BranchGroup("right updated", true));
        frames.Frame();
        Assert.Equal("right updated:2", frames.World.Read<CounterText>(right).Value);

        frames.World.Replace(root, new BranchGroup("left recreated", false));
        frames.Frame();
        Assert.Equal("left recreated:0", frames.World.Read<CounterText>(
            Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root))).Value);
    }

    [Fact]
    public void GenericReadonlyViewsKeepSeparateInitializedProperties()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ValuePair<string>("ready", "first"));
        frames.Frame();
        Assert.Equal("first:ready:3", frames.World.Read<CounterText>(root).Value);
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal("ready", frames.World.Read<CounterText>(child).Value);

        frames.World.ReplaceSparse(root, new ValueEdit<string>(true, "changed"));
        frames.Frame();
        Assert.Equal("first:changed:4", frames.World.Read<CounterText>(root).Value);
        Assert.Equal("changed", frames.World.Read<CounterText>(child).Value);
        frames.World.ReplaceSparse(root, new ValuePair<string>("replacement", "second"));
        frames.Frame();
        Assert.Equal("second:changed:4", frames.World.Read<CounterText>(root).Value);
    }

#if DEBUG
    [Fact]
    public void StatePropertiesRequireTheirViewsActiveBuild()
    {
        var counter = new Counter("outside");
        var error = Assert.Throws<InvalidOperationException>(() => counter.Count);

        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ForeignCounterRead());
        frames.Frame();
        error = Assert.IsType<InvalidOperationException>(frames.World.ReadSparse<BuildError>(root).Exception);
    }

    [Fact]
    public void BindingRequiresAStateBeforeReadingAndWriting()
    {
        Assert.Throws<InvalidOperationException>(() => new Binding<int>(null!));
        Binding<int> binding = default;
        Assert.Throws<InvalidOperationException>(() => binding.Value);
        Assert.Throws<InvalidOperationException>(() => binding.Value = 7);
    }

    private readonly record struct ForeignCounterRead : IView, ISparseComponent
    {
        public BuildTask Build()
        {
            _ = new Counter("foreign").Count;
            return default;
        }
    }
#endif

    private readonly record struct CounterGroup(string Label) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new Counter(Label));
            return default;
        }
    }

    private readonly record struct BranchGroup(string Label, bool Alternate) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (Alternate)
                Scope.View(new Counter(Label));
            else
                Scope.View(new Counter(Label));
            return default;
        }
    }

    private partial record struct Counter(string Label) : IView, ISparseComponent
    {
        [State] public partial int Count { get; set; }

        public BuildTask Build()
        {
            ref IncrementRequest request = ref Scope.ReadWrite<IncrementRequest>(Scope.Entity);
            if (request.Increase)
            {
                Count++;
                request = default;
            }
            Scope.Write<CounterText>(Scope.Entity) = new CounterText($"{Label}:{Count}");
            return default;
        }
    }

    private partial record struct EditableCounter(string Label, int Initial) : IView, ISparseComponent
    {
        [State] private partial int Count { get; set; } = Initial;

        public BuildTask Build()
        {
            Scope.View(new CounterEditor(CountBinding));
            Scope.Write<CounterText>(Scope.Entity) = new CounterText($"{Label}:{Count}");
            return default;
        }
    }

    private readonly record struct CounterEditor(Binding<int> Count) : IView, ISparseComponent
    {
        public BuildTask Build()
        {
            ref EditRequest request = ref Scope.ReadWrite<EditRequest>(Scope.Entity);
            Binding<int> count = Count;
            if (request.Set)
            {
                count.Value = request.Value;
                request = default;
            }
            Scope.Write<CounterText>(Scope.Entity) = new CounterText($"editor:{count.Value}");
            return default;
        }
    }

    private readonly partial record struct ValuePair<T>(T Initial, string Label) : IView, ISparseComponent
        where T : notnull
    {
        [State] private partial T Value { get; set; } = Initial;
        [State] private partial int Count { get; set; } = 3;

        public BuildTask Build()
        {
            ref ValueEdit<T> edit = ref Scope.ReadWrite<ValueEdit<T>>(Scope.Entity);
            if (edit.Set)
            {
                Value = edit.Value;
                Count++;
                edit = default;
            }
            Scope.View(new ValueDisplay<T>(ValueBinding));
            Scope.Write<CounterText>(Scope.Entity) = new CounterText($"{Label}:{Value}:{Count}");
            return default;
        }
    }

    private readonly record struct ValueDisplay<T>(Binding<T> Value) : IView, ISparseComponent where T : notnull
    {
        public BuildTask Build()
        {
            Scope.Write<CounterText>(Scope.Entity) = new CounterText(Value.Value.ToString()!);
            return default;
        }
    }

    private readonly record struct IncrementRequest(bool Increase) : IComponent;
    private readonly record struct EditRequest(bool Set, int Value) : IComponent;
    private readonly record struct ValueEdit<T>(bool Set, T Value) : ISparseComponent;
    private readonly record struct CounterText(string Value) : IComponent;
}
