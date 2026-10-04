using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;
using UserPressable = SomeEngine.Elements.Consumer.Tests.Rewrites.PressableFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

/// <summary>Public pointer behavior shared with the NativeAOT executable.</summary>
internal static partial class PointerChecks
{
    internal static void Run(bool rewritten)
    {
        using var world = new World();
        Entity capture = world.CreateEntity(new InputCapture { Keyboard = true });
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(800, 600), Scale = 2 });
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new HitTestSystem());
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem());
        Entity root = Views.Views.Spawn(world, canvas, new ButtonExample(rewritten, 0));
        systems.Update();
        Entity container = Hierarchy<ElementTree>.GetChildren(world, root)[0];
        Entity button = Hierarchy<ElementTree>.GetChildren(world, container)[0];
        Require(world.Read<DisplayedCount>(root).Value == 0, "A new button displays zero.");

        Pointer.Move(world, canvas, new Vector2(40, 20));
        Pointer.Press(world, canvas, PointerButton.Primary);
        Pointer.Release(world, canvas, PointerButton.Primary);
        systems.Update();
        Require(world.Read<DisplayedCount>(root).Value == 1, "The caller consumes one click on the next frame.");
        Require(world.Read<Clicks>(button).Count == 1, "One completed click advances the public count.");
        Require(world.Read<Hover>(button).Hovered && !world.Read<Hover>(button).Pressed,
            "A released button remains hovered.");
        Require(world.Read<InputCapture>(capture).Pointer && world.Read<InputCapture>(capture).Keyboard,
            "Pointer capture is written beside the existing keyboard capture.");

        world.Replace(root, new ButtonExample(rewritten, 1));
        Pointer.Wheel(world, canvas, new Vector2(0, 2));
        systems.Update();
        Require(world.Read<DisplayedCount>(root).Value == 1 && world.Read<Clicks>(button).Count == 1,
            "A repeated declaration preserves consumed clicks and wheel input preserves click counts.");

        Pointer.Press(world, canvas, PointerButton.Primary);
        systems.Update();
        Require(world.Read<Hover>(button).Pressed, "A held button exposes its press across frames.");
        Pointer.Move(world, canvas, new Vector2(300, 200));
        systems.Update();
        Require(!world.Read<Hover>(button).Hovered && world.Read<Hover>(button).Pressed,
            "The held press stays on its original node as the pointer leaves.");
        Require(world.Read<InputCapture>(capture).Pointer, "A held UI press consumes pointer input.");
        Pointer.Release(world, canvas, PointerButton.Primary);
        systems.Update();
        Require(world.Read<Clicks>(button).Count == 1 && !world.Read<Hover>(button).Pressed,
            "Releasing outside ends the held press and preserves the click count.");
        Require(world.Read<InputCapture>(capture).Pointer, "The UI consumes the release of its held press.");
        systems.Update();
        Require(!world.Read<InputCapture>(capture).Pointer && world.Read<InputCapture>(capture).Keyboard,
            "The following blank frame gives pointer input to the game.");

        Pointer.Move(world, canvas, new Vector2(40, 20));
        Pointer.Press(world, canvas, PointerButton.Primary);
        Pointer.Release(world, canvas, PointerButton.Primary);
        systems.Update();
        Require(world.Read<DisplayedCount>(root).Value == 2 && world.Read<Clicks>(button).Count == 2,
            "A later click is consumed once by the same caller.");
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private readonly record struct ButtonExample(bool Rewritten, int Revision) : IView, IComponent
    {
        public BuildTask Build()
        {
            ref Count count = ref Scope.ReadWrite<Count>(Scope.Entity);
            using (Absolute().Content())
            {
                Entity button = Absolute().Size(100, 50).Position(20, 10);
                if (Rewritten)
                    UserPressable.Pressable(button);
                else
                    PressableFunctions.Pressable(button);
                if (Rewritten ? UserPressable.Clicked(button) : button.Clicked())
                    count.Value++;
                if (Rewritten ? UserPressable.Clicked(button) : button.Clicked())
                    count.Value += 100;
            }
            Scope.Write<DisplayedCount>(Scope.Entity) = new DisplayedCount(count.Value);
            return default;
        }
    }

    private record struct Count(int Value) : IComponent;
    private readonly record struct DisplayedCount(int Value) : IComponent;
}
