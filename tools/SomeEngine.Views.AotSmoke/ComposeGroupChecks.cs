using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views.Compose;
using SomeEngine.Views.Tests;
using static SomeEngine.Views.Compose.Compose;

namespace SomeEngine.Views.AotSmoke;

internal static partial class ComposeGroupChecks
{
    internal static void Run(HeadlessFrameDriver frames)
    {
        Entity root = Views.Spawn(frames.World, Entity.Null, new Screen_View(true));
        frames.Frame();
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Require(children.Length == 3, "Inline groups declare their content on the caller's tree.");
        Entity first = Hierarchy<ElementTree>.GetChildren(frames.World, children[0]).Single();
        Entity second = Hierarchy<ElementTree>.GetChildren(frames.World, children[1]).Single();
        Require(frames.World.ReadSparse<CounterText>(first).Value == 10 &&
            frames.World.ReadSparse<CounterText>(second).Value == 20,
            "Each inline call site starts with its own remembered value.");
        frames.World.ReadSparse<CounterText>(first).Change(11);
        frames.World.ReadSparse<CounterText>(second).Change(22);
        frames.World.ReadSparse<CounterChange>(root).Change(7);
        frames.World.Replace(second, new Selection(77));
        frames.Frame();

        frames.World.ReplaceSparse(root, new Screen_View(false));
        frames.Frame();
        Entity[] remaining = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Require(remaining.SequenceEqual([children[1], children[2]]) &&
            Hierarchy<ElementTree>.GetChildren(frames.World, remaining[0]).Single() == second &&
            frames.World.ReadSparse<CounterText>(second).Value == 22 &&
            frames.World.Read<Selection>(second).Value == 77,
            "A following inline call keeps its node and state through a conditional sibling change.");
        Require(frames.World.ReadSparse<Card>(remaining[1]).Title == "12",
            "A value-producing composable retains its state and supplies the expression result.");

        frames.World.ReplaceSparse(root, new Screen_View(true));
        frames.Frame();
        children = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        first = Hierarchy<ElementTree>.GetChildren(frames.World, children[0]).Single();
        Require(frames.World.ReadSparse<CounterText>(first).Value == 11 &&
            frames.World.ReadSparse<CounterText>(second).Value == 22 &&
            !frames.World.HasSparse<BuildError>(root),
            "Nested inline groups restore remembered values when caller content returns.");
        CheckContentFunctions(frames);
    }

    private static void CheckContentFunctions(HeadlessFrameDriver frames)
    {
        State<int>[] values = [new(10), new(20)];
        Entity root = Views.Spawn(frames.World, Entity.Null, new RowsScreen_View(values));
        frames.Frame();
        Entity[] content = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Require(content.Length == 2, "Value-producing groups declare each content call as a child view.");
        Entity[] cards = content.Select(child => Hierarchy<ElementTree>.GetChildren(frames.World, child).Single()).ToArray();
        values[0].Value = 11;
        frames.Frame();
        Require(frames.World.ReadSparse<Card>(cards[0]).Title == "11" &&
            frames.World.ReadSparse<Card>(cards[1]).Title == "20" &&
            frames.World.ReadSparse<GroupCount>(root).Value == 3,
            "Content functions in value-producing groups track their own state reads.");
    }

    [Composable]
    private static void RowsScreen(State<int>[] values) =>
        Scope.Write<GroupCount>(Scope.Entity) = new(1 + Rows(values,
            static value => Scope.View(new Card(value.Value.ToString()))));

    [Composable]
    private static int Rows(State<int>[] values, [Composable] Action<State<int>> row)
    {
        foreach (State<int> value in values)
            row(value);
        return values.Length;
    }

    [Composable]
    private static void Screen(bool includeFirst)
    {
        if (includeFirst) Header("x", 10); Header("y", 20);
        Scope.View(new Card((5 + RememberCounter()).ToString()));
    }

    [Composable, Inline]
    private static void Header(string text, int initial)
    {
        using (Scope.View(new Group()).Content())
        {
            var value = Remember(() => MutableStateOf(initial));
            Label(text, value);
        }
    }

    [Composable, Inline]
    private static void Label(string text, State<int> value) =>
        Scope.View(new CounterText(text, value.Value, next => value.Value = next));

    [Composable]
    private static int RememberCounter()
    {
        var value = Remember(static () => MutableStateOf(0));
        Scope.Write<CounterChange>(Scope.Entity) = new(next => value.Value = next);
        return value.Value;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

public readonly record struct CounterChange(Action<int> Change) : ISparseComponent;
public readonly record struct GroupCount(int Value) : ISparseComponent;

public readonly record struct CounterText(string Text, int Value, Action<int> Change) : IView, ISparseComponent
{
    public BuildTask Build()
    {
        if (!Scope.World.Has<Selection>(Scope.Entity))
        {
            Scope.World.Add(Scope.Entity, new Selection(0));
            Scope.World.AddTag<LocalState>(Scope.Entity);
        }
        return default;
    }
}
