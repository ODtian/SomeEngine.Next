using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views.Compose;
using SomeEngine.Views.Solid;
using SomeEngine.Views.Tests;
using static SomeEngine.Views.Solid.Solid;

namespace SomeEngine.Views.Solid.Tests;

public static partial class FrontendChecks
{
    public static void CheckInsert()
    {
        using var frames = new HeadlessFrameDriver();
        var count = new State<int>(3);
        Entity root = Views.Spawn(frames.World, Entity.Null, new SolidContent(() => Insert(() => Number(count.Value))));
        frames.Frame();
        Entity insertion = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
        Entity component = Hierarchy<ElementTree>.GetChildren(frames.World, insertion).Single();
        Entity label = Hierarchy<ElementTree>.GetChildren(frames.World, component).Single();
        Require(frames.World.ReadSparse<NumberLabel>(label).Value == 3, "Insert supplies the initial Compose parameter.");

        count.Value = 7;
        frames.Frame();

        Require(Hierarchy<ElementTree>.GetChildren(frames.World, root).Single() == insertion &&
            Hierarchy<ElementTree>.GetChildren(frames.World, insertion).Single() == component &&
            Hierarchy<ElementTree>.GetChildren(frames.World, component).Single() == label &&
            frames.World.ReadSparse<Number_View>(component).value == 7 &&
            frames.World.ReadSparse<NumberLabel>(label).Value == 7,
            "Insert delivers changed ordinary parameters while preserving the declared nodes.");
    }

    public static void CheckComponent()
    {
        using var frames = new HeadlessFrameDriver();
        var factor = new State<int>(2);
        Entity root = Views.Spawn(frames.World, Entity.Null, new SolidHost_View(3, factor));
        frames.Frame();
        Entity counter = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
        State<int> count = frames.World.ReadSparse<CounterState>(counter).Count;
        Require(frames.World.ReadSparse<CounterResult>(counter).Value == 6, "The exported Solid component builds its initial computation.");

        count.Value = 5;
        frames.Frame();
        Require(frames.World.ReadSparse<CounterResult>(counter).Value == 10, "The component responds to its local state.");

        frames.World.ReplaceSparse(root, new SolidHost_View(9, factor));
        frames.Frame();
        Require(Hierarchy<ElementTree>.GetChildren(frames.World, root).Single() == counter &&
            frames.World.ReadSparse<CounterState>(counter).Count.Value == 5 &&
            frames.World.ReadSparse<CounterResult>(counter).Value == 10,
            "A Compose refresh retains the Solid component and its edited local state.");

        count.Value = 6;
        factor.Value = 3;
        frames.Frame();
        Require(frames.World.ReadSparse<CounterResult>(counter).Value == 18,
            "The retained Memo and Effect continue responding to local and shared state.");
    }

    public static void CheckInsertedBranches()
    {
        using var frames = new HeadlessFrameDriver();
        var visible = new State<bool>(true);
        var count = new State<int>(3);
        Entity root = Views.Spawn(frames.World, Entity.Null, new SolidContent(() => Insert(() =>
        {
            if (visible.Value)
                Number(count.Value);
            Scope.View(new NumberLabel(99), 1);
        })));
        frames.Frame();
        Entity insertion = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
        Entity[] first = Hierarchy<ElementTree>.GetChildren(frames.World, insertion).ToArray();
        Entity label = Hierarchy<ElementTree>.GetChildren(frames.World, first[0]).Single();

        visible.Value = false;
        frames.Frame();
        Require(Hierarchy<ElementTree>.GetChildren(frames.World, insertion).Single() == first[1] &&
            !frames.World.IsAlive(first[0]) && !frames.World.IsAlive(label),
            "Insert releases omitted content and retains the following node by its key.");

        count.Value = 7;
        visible.Value = true;
        frames.Frame();
        Entity[] restored = Hierarchy<ElementTree>.GetChildren(frames.World, insertion).ToArray();
        Entity nextLabel = Hierarchy<ElementTree>.GetChildren(frames.World, restored[0]).Single();
        Require(restored[0] != first[0] && restored[1] == first[1] &&
            frames.World.ReadSparse<NumberLabel>(nextLabel).Value == 7,
            "Insert recreates returning branches with current values and preserves the following node.");
    }

    public static void CheckBuild()
    {
        using var frames = new HeadlessFrameDriver();
        var factor = new State<int>(2);
        Entity root = Views.Spawn(frames.World, Entity.Null, new CounterHost(3, factor));
        frames.Frame();
        Entity counter = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
        State<int> count = frames.World.ReadSparse<CounterState>(counter).Count;
        count.Value = 5;
        frames.Frame();

        frames.World.ReplaceSparse(root, new CounterHost(9, factor));
        frames.Frame();
        factor.Value = 3;
        frames.Frame();
        Require(Hierarchy<ElementTree>.GetChildren(frames.World, root).Single() == counter &&
            frames.World.ReadSparse<CounterState>(counter).Count.Value == 5 &&
            frames.World.ReadSparse<CounterResult>(counter).Value == 15,
            "An ordinary view Build preserves the exported component and its responsive state.");
    }

    public static void CheckInsertionRecovery()
    {
        using var frames = new HeadlessFrameDriver();
        var count = new State<int>(3);
        var fail = new State<bool>();
        var error = new InvalidOperationException("inserted content failed");
        Entity root = Views.Spawn(frames.World, Entity.Null, new SolidContent(() => Insert(() =>
        {
            Scope.View(new NumberLabel(count.Value), 1);
            if (fail.Value)
                throw error;
        })));
        frames.Frame();
        Entity insertion = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
        Entity label = Hierarchy<ElementTree>.GetChildren(frames.World, insertion).Single();

        count.Value = 7;
        fail.Value = true;
        frames.Frame();
        Require(frames.World.ReadSparse<BuildError>(insertion).Exception == error &&
            Hierarchy<ElementTree>.GetChildren(frames.World, insertion).Single() == label &&
            frames.World.ReadSparse<NumberLabel>(label).Value == 3,
            "Failed Insert content records its error and preserves the previous child data.");

        fail.Value = false;
        frames.Frame();
        Require(!frames.World.HasSparse<BuildError>(insertion) &&
            Hierarchy<ElementTree>.GetChildren(frames.World, insertion).Single() == label &&
            frames.World.ReadSparse<NumberLabel>(label).Value == 7,
            "Insert recovers on the same node when its dependency changes.");
    }

    [Composable]
    private static void SolidHost(int seed, State<int> factor) => CounterContent(seed, factor);

    [Component]
    private static void CounterContent(int seed, State<int> factor) => Counter(seed, factor);

    [Component]
    private static void Counter(int seed, State<int> factor)
    {
        var count = new State<int>(seed);
        Scope.Write<CounterState>(Scope.Entity) = new(count);
        Func<int> total = Memo(() => count.Value * factor.Value);
        Effect(() => Scope.Write<CounterResult>(Scope.Entity) = new(total()));
    }

    private readonly record struct CounterState(State<int> Count) : ISparseComponent;
    private readonly record struct CounterResult(int Value) : ISparseComponent;

    private readonly record struct CounterHost(int Seed, State<int> Factor) : IView, ISparseComponent
    {
        public BuildTask Build()
        {
            Counter(Seed, Factor);
            return default;
        }
    }

    [Composable]
    private static void Number(int value) => Scope.View(new NumberLabel(value));

    private readonly record struct SolidContent(Action Content) : IView, ISparseComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            Content();
            return default;
        }
    }

    private readonly record struct NumberLabel(int Value) : IView, ISparseComponent
    {
        public BuildTask Build() => default;
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }
}
