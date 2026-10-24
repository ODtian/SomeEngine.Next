using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;

namespace SomeEngine.Elements;

/// <summary>Queues keyboard input for the next input stage.</summary>
public static class Keyboard
{
    /// <summary>Queues committed characters, including confirmed IME text.</summary>
    public static void Text(World world, string text)
    {
        DevelopmentCheck.Require(text is not null, "Keyboard.Text requires text.");
        FocusInputData.Enqueue(world, new FocusInput { Kind = FocusInputKind.Text, Text = text });
    }

    /// <summary>Queues candidate text and its UTF-16 caret; an empty string ends composition.</summary>
    public static void Composition(World world, string text, int caret)
    {
        DevelopmentCheck.Require(text is not null, "Keyboard.Composition requires text.");
        DevelopmentCheck.Require((uint)caret <= (uint)text.Length,
            "Keyboard.Composition requires a caret inside its text.");
        FocusInputData.Enqueue(world, new FocusInput
        {
            Kind = FocusInputKind.Composition, Text = text, CompositionCaret = caret,
        });
    }

    public static void Key(World world, Key key, bool pressed)
    {
        DevelopmentCheck.Require(key <= Elements.Key.Delete, "Keyboard.Key requires a supported key.");
        FocusInputData.Enqueue(world, new FocusInput { Kind = FocusInputKind.Key, Key = key, Pressed = pressed });
    }
}
