using static SomeEngine.Layout.StackFunctions;
using static SomeEngine.Layout.GridFunctions;
using static SomeEngine.Layout.FlexFunctions;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;

namespace SomeEngine.Views.AotSmoke;

/// <summary>Stack and Grid behavior shared by unit tests and NativeAOT.</summary>
public static partial class StackGridChecks
{
    public static void Run()
    {
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem());
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(400, 300) });
        Entity root = Views.Spawn(world, canvas, new NamedLayouts());
        systems.Update();
        Entity stack = Child(world, root);
        Entity grid = Child(world, stack);
        var cells = Hierarchy<ElementTree>.GetChildren(world, grid);
        Entity overlay = Child(world, cells[0]);
        Entity[] rowItems = Hierarchy<ElementTree>.GetChildren(world, cells[1]).ToArray();
        Require(world.Read<LayoutRect>(grid).Value == new Rect(10, 10, 380, 280), "Grid uses the Stack content area.");
        Require(world.Read<LayoutRect>(overlay).Value == new Rect(50, 40, 20, 10), "Stack aligns its overlaid child to the cell end.");
        Require(world.Read<LayoutRect>(rowItems[0]).Value == new Rect(80, 60, 150, 20), "Grid assigns the remaining column width to its Row.");
        Require(world.Read<LayoutRect>(rowItems[1]).Value == new Rect(240, 60, 150, 20), "Row distributes the Grid cell width and preserves Gap.");

        world.Replace(canvas, new Canvas { Size = new Size(500, 400) });
        systems.Update();
        Require(world.Read<LayoutRect>(grid).Value == new Rect(10, 10, 480, 380), "Canvas resizing changes the Grid tracks.");
        Require(world.Read<LayoutRect>(rowItems[0]).Value == new Rect(80, 60, 200, 20), "Nested Flex receives the resized cell width.");
        Require(world.Read<LayoutRect>(rowItems[1]).Value == new Rect(290, 60, 200, 20), "The second item follows the resized first item.");

        Entity defaults = Views.Spawn(world, canvas, new DefaultStack());
        systems.Update();
        Entity defaultStack = Child(world, defaults);
        Entity defaultChild = Child(world, defaultStack);
        Require(world.Read<LayoutRect>(defaultChild).Value == new Rect(0, 0, 20, 10), "The parameterless Stack uses Start on both axes.");
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

    private readonly record struct NamedLayouts : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Stack(Align.Center, Align.Center).Padding(10).Content())
            using (Grid([GridTrack.Fixed(40), GridTrack.Fraction()], [GridTrack.Fixed(60), GridTrack.Fraction()], Gap: 10).Content())
            {
                using (Stack(Align.End, Align.End).Content())
                    Scope.View(new Absolute(), 1).Size(20, 10);
                using (Row(10).Cell(1, 1).Content())
                {
                    Scope.View(new Absolute(), 2).Size(0, 20).Grow();
                    Scope.View(new Absolute(), 3).Size(0, 20).Grow();
                }
            }
            return default;
        }
    }

    private readonly record struct DefaultStack : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Stack(), 1).Content())
                Scope.View(new Absolute(), 2).Size(20, 10);
            return default;
        }
    }
}
