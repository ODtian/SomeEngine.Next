using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Typography;
using SomeEngine.Views;
using static SomeEngine.Elements.ButtonFunctions;
using static SomeEngine.Layout.AbsoluteFunctions;
using Size = SomeEngine.Layout.Size;

namespace SomeEngine.Elements.Consumer.Tests;

/// <summary>Button behavior exercised by the consumer tests and the NativeAOT executable.</summary>
internal static partial class ButtonChecks
{
    internal static void Run(ButtonKind kind)
    {
        using var frames = new ButtonFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ClickingButtonExample(kind, "AV"));
        frames.Frame();
        Entity button = frames.World.Read<ClickDisplay>(root).Button;
        Require(frames.World.Read<AccessibleRole>(button).Value == Role.Button &&
            frames.World.Read<AccessibleName>(button).Value == "AV",
            "Built-in, user, and icon buttons expose the button role and their label.");
        Require(frames.World.Read<ClickDisplay>(root).Count == 0, "A new button displays zero consumed clicks.");

        Pointer.Move(frames.World, frames.Canvas, new Vector2(4, 4));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Require(frames.World.Read<ClickDisplay>(root).Count == 0, "Holding a press keeps the caller's click count.");
        Require(frames.World.Read<Surface>(DescendantsWith<Surface>(frames.World, button).Single()).Fill ==
            new Color(0.1f, 0.1f, 0.1f), "The held button uses its default pressed fill.");
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Require(frames.World.Read<ClickDisplay>(root).Count == 1 && !frames.World.Read<ClickDisplay>(root).SecondClick,
            "The caller consumes a completed click once across successive reads.");
        Require(frames.World.Read<Clicks>(button).Count == 1, "A completed click advances the public count.");

        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Require(frames.World.Read<ClickDisplay>(root).Count == 1 && !frames.World.Read<ClickDisplay>(root).SecondClick,
            "A repeated release preserves the consumed click display.");
        for (int click = 0; click < 2; click++)
        {
            Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
            Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        }
        frames.Frame();
        Require(frames.World.Read<ClickDisplay>(root).Count == 2 && !frames.World.Read<ClickDisplay>(root).SecondClick,
            "One read consumes all clicks completed since the previous read.");
        Require(frames.World.Read<Clicks>(button).Count == 3, "Each completed click remains in the public count.");

        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        var theme = new ButtonTheme
        {
            Fill = new Color(0.4f, 0.5f, 0.6f),
            HoverFill = new Color(0.5f, 0.6f, 0.7f),
            PressedFill = new Color(0.2f, 0.3f, 0.4f),
            TextColor = new Color(0.8f, 0.7f, 0.6f, 0.5f),
            Padding = new Thickness(3, 5, 7, 11),
            CornerRadius = 12,
        };
        frames.World.Replace(root, new ClickingButtonExample(kind, "OK", theme));
        frames.Frame();
        button = frames.World.Read<ClickDisplay>(root).Button;
        Require(frames.World.Read<AccessibleRole>(button).Value == Role.Button &&
            frames.World.Read<AccessibleName>(button).Value == "OK",
            "A changed label updates the button's accessibility name.");
        List<Entity> texts = DescendantsWith<Text>(frames.World, button);
        Require(texts.Count(node => frames.World.Read<Text>(node).Value == "OK") == 1,
            "Updated parameters display the new button label.");
        Require(frames.World.Read<Hover>(button).Pressed && frames.World.Read<ClickDisplay>(root).Count == 2,
            "Updating the label and theme preserves the held press and consumed clicks.");
        Entity surface = DescendantsWith<Surface>(frames.World, button).Single();
        Require(frames.World.Read<Surface>(surface).Fill == new Color(0.2f, 0.3f, 0.4f) &&
            frames.World.Read<Surface>(surface).CornerRadius == 12 &&
            frames.World.Read<Box>(surface).Padding == new Thickness(3, 5, 7, 11),
            "Provide updates pressed fill, corners, and asymmetric padding within the frame.");
        foreach (Entity text in texts)
            Require(frames.World.Read<ShapedText>(text).Style.Color == new Color(0.8f, 0.7f, 0.6f, 0.5f),
                "The label and glyph use the provided text color.");
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Require(frames.World.Read<ClickDisplay>(root).Count == 3 && !frames.World.Read<ClickDisplay>(root).SecondClick,
            "The held press completes one later click after the parameter update.");
        Require(frames.World.Read<Surface>(surface).Fill == new Color(0.5f, 0.6f, 0.7f),
            "Release uses the provided hover fill.");
        Pointer.Move(frames.World, frames.Canvas, new Vector2(300, 200));
        frames.Frame();
        Require(frames.World.Read<Surface>(surface).Fill == new Color(0.4f, 0.5f, 0.6f),
            "Moving away uses the provided normal fill.");
    }

    internal static Entity DeclareButton(ButtonKind kind, string label) => kind switch
    {
        ButtonKind.BuiltIn => Button(label),
        ButtonKind.Rewritten => Rewrites.ButtonFunctions.Button(label),
        ButtonKind.Icon => Rewrites.IconButtonFunctions.IconButton(">", label),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static List<Entity> DescendantsWith<T>(World world, Entity node) where T : struct, IComponent
    {
        var result = new List<Entity>();
        foreach (Entity child in Hierarchy<ElementTree>.GetChildren(world, node))
        {
            if (world.Has<T>(child))
                result.Add(child);
            result.AddRange(DescendantsWith<T>(world, child));
        }
        return result;
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private readonly record struct ClickingButtonExample(ButtonKind Kind, string Label, ButtonTheme? Theme = null)
        : IView, IComponent
    {
        public BuildTask Build()
        {
            if (Theme is ButtonTheme theme)
                Scope.Entity.Provide(theme);
            ref ClickCount count = ref Scope.ReadWrite<ClickCount>(Scope.Entity);
            using (Absolute().Content())
            {
                Entity button = DeclareButton(Kind, Label);
                if (button.Clicked())
                    count.Value++;
                bool secondClick = button.Clicked();
                Scope.Write<ClickDisplay>(Scope.Entity) = new ClickDisplay(button, count.Value, secondClick);
            }
            return default;
        }
    }

    private record struct ClickCount(int Value) : IComponent;
    private readonly record struct ClickDisplay(Entity Button, int Count, bool SecondClick) : IComponent;
}

public enum ButtonKind : byte
{
    BuiltIn,
    Rewritten,
    Icon,
}

internal sealed class ButtonFrames : IDisposable
{
    private readonly Font _font;
    private readonly SystemGroup<ImmediateSystemContext> _systems;
    public World World { get; } = new();
    public Entity Canvas { get; }

    public ButtonFrames()
    {
        _font = Font.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "Latin.ttf")));
        Canvas = World.CreateEntity(new Canvas { Size = new Size(320, 240) });
        World.Add(Canvas, new TextTheme(new TextStyle { Font = _font, Size = 20 }));
        _systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(World));
        var shaping = new TextShapingSystem();
        _systems.Add(new HitTestSystem());
        _systems.Add(new ViewRebuildSystem());
        _systems.Add(shaping);
        _systems.Add(new LayoutSystem(shaping));
    }

    public void Frame() => _systems.Update();

    public void Dispose()
    {
        _systems.Dispose();
        World.Dispose();
        _font.Dispose();
    }
}
