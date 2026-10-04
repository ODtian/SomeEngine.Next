using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

internal static partial class FocusChecks
{
    internal static void RunCompositions()
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new FocusPanels());
        frames.Frame();
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        var panels = Hierarchy<ElementTree>.GetChildren(frames.World, container);
        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        Require(frames.World.Read<PanelDisplay>(root).Index == 0,
            "The caller displays focus on the left composition.");
        Gamepad.Navigate(frames.World, Direction.Right);
        Gamepad.Confirm(frames.World);
        frames.Frame();
        Require(frames.World.HasSparse<Focused>(panels[1]) && frames.World.Read<Clicks>(panels[1]).Count == 1,
            "The right composition holds focus and receives confirmation on its own entity.");
        Require(frames.World.Read<PanelDisplay>(root) == new PanelDisplay(1, 1),
            "The caller displays the right composition's focus and confirmation.");
    }

    private readonly record struct FocusPanels() : IView, IComponent
    {
        public BuildTask Build()
        {
            int focused = -1;
            ref ConfirmationCount confirmations = ref Scope.ReadWrite<ConfirmationCount>(Scope.Entity);
            using (Absolute().Content())
            {
                for (int index = 0; index < 2; index++)
                {
                    Entity panel = Scope.View(new FocusPanel(index * 60));
                    if (panel.Focused())
                        focused = index;
                    if (panel.Clicked())
                        confirmations.Value++;
                }
            }
            Scope.Write<PanelDisplay>(Scope.Entity) = new PanelDisplay(focused, confirmations.Value);
            return default;
        }
    }

    private readonly record struct FocusPanel(float X) : IView, IComponent
    {
        public BuildTask Build()
        {
            FocusFunctions.Focusable(Scope.Entity);
            Absolute().Size(20, 20).Position(X, 0);
            return default;
        }
    }

    private readonly record struct PanelDisplay(int Index, int Confirmations = 0) : IComponent;
}
