using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views.Compose;
using SomeEngine.Views.Tests;
using static SomeEngine.Views.Compose.Compose;

namespace SomeEngine.Views.AotSmoke;

internal static partial class RememberChecks
{
    private static string s_decoration = " old";

    internal static void Run(HeadlessFrameDriver frames)
    {
        var refresh = MutableStateOf(0);
        var selectedTick = MutableStateOf(-1);
        Entity root = Views.Spawn(frames.World, Entity.Null, new Screen_View(refresh, selectedTick));
        frames.Frame();
        Entity toggle = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
        Require(frames.World.ReadSparse<Text>(toggle).Value == "False old", "Remember creates the initial lifted state.");
        s_decoration = " new";
        refresh.Value = 1;
        frames.Frame();
        Require(frames.World.ReadSparse<Text>(root).Value == "1" &&
            frames.World.ReadSparse<Text>(toggle).Value == "False old", "Equal values preserve child content across parent refreshes.");
        var button = frames.World.ReadSparse<Toggle_View>(toggle);
        Require(button.tooltip() == "tick 1", "Func parameters refresh with the stored view data.");
        button.change(true);
        frames.Frame();
        Require(selectedTick.Value == 1 && frames.World.ReadSparse<Text>(toggle).Value == "True new",
            "The latest callback updates remembered state and displays its new value.");

        Entity branches = Views.Spawn(frames.World, Entity.Null, new Branches_View(false));
        frames.Frame();
        var first = frames.World.ReadSparse<BranchValues>(branches);
        first.Change(23);
        first.ChangeTail(37);
        frames.Frame();
        frames.World.ReplaceSparse(branches, new Branches_View(true));
        frames.Frame();
        var second = frames.World.ReadSparse<BranchValues>(branches);
        Require(second.Value == 10 && second.Tail == 37, "Conditional Remember calls keep their positions separate.");
        second.Change(11);
        frames.World.ReplaceSparse(branches, new Branches_View(false));
        frames.Frame();
        Require(frames.World.ReadSparse<BranchValues>(branches).Value == 23, "The first Remember branch retains its state.");
        frames.World.ReplaceSparse(branches, new Branches_View(true));
        frames.Frame();
        Require(frames.World.ReadSparse<BranchValues>(branches).Value == 11, "The second Remember branch retains its state.");

        var shared = MutableStateOf(3);
        s_decoration = " old";
        Entity readers = Views.Spawn(frames.World, Entity.Null, new Readers_View(shared));
        frames.Frame();
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, readers).ToArray();
        s_decoration = " new";
        shared.Value = 7;
        frames.Frame();
        Require(frames.World.ReadSparse<Text>(children[0]).Value == "7 new" &&
            frames.World.ReadSparse<Text>(readers).Value == "root old" &&
            frames.World.ReadSparse<Text>(children[1]).Value == "controls old", "State parameters refresh the views that read their values.");

        var score = new Score(3);
        Action content = new Action(Draw);
        var parameters = new Compare_View(new(5), score, content, static () => { });
        var equal = new Compare_View(new(5), score, content, static () => { });
        Require(parameters.Equals(equal) && new HashSet<Compare_View> { parameters }.Contains(equal),
            "Records use value equality and callbacks preserve equal parameter values and hashes.");
        Require(!parameters.Equals(equal with { score = new Score(3) }) &&
            !parameters.Equals(equal with { content = new Action(Draw) }), "Classes and composable content use reference equality.");
    }

    [Composable]
    private static void Screen(State<int> refresh, State<int> selectedTick)
    {
        var paused = Remember(static () => MutableStateOf(false));
        int tick = refresh.Value;
        Scope.Write<Text>(Scope.Entity) = new(tick.ToString());
        Toggle(paused.Value, next =>
        {
            paused.Value = next;
            selectedTick.Value = tick;
        }, () => "tick " + tick);
    }

    [Composable]
    private static void Toggle(bool value, Action<bool> change, Func<string> tooltip) =>
        Scope.Write<Text>(Scope.Entity) = new(value + s_decoration);

    [Composable]
    private static void Branches(bool alternate)
    {
        State<int> value;
        if (alternate)
            value = Remember(static () => MutableStateOf(10));
        else
            value = Remember(static () => MutableStateOf(20));
        var tail = Remember(static () => MutableStateOf(30));
        Scope.Write<BranchValues>(Scope.Entity) = new(value.Value, tail.Value,
            next => value.Value = next, next => tail.Value = next);
    }

    [Composable]
    private static void Readers(State<int> state)
    {
        Scope.Write<Text>(Scope.Entity) = new("root" + s_decoration);
        Reader(state);
        Controls(state);
    }

    [Composable]
    private static void Reader(State<int> state) => Scope.Write<Text>(Scope.Entity) = new(state.Value + s_decoration);

    [Composable]
    private static void Controls(State<int> state) => Scope.Write<Text>(Scope.Entity) = new("controls" + s_decoration);

    [Composable]
    private static void Compare(ScoreRecord value, Score score, [Composable] Action content, Action changed) { }

    private static void Draw() { }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private readonly record struct Text(string Value) : ISparseComponent;
    private readonly record struct BranchValues(int Value, int Tail, Action<int> Change, Action<int> ChangeTail) : ISparseComponent;
    private sealed record ScoreRecord(int Value);
    private sealed class Score(int value)
    {
        public override bool Equals(object? other) => other is Score score && score.Value == Value;
        public override int GetHashCode() => Value;
        private int Value { get; } = value;
    }
}
