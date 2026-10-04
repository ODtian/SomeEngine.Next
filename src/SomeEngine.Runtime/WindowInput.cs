using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Elements;
using SomeEngine.Layout;

namespace SomeEngine.Runtime;

/// <summary>Converts window pixels, buttons and virtual keys into game-world interface input.</summary>
public sealed class WindowInput : IDisposable
{
    private readonly World _world;
    private readonly QueryHandle _canvasQuery;
    private readonly QueryHandle _captureQuery;
    private readonly List<ScreenCanvas> _canvases = [];

    public WindowInput(World world)
    {
        DevelopmentCheck.Require(world is not null, "Window input requires a game World.");
        _world = world;
        _canvasQuery = world.Query(world.QueryDefinition().Read<Canvas>());
        _captureQuery = world.Query(world.QueryDefinition().Read<InputCapture>());
    }

    /// <summary>The current game-world capture flags, after the interface input stage.</summary>
    public InputCapture InputCapture
    {
        get
        {
            InputCapture capture = default;
            _world.ExecuteQuery(_captureQuery, ref capture, static (rows, ref value) =>
            {
                foreach (var row in rows.Rows)
                    value = row.Read<InputCapture>();
            });
            return capture;
        }
    }

    public void MovePointer(Vector2 position)
    {
        ReadCanvases();
        foreach (ScreenCanvas canvas in _canvases)
            Pointer.Move(_world, canvas.Entity, position / canvas.Scale);
    }

    /// <summary>Buttons 0, 1 and 2 are primary, secondary and middle; extra window buttons retain movement.</summary>
    public void ChangePointerButton(int button, bool pressed, Vector2 position)
    {
        MovePointer(position);
        if ((uint)button > 2) return;
        foreach (ScreenCanvas canvas in _canvases)
        {
            if (pressed) Pointer.Press(_world, canvas.Entity, (PointerButton)button);
            else Pointer.Release(_world, canvas.Entity, (PointerButton)button);
        }
    }

    public void LeavePointer() => MovePointer(new Vector2(-1, -1));

    public void ScrollPointer(Vector2 position, Vector2 delta)
    {
        MovePointer(position);
        foreach (ScreenCanvas canvas in _canvases)
            Pointer.Wheel(_world, canvas.Entity, delta / canvas.Scale);
    }

    /// <summary>Ends held presses outside the canvas and forwards key releases when the window loses focus.</summary>
    public void ReleaseInput()
    {
        LeavePointer();
        foreach (ScreenCanvas canvas in _canvases)
        {
            Pointer.Release(_world, canvas.Entity, PointerButton.Primary);
            Pointer.Release(_world, canvas.Entity, PointerButton.Secondary);
            Pointer.Release(_world, canvas.Entity, PointerButton.Middle);
        }
        foreach (Key key in Enum.GetValues<Key>())
            Keyboard.Key(_world, key, false);
    }

    /// <summary>Forwards navigation and confirmation changes, including repeated key presses.</summary>
    public void ChangeKey(int virtualKey, bool pressed)
    {
        Key? key = virtualKey switch
        {
            RuntimeInput.KeyLeft => Key.Left,
            RuntimeInput.KeyRight => Key.Right,
            RuntimeInput.KeyUp => Key.Up,
            RuntimeInput.KeyDown => Key.Down,
            RuntimeInput.KeyEnter => Key.Enter,
            RuntimeInput.KeySpace => Key.Space,
            _ => null,
        };
        if (key is { } value) Keyboard.Key(_world, value, pressed);
    }

    private void ReadCanvases()
    {
        _canvases.Clear();
        List<ScreenCanvas> canvases = _canvases;
        _world.ExecuteQuery(_canvasQuery, ref canvases, static (rows, ref values) =>
        {
            foreach (var row in rows.Rows)
            {
                Canvas canvas = row.Read<Canvas>();
                if (canvas.Target.Kind == CanvasTargetKind.Screen)
                    values.Add(new ScreenCanvas(row.Entity, canvas.Scale));
            }
        });
    }

    public void Dispose()
    {
        _world.ReleaseQuery(_captureQuery);
        _world.ReleaseQuery(_canvasQuery);
    }

    private readonly record struct ScreenCanvas(Entity Entity, float Scale);
}
