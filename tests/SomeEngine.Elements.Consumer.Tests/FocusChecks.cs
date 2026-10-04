using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;
using static SomeEngine.Layout.GridFunctions;
using UserFocus = SomeEngine.Elements.Consumer.Tests.Rewrites.FocusFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

/// <summary>Public focus behavior shared with the NativeAOT executable.</summary>
internal static partial class FocusChecks
{
    internal static void RunGrid(bool rewritten)
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new FocusGrid(rewritten));
        frames.Frame();
        CheckDisplay(frames.World, root, -1, 0);

        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        CheckDisplay(frames.World, root, 0, 0);
        Keyboard.Key(frames.World, Key.Right, true);
        frames.Frame();
        CheckDisplay(frames.World, root, 1, 0);
        Keyboard.Key(frames.World, Key.Right, false);
        frames.Frame();
        CheckDisplay(frames.World, root, 1, 0);

        (Direction Direction, int Index)[] moves =
        [
            (Direction.Down, 4), (Direction.Right, 5), (Direction.Up, 2),
            (Direction.Left, 1), (Direction.Left, 0), (Direction.Left, 0),
            (Direction.Down, 3), (Direction.Down, 6), (Direction.Right, 7),
            (Direction.Right, 8), (Direction.Down, 8),
        ];
        foreach (var move in moves)
        {
            Gamepad.Navigate(frames.World, move.Direction);
            frames.Frame();
            CheckDisplay(frames.World, root, move.Index, 0);
        }

        Gamepad.Confirm(frames.World);
        frames.Frame();
        CheckDisplay(frames.World, root, 8, 1);
        Keyboard.Key(frames.World, Key.Enter, true);
        frames.Frame();
        CheckDisplay(frames.World, root, 8, 2);
        Keyboard.Key(frames.World, Key.Enter, false);
        frames.Frame();
        CheckDisplay(frames.World, root, 8, 2);
        frames.Frame();
        CheckDisplay(frames.World, root, 8, 2);
    }

    internal static void RunInputOrder()
    {
        using var frames = new FocusFrames();
        Entity capture = frames.World.CreateEntity(new InputCapture { Pointer = true });
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new FocusGrid());
        frames.Frame();
        Gamepad.Confirm(frames.World);
        frames.Frame();
        CheckDisplay(frames.World, root, -1, 0);
        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        Keyboard.Key(frames.World, Key.Right, true);
        Gamepad.Confirm(frames.World);
        Gamepad.Navigate(frames.World, Direction.Down);
        Keyboard.Key(frames.World, Key.Enter, true);
        frames.Frame();
        CheckDisplay(frames.World, root, 4, 2);
        Require(frames.World.Read<InputCapture>(capture).Pointer && frames.World.Read<InputCapture>(capture).Keyboard,
            "Focus consumes keyboard input alongside the pointer's existing consumption.");
        Entity grid = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        var cells = Hierarchy<ElementTree>.GetChildren(frames.World, grid);
        Require(frames.World.Read<Clicks>(cells[1]).Count == 1 && frames.World.Read<Clicks>(cells[4]).Count == 1,
            "Each confirmation reaches the focus selected before it.");
        Keyboard.Key(frames.World, Key.Space, true);
        Gamepad.Confirm(frames.World);
        frames.Frame();
        CheckDisplay(frames.World, root, 4, 3);
        Require(frames.World.Read<Clicks>(cells[4]).Count == 3,
            "Multiple confirmations advance the click count and one read consumes them together.");
        frames.World.DestroyEntity(cells[4]);
        Require(frames.World.Read<InputCapture>(capture).Pointer && !frames.World.Read<InputCapture>(capture).Keyboard,
            "Releasing the focused leaf clears keyboard consumption and preserves pointer consumption.");
    }

    internal static void RunWorlds()
    {
        using var first = new FocusFrames();
        using var second = new FocusFrames();
        Entity firstRoot = Views.Views.Spawn(first.World, first.Canvas, new FocusGrid());
        Entity secondRoot = Views.Views.Spawn(second.World, second.Canvas, new FocusGrid());
        first.Frame();
        second.Frame();
        Gamepad.Navigate(first.World, Direction.Right);
        Keyboard.Key(second.World, Key.Down, true);
        first.Frame();
        second.Frame();
        CheckDisplay(first.World, firstRoot, 0, 0);
        CheckDisplay(second.World, secondRoot, 0, 0);
        Gamepad.Navigate(first.World, Direction.Right);
        Gamepad.Confirm(first.World);
        first.Frame();
        second.Frame();
        CheckDisplay(first.World, firstRoot, 1, 1);
        CheckDisplay(second.World, secondRoot, 0, 0);
    }

    private static void CheckDisplay(World world, Entity root, int index, int confirmations)
    {
        Require(world.Read<FocusDisplay>(root) == new FocusDisplay(index, confirmations),
            $"The grid displays focus {index} and {confirmations} confirmations.");
        Entity grid = Hierarchy<ElementTree>.GetChildren(world, root)[0];
        var cells = Hierarchy<ElementTree>.GetChildren(world, grid);
        for (int cell = 0; cell < cells.Count; cell++)
            Require(world.HasSparse<Focused>(cells[cell]) == (cell == index),
                "The displayed cell is the only focused node.");
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private readonly record struct FocusGrid(bool Rewritten = false) : IView, IComponent
    {
        public BuildTask Build()
        {
            int focused = -1;
            ref ConfirmationCount confirmations = ref Scope.ReadWrite<ConfirmationCount>(Scope.Entity);
            using (Grid([GridTrack.Fixed(50), GridTrack.Fixed(50), GridTrack.Fixed(50)],
                       [GridTrack.Fixed(50), GridTrack.Fixed(50), GridTrack.Fixed(50)], Gap: 10).Content())
            {
                for (int index = 0; index < 9; index++)
                {
                    Entity cell = Absolute().Cell(index / 3, index % 3);
                    if (Rewritten)
                        UserFocus.Focusable(cell);
                    else
                        FocusFunctions.Focusable(cell);
                    if (Rewritten ? UserFocus.Focused(cell) : cell.Focused())
                        focused = index;
                    if (cell.Clicked())
                        confirmations.Value++;
                }
            }
            Scope.Write<FocusDisplay>(Scope.Entity) = new FocusDisplay(focused, confirmations.Value);
            return default;
        }
    }

    private record struct ConfirmationCount(int Value) : IComponent;
    private readonly record struct FocusDisplay(int Index, int Confirmations) : IComponent;
}

internal sealed class FocusFrames : IDisposable
{
    private readonly SystemGroup<ImmediateSystemContext> _systems;
    public World World { get; } = new();
    public Entity Canvas { get; }

    public FocusFrames(ISystem<ImmediateSystemContext>? navigation = null)
    {
        Canvas = World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        _systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(World));
        int focusSystem = _systems.Add(new FocusNavigationSystem());
        if (navigation is not null)
        {
            _systems.Disable(focusSystem);
            _systems.Add(navigation);
        }
        _systems.Add(new ViewRebuildSystem());
        _systems.Add(new LayoutSystem());
    }

    public void Frame() => _systems.Update();

    public void Dispose()
    {
        _systems.Dispose();
        World.Dispose();
    }
}
