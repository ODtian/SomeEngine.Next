using static SomeEngine.Layout.FlexFunctions;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;

namespace SomeEngine.Views.AotSmoke;

/// <summary>Flex behavior exercised through the same public API in unit tests and NativeAOT.</summary>
public static partial class FlexChecks
{
    public static void Run()
    {
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem());
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(210, 200) });
        Entity root = Views.Spawn(world, canvas, new WrappedItems());
        systems.Update();
        Entity flex = Child(world, root);
        var children = Hierarchy<ElementTree>.GetChildren(world, flex);
        Require(world.Read<LayoutRect>(children[0]).Value == new Rect(0, 0, 100, 40), "The first wrapped line keeps its basis.");
        Require(world.Read<LayoutRect>(children[1]).Value == new Rect(110, 0, 100, 40), "Gap separates items within a line.");
        Require(world.Read<LayoutRect>(children[2]).Value == new Rect(0, 50, 210, 40), "Each wrapped line distributes its remaining space.");

        world.Replace(canvas, new Canvas { Size = new Size(310, 200) });
        systems.Update();
        Require(world.Read<LayoutRect>(children[0]).Value == new Rect(0, 0, 150, 40), "Canvas resizing updates Grow allocation.");
        Require(world.Read<LayoutRect>(children[1]).Value == new Rect(160, 0, 150, 40), "Gap follows the resized first item.");
        Require(world.Read<LayoutRect>(children[2]).Value == new Rect(0, 50, 310, 40), "The last line grows to its new available size.");

        Entity nestedCanvas = world.CreateEntity(new Canvas { Size = new Size(400, 200) });
        Entity nestedRoot = Views.Spawn(world, nestedCanvas, new NestedItems());
        systems.Update();
        Entity row = Child(world, nestedRoot);
        Entity column = Hierarchy<ElementTree>.GetChildren(world, row)[1];
        Entity leaf = Child(world, column);
        Require(world.Read<LayoutRect>(column).Value == new Rect(100, 10, 290, 180), "Row grows and stretches its nested Column.");
        Require(world.Read<LayoutRect>(leaf).Value == new Rect(100, 10, 290, 30), "Column measures its child at the final width.");
    }

    private static Entity Child(World world, Entity parent)
    {
        var children = Hierarchy<ElementTree>.GetChildren(world, parent);
        Require(children.Count == 1, "This branch declares one child.");
        return children[0];
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private readonly record struct WrappedItems : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Flex(Axis.Horizontal, Gap: 10, Wrap: true).Content())
            {
                Scope.View(new Absolute(), 1).Size(100, 40).Basis(100).Grow();
                Scope.View(new Absolute(), 2).Size(100, 40).Grow();
                Scope.View(new Absolute(), 3).Size(100, 40).Grow();
            }
            return default;
        }
    }

    private readonly record struct NestedItems : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Row(10).Padding(10).Content())
            {
                Scope.View(new Absolute(), 1).Size(80, 40);
                using (Column(5).Grow().Content())
                    Scope.View(new Absolute(), 2).Size(float.NaN, 30);
            }
            return default;
        }
    }
}
