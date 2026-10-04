using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

/// <summary>Public list and scrolling behavior shared with the NativeAOT executable.</summary>
internal static partial class ListChecks
{
    internal static void Run(bool rewritten)
    {
        using var world = new World();
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(200, 120) });
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new HitTestSystem());
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem());
        var label = new State<string>("Item 2");
        Entity root = Views.Views.Spawn(world, canvas, new ListExample(rewritten, label));
        systems.Update();
        systems.Update();
        Entity panel = Hierarchy<ElementTree>.GetChildren(world, root)[0];
        Entity list = Hierarchy<ElementTree>.GetChildren(world, panel)[0];
        Entity[] before = Hierarchy<ElementTree>.GetChildren(world, list).ToArray();
        Require(before.Length == 3 && world.Read<ScrollOffset>(list).Max.Y == 319936,
            "Ten thousand items create the two viewport rows and a buffer row, with the full content extent.");
        world.Replace(before[1], new LabelText("Editing item 1"));
        Pointer.Move(world, canvas, new Vector2(10, 10));
        Pointer.Wheel(world, canvas, new Vector2(0, 64));
        systems.Update();
        systems.Update();
        Entity[] after = Hierarchy<ElementTree>.GetChildren(world, list).ToArray();
        Require(world.Read<ListRange>(root).Value == new VisibleRange(1, 5) && after.Length == 4,
            "Scrolling updates the caller's visible range.");
        Require(after[0] == before[1] && after[1] == before[2] && !world.IsAlive(before[0]),
            "Keyed rows keep their identities while departing rows are released.");
        Require(world.Read<LabelText>(after[0]).Value == "Editing item 1" && world.Read<LabelText>(after[2]).Value == "Item 3",
            "Retained row data is preserved and new rows receive their content.");
        label.Value = "Updated item 2";
        systems.Update();
        Require(world.Read<LabelText>(after[1]).Value == "Updated item 2" && world.Read<LabelText>(after[0]).Value == "Editing item 1",
            "A row data change refreshes that row's display while its neighbor retains its data.");

        Entity scrollRoot = Views.Views.Spawn(world, canvas, new ScrollExample(rewritten));
        systems.Update();
        Entity scrollPanel = Hierarchy<ElementTree>.GetChildren(world, scrollRoot)[0];
        Entity scroll = Hierarchy<ElementTree>.GetChildren(world, scrollPanel)[0];
        Entity content = Hierarchy<ElementTree>.GetChildren(world, scroll)[0];
        Pointer.Move(world, canvas, new Vector2(130, 10));
        Pointer.Wheel(world, canvas, new Vector2(20, 30));
        systems.Update();
        Require(world.Read<ScrollOffset>(scroll).Max == new Vector2(80, 60) &&
            world.Read<ScrollOffset>(scroll).Value == new Vector2(20, 30),
            "The caller-content ScrollView exposes its measured scrolling range.");
        var rectangle = world.Read<LayoutRect>(content).Value;
        Require(rectangle.X == 100 && rectangle.Y == -30,
            "ScrollView content follows the layout-written offset.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private readonly record struct ListExample(bool Rewritten, State<string> Label) : IView, IComponent
    {
        public BuildTask Build()
        {
            using var panel = Absolute().Content();
            Entity list = Rewritten ? Scope.View(new Rewrites.VirtualList(32)) : Scope.View(new VirtualList(32));
            using var content = list.Size(100, 64).Content();
            VisibleRange range = Rewritten ? Rewrites.VirtualListFunctions.Visible(list, 10000) : VirtualListFunctions.Visible(list, 10000);
            Scope.Write<ListRange>(Scope.Entity) = new ListRange(range);
            foreach (int index in range)
            {
                using (Scope.Key((ulong)index))
                    Scope.View(new Row(index, index == 2 ? Label : null));
            }
            return default;
        }
    }

    private readonly record struct Row(int Index, State<string>? Label) : IView, IComponent, ILayout
    {
        public BuildTask Build()
        {
            Scope.Write<LabelText>(Scope.Entity) = new LabelText(Label?.Value ?? $"Item {Index}");
            return default;
        }

        public Size Measure(ref LayoutPass pass, in Constraints constraints) => constraints.Constrain(new Size(100, 32));
    }

    private readonly record struct ScrollExample(bool Rewritten) : IView, IComponent
    {
        public BuildTask Build()
        {
            using var panel = Absolute().Content();
            Entity scroll = Rewritten ? Scope.View(new Rewrites.ScrollView()) : Scope.View(new ScrollView());
            using (scroll.Size(60, 40).Position(120, 0).Content())
                Absolute().Size(140, 100);
            return default;
        }
    }

    private readonly record struct LabelText(string Value) : IComponent;
    private readonly record struct ListRange(VisibleRange Value) : IComponent;
}
