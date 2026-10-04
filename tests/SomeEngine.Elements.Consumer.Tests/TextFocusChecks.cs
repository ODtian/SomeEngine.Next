using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;
using static SomeEngine.Layout.GridFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

internal static partial class TextEditingChecks
{
    internal static void RunInputOrder(bool rewritten = false)
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new TwoEditors(rewritten));
        frames.Frame();
        EditorNodes nodes = frames.World.Read<EditorNodes>(root);
        Keyboard.Text(frames.World, "early");
        Gamepad.Navigate(frames.World, Direction.Down);
        Keyboard.Text(frames.World, "first");
        Keyboard.Composition(frames.World, "候", 1);
        Gamepad.Navigate(frames.World, Direction.Down);
        Keyboard.Text(frames.World, "second");
        Keyboard.Composition(frames.World, "选", 1);
        Gamepad.Navigate(frames.World, Direction.Up);
        Keyboard.Text(frames.World, "1");
        frames.Frame();

        EditableText first = frames.World.Read<EditableText>(nodes.First);
        EditableText second = frames.World.Read<EditableText>(nodes.Second);
        Require(first.Text == "first1" && second.Text == "second" &&
            string.IsNullOrEmpty(first.Composition) && string.IsNullOrEmpty(second.Composition) &&
            first.CompositionCaret == 0 && second.CompositionCaret == 0 &&
            frames.World.HasSparse<Focused>(nodes.First),
            "Each text reaches its event's focused node and leaving an editor ends its candidate presentation.");
        Keyboard.Key(frames.World, Key.Down, true);
        frames.Frame();
        Require(frames.World.HasSparse<Focused>(nodes.First),
            "A single-line editor keeps vertical keys while gamepad directions navigate between editors.");
    }

    private readonly record struct TwoEditors(bool Rewritten = false) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Grid([GridTrack.Fixed(60), GridTrack.Fixed(60)], [GridTrack.Fixed(120)], Gap: 10).Content())
            {
                Entity first = DeclareEditor(Absolute().Cell(0, 0), Rewritten);
                Entity second = DeclareEditor(Absolute().Cell(1, 0), Rewritten);
                Scope.Write<EditorNodes>(Scope.Entity) = new EditorNodes(first, second);
            }
            return default;
        }
    }

    private readonly record struct EditorNodes(Entity First, Entity Second) : IComponent;
}
