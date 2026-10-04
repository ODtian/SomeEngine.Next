using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

internal static partial class FocusChecks
{
    internal static void RunMixedInputs()
    {
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new HitTestSystem());
        systems.Add(new FocusNavigationSystem());
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem());
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(world, canvas, new FocusButton());
        systems.Update();
        Gamepad.Navigate(world, Direction.Right);
        systems.Update();
        Require(world.Read<ButtonDisplay>(root) == new ButtonDisplay(true, 0),
            "A composed focusable and pressable button displays its focus.");
        Pointer.Move(world, canvas, new Vector2(20, 20));
        Pointer.Press(world, canvas, PointerButton.Primary);
        Pointer.Release(world, canvas, PointerButton.Primary);
        Gamepad.Confirm(world);
        systems.Update();
        Require(world.Read<Clicks>(root).Count == 2 && world.Read<ButtonDisplay>(root) == new ButtonDisplay(true, 1),
            "A pointer click and focus confirmation share one count consumed by the caller.");
        Require(world.Read<Hover>(root).Hovered && world.Read<Hover>(root).Within,
            "The same button exposes pointer hover while it holds keyboard focus.");
        Keyboard.Key(world, Key.Space, true);
        systems.Update();
        Require(world.Read<Clicks>(root).Count == 3 && world.Read<ButtonDisplay>(root) == new ButtonDisplay(true, 2),
            "A later keyboard confirmation continues the shared click count.");
    }

    private readonly record struct FocusButton() : IView, IComponent
    {
        public BuildTask Build()
        {
            FocusFunctions.Focusable(PressableFunctions.Pressable(Scope.Entity));
            Absolute().Size(20, 20).Position(10, 10);
            ref ConfirmationCount count = ref Scope.ReadWrite<ConfirmationCount>(Scope.Entity);
            if (Scope.Entity.Clicked())
                count.Value++;
            Scope.Write<ButtonDisplay>(Scope.Entity) = new ButtonDisplay(Scope.Entity.Focused(), count.Value);
            return default;
        }
    }

    private readonly record struct ButtonDisplay(bool Focused, int Confirmations) : IComponent;
}
