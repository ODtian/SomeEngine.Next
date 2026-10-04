using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Elements;
using SomeEngine.Elements.Consumer.Tests;
using SomeEngine.Layout;
using SomeEngine.Views;

internal static partial class TextFieldViewportChecks
{
    internal static void Run()
    {
        Func<string, string, Entity>[] fields =
        [
            static (text, placeholder) => Scope.View(new TextField(text, placeholder), 1),
            static (text, placeholder) => Scope.View(new SomeEngine.Elements.Consumer.Tests.Rewrites.TextField(text, placeholder), 1),
        ];
        foreach (var declare in fields)
        {
            using var frames = new ValueFrames(text: true);
            var value = new State<string>("");
            Entity root = SomeEngine.Views.Views.Spawn(frames.World, frames.Canvas, new BoundField(value, declare));
            frames.Frame();
            Gamepad.Navigate(frames.World, Direction.Right);
            string text = new('A', 30);
            Keyboard.Text(frames.World, text);
            frames.Frame();
            Entity field = frames.World.Read<FieldReference>(root).Field;
            Require(value.Value == text && frames.World.Read<EditableText>(field).Text == text,
                "The parent accepts long text through Edited and passes it back.");
            Require(CaretIsVisible(frames.World, field), "Long text keeps the active caret in its viewport.");
            Keyboard.Key(frames.World, Key.Home, true);
            frames.Frame();
            Require(CaretIsVisible(frames.World, field), "Moving Home keeps the caret visible.");
            Keyboard.Key(frames.World, Key.Shift, true);
            Keyboard.Key(frames.World, Key.End, true);
            Keyboard.Key(frames.World, Key.Shift, false);
            frames.Frame();
            Require(CaretIsVisible(frames.World, field), "Selecting to End keeps the active caret visible.");
            string composition = new('B', 40);
            Keyboard.Composition(frames.World, composition, composition.Length);
            frames.Frame();
            Require(CaretIsVisible(frames.World, field), "The composition caret is visible while showing a long candidate.");
            Keyboard.Composition(frames.World, composition, 0);
            frames.Frame();
            Require(CaretIsVisible(frames.World, field), "An updated composition caret stays visible.");
            Keyboard.Text(frames.World, "Z");
            frames.Frame();
            Require(value.Value == "Z" && CaretIsVisible(frames.World, field), "IME commit returns visible accepted text.");
            Keyboard.Key(frames.World, Key.Backspace, true);
            frames.Frame();
            Entity label = ButtonChecks.DescendantsWith<TextContent>(frames.World, field).Single();
            Require(value.Value == "" && frames.World.Read<TextContent>(label).Value == "Name" &&
                frames.World.Read<AccessibleName>(field).Value == "Name" &&
                frames.World.Read<AccessibleRole>(field).Value == Role.TextField && CaretIsVisible(frames.World, field),
                "Deleting the text restores the placeholder, accessible name, and visible caret.");
        }
    }

    private static bool CaretIsVisible(World world, Entity field)
    {
        Entity scroll = ButtonChecks.DescendantsWith<ScrollOffset>(world, field).Single();
        Rect viewport = world.Read<LayoutRect>(scroll).Value;
        Thickness padding = world.Read<Box>(scroll).Padding;
        Entity caretNode = ButtonChecks.DescendantsWith<Surface>(world, field)
            .Single(node => world.Read<Surface>(node).Fill == new Color(1, 1, 1));
        Rect caret = world.Read<LayoutRect>(caretNode).Value;
        return caret.X >= viewport.X + padding.Left - 0.001f &&
            caret.X + caret.Width <= viewport.X + viewport.Width - padding.Right + 0.001f &&
            caret.Y >= viewport.Y + padding.Top - 0.001f &&
            caret.Y + caret.Height <= viewport.Y + viewport.Height - padding.Bottom + 0.001f;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private readonly record struct BoundField(State<string> Value, Func<string, string, Entity> Declare) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (SomeEngine.Layout.AbsoluteFunctions.Absolute().Content())
            {
                Entity field = Declare(Value.Value, "Name");
                if (field.Edited(out string text)) Value.Value = text;
                Scope.Write<FieldReference>(Scope.Entity) = new FieldReference(field);
            }
            return default;
        }
    }

    private readonly record struct FieldReference(Entity Field) : IComponent;
}
