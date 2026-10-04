using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class TextFieldVisibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongInputKeepsTheFocusedCaretInsideTheViewport(bool rewritten)
    {
        using var frames = new ValueFrames(text: true);
        Entity field = SpawnField(frames, rewritten, "", "Name");
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        string text = new('A', 30);
        Keyboard.Text(frames.World, text);

        frames.Frame();

        Rect caret = ReadCaret(frames.World, field);
        Rect viewport = ReadViewport(frames.World, field);
        Assert.Equal(text, frames.World.Read<EditableText>(field).Text);
        Assert.InRange(caret.X, viewport.X - 0.001f, viewport.X + viewport.Width - caret.Width + 0.001f);
        Assert.InRange(caret.Y, viewport.Y - 0.001f, viewport.Y + viewport.Height - caret.Height + 0.001f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovingAndSelectingKeepTheActiveCaretVisible(bool rewritten)
    {
        using var frames = new ValueFrames(text: true);
        string text = new('A', 40);
        Entity field = SpawnField(frames, rewritten, text, "Name");
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        Keyboard.Key(frames.World, Key.Home, true);
        frames.Frame();
        Rect viewport = ReadViewport(frames.World, field);
        Assert.Equal(viewport.X, ReadCaret(frames.World, field).X, 3);

        Keyboard.Key(frames.World, Key.Shift, true);
        Keyboard.Key(frames.World, Key.End, true);
        frames.Frame();
        Rect selection = ReadSelection(frames.World, field);
        Rect caret = ReadCaret(frames.World, field);
        Assert.Equal(viewport.X + viewport.Width, caret.X + caret.Width, 3);
        Assert.True(selection.X < viewport.X + viewport.Width && selection.X + selection.Width > viewport.X);

        Keyboard.Key(frames.World, Key.Shift, false);
        Keyboard.Key(frames.World, Key.End, true);
        Keyboard.Key(frames.World, Key.Shift, true);
        Keyboard.Key(frames.World, Key.Home, true);
        frames.Frame();
        selection = ReadSelection(frames.World, field);
        Assert.Equal(viewport.X, ReadCaret(frames.World, field).X, 3);
        Assert.True(selection.X < viewport.X + viewport.Width && selection.X + selection.Width > viewport.X);
        Keyboard.Key(frames.World, Key.Shift, false);
        Keyboard.Key(frames.World, Key.Right, true);
        frames.Frame();
        Assert.Equal(viewport.X + viewport.Width, ReadCaret(frames.World, field).X + 1, 3);

        for (int index = 0; index < text.Length; index++)
        {
            Keyboard.Key(frames.World, Key.Left, true);
            frames.Frame();
            caret = ReadCaret(frames.World, field);
            Assert.InRange(caret.X, viewport.X - 0.001f, viewport.X + viewport.Width - caret.Width + 0.001f);
        }
        Assert.Equal(0, frames.World.Read<EditableText>(field).Caret);
        Assert.Equal(text, frames.World.Read<EditableText>(field).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompositionCaretStaysVisibleThroughCandidateUpdatesAndCommit(bool rewritten)
    {
        using var frames = new ValueFrames(text: true);
        Entity field = SpawnField(frames, rewritten, "", "Name");
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        string candidate = new('A', 40);
        Keyboard.Composition(frames.World, candidate, candidate.Length);
        frames.Frame();
        Rect caret = ReadCaret(frames.World, field);
        Rect viewport = ReadViewport(frames.World, field);
        Assert.Equal(viewport.X + viewport.Width, caret.X + caret.Width, 3);
        Assert.Equal("", frames.World.Read<EditableText>(field).Text);
        Assert.Equal(candidate, frames.World.Read<TextContent>(ButtonChecks.DescendantsWith<TextContent>(frames.World, field).Single()).Value);

        Keyboard.Composition(frames.World, candidate, 0);
        frames.Frame();
        Assert.Equal(viewport.X, ReadCaret(frames.World, field).X, 3);
        Keyboard.Composition(frames.World, candidate, candidate.Length);
        Keyboard.Text(frames.World, candidate);
        frames.Frame();
        caret = ReadCaret(frames.World, field);
        Assert.Equal(viewport.X + viewport.Width, caret.X + caret.Width, 3);
        Assert.Equal(candidate, frames.World.Read<EditableText>(field).Text);
        Assert.Equal("", frames.World.Read<EditableText>(field).Composition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WheelPositionIsRetainedUntilTheNextCaretMove(bool rewritten)
    {
        using var frames = new ValueFrames(text: true);
        Entity field = SpawnField(frames, rewritten, "", "Name");
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, new string('A', 30));
        frames.Frame();
        Rect viewport = ReadViewport(frames.World, field);
        Entity scroll = ButtonChecks.DescendantsWith<ScrollOffset>(frames.World, field).Single();
        Pointer.Move(frames.World, frames.Canvas, new Vector2(viewport.X + 1, viewport.Y + 1));
        Pointer.Wheel(frames.World, frames.Canvas, new Vector2(-1000, 0));
        frames.Frame();
        Assert.Equal(0, frames.World.Read<ScrollOffset>(scroll).Value.X);
        frames.Frame();
        Assert.Equal(0, frames.World.Read<ScrollOffset>(scroll).Value.X);

        Keyboard.Key(frames.World, Key.Left, true);
        frames.Frame();

        Rect caret = ReadCaret(frames.World, field);
        Assert.Equal(29, frames.World.Read<EditableText>(field).Caret);
        Assert.InRange(caret.X, viewport.X - 0.001f, viewport.X + viewport.Width - caret.Width + 0.001f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParameterFeedbackAndPlaceholderChangesKeepTheCaretAndAccessibleNameConsistent(bool rewritten)
    {
        using var frames = new ValueFrames(text: true);
        var value = new State<string>("");
        Func<string, string, Entity> declare = rewritten
            ? static (text, placeholder) => Scope.View(new Rewrites.TextField(text, placeholder), 1)
            : static (text, placeholder) => Scope.View(new TextField(text, placeholder), 1);
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new BoundField(value, "Name", declare));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        string entered = new('A', 30);
        Keyboard.Text(frames.World, entered);
        frames.Frame();
        Entity field = frames.World.Read<FieldReference>(root).Field;
        Rect viewport = ReadViewport(frames.World, field);
        Rect caret = ReadCaret(frames.World, field);
        Assert.Equal(entered, value.Value);
        Assert.InRange(caret.X, viewport.X - 0.001f, viewport.X + viewport.Width - caret.Width + 0.001f);
        Assert.Equal("Name", frames.World.Read<AccessibleName>(field).Value);

        frames.World.Replace(root, new BoundField(value, "Account", declare));
        frames.Frame();
        caret = ReadCaret(frames.World, field);
        Assert.Equal(Role.TextField, frames.World.Read<AccessibleRole>(field).Value);
        Assert.Equal("Account", frames.World.Read<AccessibleName>(field).Value);
        Assert.Equal(entered, value.Value);
        Assert.InRange(caret.X, viewport.X - 0.001f, viewport.X + viewport.Width - caret.Width + 0.001f);

        value.Value = "Z";
        frames.Frame();
        Assert.Equal("Z", frames.World.Read<EditableText>(field).Text);
        Keyboard.Key(frames.World, Key.Backspace, true);
        frames.Frame();
        viewport = ReadViewport(frames.World, field);
        Assert.Equal("", value.Value);
        Assert.Equal("Account", frames.World.Read<TextContent>(ButtonChecks.DescendantsWith<TextContent>(frames.World, field).Single()).Value);
        Assert.Equal(viewport.X, ReadCaret(frames.World, field).X, 3);
        Assert.Equal("Account", frames.World.Read<AccessibleName>(field).Value);
    }

    private readonly record struct BoundField(State<string> Value, string Placeholder, Func<string, string, Entity> Declare)
        : IView, IComponent
    {
        public BuildTask Build()
        {
            using (SomeEngine.Layout.AbsoluteFunctions.Absolute().Content())
            {
                Entity field = Declare(Value.Value, Placeholder);
                if (field.Edited(out string text))
                    Value.Value = text;
                Scope.Write<FieldReference>(Scope.Entity) = new FieldReference(field);
            }
            return default;
        }
    }

    private readonly record struct FieldReference(Entity Field) : IComponent;

    private static Entity SpawnField(ValueFrames frames, bool rewritten, string text, string placeholder) => rewritten
        ? Views.Views.Spawn(frames.World, frames.Canvas, new Rewrites.TextField(text, placeholder))
        : Views.Views.Spawn(frames.World, frames.Canvas, new TextField(text, placeholder));

    private static Rect ReadCaret(World world, Entity field) => world.Read<LayoutRect>(
        ButtonChecks.DescendantsWith<Surface>(world, field).Single(node => world.Read<Surface>(node).Fill == new Color(1, 1, 1))).Value;

    private static Rect ReadSelection(World world, Entity field) => world.Read<LayoutRect>(
        ButtonChecks.DescendantsWith<Surface>(world, field).Single(node => world.Read<Surface>(node).Fill == new Color(0.15f, 0.45f, 0.85f, 0.35f))).Value;

    private static Rect ReadViewport(World world, Entity field)
    {
        Entity viewport = ButtonChecks.DescendantsWith<ScrollOffset>(world, field).Single();
        Rect rectangle = world.Read<LayoutRect>(viewport).Value;
        Thickness padding = world.Read<Box>(viewport).Padding;
        return new Rect(rectangle.X + padding.Left, rectangle.Y + padding.Top,
            rectangle.Width - padding.Left - padding.Right, rectangle.Height - padding.Top - padding.Bottom);
    }
}
