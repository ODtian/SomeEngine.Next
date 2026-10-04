using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.Elements;
using static SomeEngine.Views.Solid.Solid;
using SolidFrames = SomeEngine.Views.Tests.HeadlessFrameDriver;

namespace SomeEngine.Views.Solid.Tests;

/// <summary>Public list behavior shared by managed and NativeAOT consumers.</summary>
public static partial class ListChecks
{
    public static void Run()
    {
        CheckForRows();
        CheckIndexRows();
        CheckQueryRows(false);
        CheckQueryRows(true);
    }

    private static void CheckForRows()
    {
        using var frames = new SolidFrames();
        var items = new State<IReadOnlyList<int>>([1, 2, 3]);
        var sample = new State<string>("first");
        Func<int> firstIndex = null!;
        Entity[] outputs = [frames.World.CreateEntity(new Display("")), frames.World.CreateEntity(new Display("")),
            frames.World.CreateEntity(new Display(""))];
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
            For(() => items.Value, (item, index) =>
            {
                if (item == 1)
                    firstIndex = index;
                Text($"{item}:{sample.Value}");
                Text(index);
                Effect(() => Scope.Write<Display>(outputs[item - 1]).Value = sample.Value);
            })));
        frames.Frame();
        Entity list = GetList(frames.World, root);
        Entity[] rows = GetRows(frames.World, list);
        Require(firstIndex() == 0, "For index getters can be read outside a build.");

        sample.Value = "second";
        items.Value = [3, 1, 2];
        frames.Frame();
        Entity[] reordered = GetRows(frames.World, list);
        Require(reordered.SequenceEqual(new[] { rows[2], rows[0], rows[1] }), "For moves existing item rows.");
        Require(ReadText(frames.World, reordered[0], 0) == "3:first" &&
            ReadText(frames.World, reordered[1], 0) == "1:first", "For retains initial row content.");
        Require(ReadText(frames.World, reordered[0], 1) == "0" &&
            ReadText(frames.World, reordered[1], 1) == "1", "For index getters follow the new order.");
        Require(firstIndex() == 1, "For index getters expose the moved row's position to callbacks.");

        Entity removedText = Hierarchy<ElementTree>.GetChildren(frames.World, rows[1])[0];
        items.Value = [3, 1];
        sample.Value = "third";
        frames.Frame();
        Require(!frames.World.IsAlive(rows[1]) && !frames.World.IsAlive(removedText), "Removing an item releases its row and text.");
        Require(frames.World.Read<Display>(outputs[0]).Value == "third" &&
            frames.World.Read<Display>(outputs[1]).Value == "second", "Remaining row effects update their data after a row is released.");
    }

    private static void CheckIndexRows()
    {
        using var frames = new SolidFrames();
        var items = new State<IReadOnlyList<string>>(["first", "second"]);
        Func<string> firstItem = null!;
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
            Index(() => items.Value, (item, index) =>
            {
                if (index == 0)
                    firstItem = item;
                Text(item());
                Text(item);
            })));
        frames.Frame();
        Entity list = GetList(frames.World, root);
        Entity[] rows = GetRows(frames.World, list);
        Require(firstItem() == "first", "Index item getters can be read outside a build.");
        Entity firstText = Hierarchy<ElementTree>.GetChildren(frames.World, rows[0])[1];
        firstText.SetText(frames.World, "edited first");

        items.Value = ["first", "changed"];
        frames.Frame();
        Require(GetRows(frames.World, list).SequenceEqual(rows), "Index retains each position's row.");
        Require(ReadText(frames.World, rows[0], 1) == "edited first" &&
            ReadText(frames.World, rows[1], 1) == "changed", "Only the changed position refreshes its item binding.");
        Require(ReadText(frames.World, rows[1], 0) == "second", "Index keeps initial content alongside item bindings.");

        items.Value = ["changed", "first"];
        frames.Frame();
        Require(GetRows(frames.World, list).SequenceEqual(rows) && ReadText(frames.World, rows[0], 1) == "changed" &&
            ReadText(frames.World, rows[1], 1) == "first", "Reordered items update getters on the same positions.");
        Require(firstItem() == "changed", "Index item getters expose the current position's value to callbacks.");
        items.Value = ["changed"];
        frames.Frame();
        Require(!frames.World.IsAlive(rows[1]) && GetRows(frames.World, list).Single() == rows[0],
            "A shortened list releases its trailing positions.");
    }

    private static void CheckQueryRows(bool byPosition)
    {
        using var frames = new SolidFrames();
        QueryHandle query = frames.World.Query(frames.World.QueryDefinition().All<Number>());
        Entity first = frames.World.CreateEntity(new Number(10));
        Entity second = frames.World.CreateEntity(new Number(20));
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
        {
            if (byPosition)
                Index(() => Scope.Each(query), (item, index) => Text(() => Scope.Read<Number>(item()).Value));
            else
                For(() => Scope.Each(query), (item, index) => Text(() => Scope.Read<Number>(item).Value));
        }));
        frames.Frame();
        Entity list = GetList(frames.World, root);
        Entity[] rows = GetRows(frames.World, list);
        Hierarchy<ElementTree>.GetChildren(frames.World, rows[1])[0].SetText(frames.World, "edited second");
        frames.World.Replace(first, new Number(15));
        frames.Frame();
        Require(ReadText(frames.World, rows[0], 0) == "15" && ReadText(frames.World, rows[1], 0) == "edited second",
            "Each row's data reads update its own text.");

        Entity third = frames.World.CreateEntity(new Number(30));
        frames.Frame();
        Entity[] expanded = GetRows(frames.World, list);
        frames.World.DestroyEntity(first);
        frames.Frame();
        Entity[] remaining = GetRows(frames.World, list);
        Require(remaining.SequenceEqual(byPosition ? rows : new[] { expanded[2], rows[1] }),
            "Each supplies entities while list keys determine the retained rows.");
        Require(ReadText(frames.World, remaining[0], 0) == "30" && ReadText(frames.World, remaining[1], 0) == "edited second",
            "Membership changes preserve independent row data.");
        Require(!frames.World.IsAlive(byPosition ? expanded[2] : rows[0]), "Membership removal releases the omitted row.");
    }

    private static Entity GetList(World world, Entity root) => Hierarchy<ElementTree>.GetChildren(world, root).Single();
    private static Entity[] GetRows(World world, Entity list) => Hierarchy<ElementTree>.GetChildren(world, list).ToArray();
    private static string ReadText(World world, Entity row, int index) =>
        world.Read<Elements.Text>(Hierarchy<ElementTree>.GetChildren(world, row)[index]).Value;

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private record struct Display(string Value) : IComponent;
    private readonly record struct Number(int Value) : IComponent;
    private readonly record struct OnceView(Action Content) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;
        public BuildTask Build()
        {
            Content();
            return default;
        }
    }
}
