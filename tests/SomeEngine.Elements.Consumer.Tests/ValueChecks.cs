using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using Font = SomeEngine.Typography.Font;
using SomeEngine.Views;
using static SomeEngine.Elements.ToggleFunctions;
using static SomeEngine.Elements.SliderFunctions;
using static SomeEngine.Elements.ValueFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

internal static partial class ValueChecks
{
    internal static void Run()
    {
        for (int variant = 0; variant < 2; variant++)
        {
            RunToggle(variant == 1);
            RunKeyboard(variant == 1);
            RunDrag(variant == 1);
            RunLimits(variant == 1);
            RunDragOrder(variant == 1);
            RunContinuous(variant == 1);
            RunEqualRange(variant == 1);
            RunHalfStep(variant == 1);
        }
        RunVolume();
    }

    internal static void RunToggle(bool rewritten)
    {
        using var frames = new ValueFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ToggleExample(rewritten));
        frames.Frame();
        ToggleDisplay display = frames.World.Read<ToggleDisplay>(root);
        Require(!display.On, "The toggle displays the caller's initial value.");
        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        display = frames.World.Read<ToggleDisplay>(root);
        Require(display.On && !display.Again && ReadToggle(frames.World, display.Toggle, rewritten),
            "A pointer activation toggles the caller's value once and updates its view.");
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Key(frames.World, Key.Space, true);
        frames.Frame();
        display = frames.World.Read<ToggleDisplay>(root);
        Require(!display.On && !display.Again && !ReadToggle(frames.World, display.Toggle, rewritten),
            "Space activates the focused toggle through the same public behavior.");
        frames.Frame();
        Require(!frames.World.Read<ToggleDisplay>(root).On, "The next frame retains the caller's value.");
        Keyboard.Key(frames.World, Key.Enter, true);
        frames.Frame();
        Require(frames.World.Read<ToggleDisplay>(root).On, "Enter confirms the focused toggle.");
        Gamepad.Confirm(frames.World);
        frames.Frame();
        Require(!frames.World.Read<ToggleDisplay>(root).On, "Gamepad confirmation uses the same toggle behavior.");
    }

    private static bool ReadToggle(World world, Entity node, bool rewritten) => rewritten ?
        world.ReadSparse<Rewrites.Toggle>(node).On : world.ReadSparse<Toggle>(node).On;

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    internal static void RunKeyboard(bool knob)
    {
        using var frames = new ValueFrames();
        Entity settings = frames.World.CreateEntity(new AudioSettings { Volume = 0.5f });
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ValueExample(settings, 0, 1, 0.1f, knob));
        frames.Frame();
        Entity slider = frames.World.Read<ValueDisplay>(root).Control;
        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        Require(frames.World.HasSparse<Focused>(slider), "Navigation focuses the slider.");
        Keyboard.Key(frames.World, Key.Right, true);
        Keyboard.Key(frames.World, Key.Up, true);
        frames.Frame();
        Require(Near(frames.World.Read<AudioSettings>(settings).Volume, 0.7f),
            "Right and Up apply ordered positive steps through Edited.");
        Require(!frames.World.Read<ValueDisplay>(root).Again,
            "A second Edited read consumes the same edit once.");
        Keyboard.Key(frames.World, Key.Down, true);
        Keyboard.Key(frames.World, Key.Left, false);
        frames.Frame();
        Require(Near(frames.World.Read<AudioSettings>(settings).Volume, 0.6f),
            "Down decreases the value and a key release retains it.");
        Keyboard.Key(frames.World, Key.Left, true);
        frames.Frame();
        Require(Near(frames.World.Read<AudioSettings>(settings).Volume, 0.5f), "Left decreases the value.");
        Require(Near(frames.World.Read<ValueRange>(slider).Value, 0.5f),
            "The caller's committed value becomes the slider's parameter in the same frame.");
    }

    internal static void RunDrag(bool knob)
    {
        using var frames = new ValueFrames();
        Entity settings = frames.World.CreateEntity(new AudioSettings { Volume = 0.3f });
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ValueExample(settings, 0.1f, 0.9f, 0.2f, knob));
        frames.Frame();
        uint version = frames.World.WriteVersion<AudioSettings>(settings);
        Vector2 start = knob ? new Vector2(24, 24) : new Vector2(43, 12);
        Vector2 direction = knob ? -Vector2.UnitY : Vector2.UnitX;
        Pointer.Move(frames.World, frames.Canvas, start);
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Move(frames.World, frames.Canvas, start + 12 * direction);
        frames.Frame();
        Require(Near(frames.World.Read<AudioSettings>(settings).Volume, 0.3f) &&
            frames.World.WriteVersion<AudioSettings>(settings) == version,
            "A movement below half a step preserves the caller's value and version.");
        Pointer.Move(frames.World, frames.Canvas, start + 24 * direction);
        frames.Frame();
        Require(Near(frames.World.Read<AudioSettings>(settings).Volume, 0.5f),
            "Two small movements accumulate before quantization relative to Minimum.");
        Pointer.Move(frames.World, frames.Canvas, start + 400 * direction);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Require(Near(frames.World.Read<AudioSettings>(settings).Volume, 0.9f),
            "A captured drag reaches the upper limit even when release is outside the control.");
        Entity control = frames.World.Read<ValueDisplay>(root).Control;
        Require(!frames.World.Read<Drag>(control).Active && !frames.World.Read<ValueDisplay>(root).Again,
            "The completed gesture supplies one consumed edit.");
        version = frames.World.WriteVersion<AudioSettings>(settings);
        frames.Frame();
        Require(frames.World.WriteVersion<AudioSettings>(settings) == version,
            "A later frame retains the completed value without another edit.");
    }

    private static bool Near(float actual, float expected) => MathF.Abs(actual - expected) < 0.00001f;

    private readonly record struct ValueExample(Entity Settings, float Minimum, float Maximum, float Step, bool Knob = false) : IView, IComponent
    {
        public BuildTask Build()
        {
            float volume = Scope.Read<AudioSettings>(Settings).Volume;
            Entity control = Knob ? Rewrites.KnobFunctions.Knob(volume, Minimum, Maximum, Step) :
                Slider(volume, Minimum, Maximum, Step);
            if (control.Edited(out float value))
                Scope.Write<AudioSettings>(Settings).Volume = value;
            Scope.Write<ValueDisplay>(Scope.Entity) = new ValueDisplay(control, control.Edited(out _));
            return default;
        }
    }

    private struct AudioSettings : IComponent { public float Volume; }
    private readonly record struct ValueDisplay(Entity Control, bool Again) : IComponent;

    private readonly record struct ToggleExample(bool Rewritten) : IView, IComponent
    {
        public BuildTask Build()
        {
            ref ToggleValue state = ref Scope.ReadWrite<ToggleValue>(Scope.Entity);
            Entity toggle = Rewritten ? Rewrites.ToggleFunctions.Toggle(state.On) : Toggle(state.On);
            if (toggle.Toggled())
                state.On = !state.On;
            Scope.Write<ToggleDisplay>(Scope.Entity) = new ToggleDisplay(toggle, state.On, toggle.Toggled());
            return default;
        }
    }

    private struct ToggleValue : IComponent { public bool On; }
    private readonly record struct ToggleDisplay(Entity Toggle, bool On, bool Again) : IComponent;
}

internal sealed class ValueFrames : IDisposable
{
    private readonly SystemGroup<ImmediateSystemContext> _systems;
    private readonly Font? _font;
    internal World World { get; } = new();
    internal Entity Canvas { get; }

    internal ValueFrames(bool text = false, ISystem<ImmediateSystemContext>? gameInput = null)
    {
        Canvas = World.CreateEntity(new Canvas { Size = new Size(320, 240) });
        _systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(World));
        _systems.Add(new HitTestSystem());
        _systems.Add(new FocusNavigationSystem());
        if (gameInput is not null)
            _systems.Add(gameInput);
        _systems.Add(new ViewRebuildSystem());
        if (text)
        {
            _font = Font.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "Latin.ttf")));
            World.Add(Canvas, new TextTheme(new TextStyle { Font = _font, Size = 20 }));
            var shaping = new TextShapingSystem();
            _systems.Add(shaping);
            _systems.Add(new LayoutSystem(shaping));
        }
        else
            _systems.Add(new LayoutSystem());
    }

    internal void Frame() => _systems.Update();

    public void Dispose()
    {
        _systems.Dispose();
        World.Dispose();
        _font?.Dispose();
    }
}
