using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;
using UserDrag = SomeEngine.Elements.Consumer.Tests.Rewrites.DraggableFunctions;
using UserScroll = SomeEngine.Elements.Consumer.Tests.Rewrites.ScrollableFunctions;
using UserSelection = SomeEngine.Elements.Consumer.Tests.Rewrites.SelectableFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

/// <summary>Public behavior composition shared by unit tests and the NativeAOT executable.</summary>
internal static partial class BehaviorChecks
{
    internal static void Run(bool rewritten)
    {
        using var world = new World();
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(200, 120) });
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new HitTestSystem());
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem());
        Entity root = Views.Views.Spawn(world, canvas, new BehaviorExample(rewritten, 0));
        systems.Update();
        Entity container = Hierarchy<ElementTree>.GetChildren(world, root)[0];
        var children = Hierarchy<ElementTree>.GetChildren(world, container);
        Entity dragNode = children[0];
        Entity selection = children[1];
        Entity scroll = children[2];
        Entity content = Hierarchy<ElementTree>.GetChildren(world, scroll)[0];

        Pointer.Move(world, canvas, new Vector2(10, 10));
        Pointer.Press(world, canvas, PointerButton.Primary);
        Pointer.Move(world, canvas, new Vector2(14, 13));
        systems.Update();
        Drag drag = world.Read<Drag>(dragNode);
        Require(drag.Active && drag.Count == 1 && drag.Delta == new Vector2(4, 3) && drag.Total == new Vector2(4, 3),
            "The declared drag reports its first movement.");
        Pointer.Release(world, canvas, PointerButton.Primary);
        systems.Update();
        drag = world.Read<Drag>(dragNode);
        Require(!drag.Active && drag.Delta == Vector2.Zero && drag.Total == new Vector2(4, 3) && drag.Count == 1,
            "Release preserves the completed drag total.");
        Require(world.Read<Clicks>(dragNode).Count == 0, "The drag completes its own pointer gesture.");

        Pointer.Move(world, canvas, new Vector2(70, 10));
        Pointer.Press(world, canvas, PointerButton.Primary);
        Pointer.Release(world, canvas, PointerButton.Primary);
        systems.Update();
        Require(world.Read<Selected>(selection).Value, "A click selects the declared node.");
        world.Replace(root, new BehaviorExample(rewritten, 1));
        systems.Update();
        Require(world.Read<Selected>(selection).Value && world.Read<Drag>(dragNode).Count == 1,
            "Repeated declarations preserve input-written behavior data.");

        Pointer.Move(world, canvas, new Vector2(20, 60));
        Pointer.Wheel(world, canvas, new Vector2(20, 30));
        systems.Update();
        ScrollOffset offset = world.Read<ScrollOffset>(scroll);
        Require(offset.Max == new Vector2(100, 80) && offset.Value == new Vector2(20, 30),
            "Wheel input uses the measured content range.");
        var rect = world.Read<LayoutRect>(content).Value;
        Require(rect.X == -20 && rect.Y == 20, "The content rectangle follows its scroll offset.");

        Pointer.Press(world, canvas, PointerButton.Primary);
        Pointer.Move(world, canvas, new Vector2(10, 40));
        Pointer.Release(world, canvas, PointerButton.Primary);
        systems.Update();
        Require(world.Read<ScrollOffset>(scroll).Value == new Vector2(30, 50),
            "Dragging scrolls opposite the pointer movement and retains the pressed target outside the viewport.");
        Require(world.Read<Drag>(scroll).Count == 1 && !world.Read<Drag>(scroll).Active,
            "A scrollable node exposes the same drag data.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private readonly record struct BehaviorExample(bool Rewritten, int Revision) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            {
                Entity drag = Absolute().Size(40, 30);
                Entity selection = Absolute().Size(40, 30).Position(60, 0);
                Entity scroll = Absolute().Size(100, 60).Position(0, 50);
                Require((Rewritten ? UserDrag.Draggable(drag) : DraggableFunctions.Draggable(drag)) == drag,
                    "Draggable returns its input entity.");
                Require((Rewritten ? UserSelection.Selectable(selection) : SelectableFunctions.Selectable(selection)) == selection,
                    "Selectable returns its input entity.");
                Require((Rewritten ? UserScroll.Scrollable(scroll) : ScrollableFunctions.Scrollable(scroll)) == scroll,
                    "Scrollable returns its input entity.");
                using (scroll.Content())
                    Absolute().Size(200, 140);
            }
            return default;
        }
    }
}
