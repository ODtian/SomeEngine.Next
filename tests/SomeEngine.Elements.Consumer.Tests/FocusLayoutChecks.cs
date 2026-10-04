using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

internal static partial class FocusChecks
{
    internal static void RunMovingLayout()
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new MovingTargets(40));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        Require(frames.World.Read<TargetDisplay>(root).Index == 0, "Initial navigation selects the top-left center.");
        frames.World.Replace(root, new MovingTargets(160));
        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        Require(frames.World.Read<TargetDisplay>(root).Index == 1,
            "The nearer diagonal center in the previous layout receives focus before layout moves it.");
        Gamepad.Navigate(frames.World, Direction.Left);
        frames.Frame();
        Require(frames.World.Read<TargetDisplay>(root).Index == 2,
            "The following input uses the completed new layout to choose its nearest left neighbor.");
    }

    internal static void RunCanvasNavigation()
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new MovingTargets(40));
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity other = Views.Views.Spawn(frames.World, canvas, new FocusPoint(30));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        Require(frames.World.Read<TargetDisplay>(root).Index == 1 && !frames.World.HasSparse<Focused>(other),
            "Direction selects a neighbor in the focused canvas even when another canvas has a closer center.");
    }

    private readonly record struct MovingTargets(float X) : IView, IComponent
    {
        public BuildTask Build()
        {
            int focused = -1;
            using (Absolute().Content())
            {
                Entity first = FocusFunctions.Focusable(Absolute().Size(20, 20));
                Entity moving = FocusFunctions.Focusable(Absolute().Size(20, 20).Position(X, 20));
                Entity last = FocusFunctions.Focusable(Absolute().Size(20, 20).Position(100, 0));
                if (first.Focused()) focused = 0;
                if (moving.Focused()) focused = 1;
                if (last.Focused()) focused = 2;
            }
            Scope.Write<TargetDisplay>(Scope.Entity) = new TargetDisplay(focused);
            return default;
        }
    }

    private readonly record struct FocusPoint(float X) : IView, IComponent
    {
        public BuildTask Build()
        {
            FocusFunctions.Focusable(Scope.Entity);
            Absolute().Size(20, 20).Position(X, 0);
            return default;
        }
    }

    private readonly record struct TargetDisplay(int Index) : IComponent;
}
