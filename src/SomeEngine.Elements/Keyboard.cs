using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;

namespace SomeEngine.Elements;

/// <summary>Queues key changes for the next focus navigation stage.</summary>
public static class Keyboard
{
    public static void Key(World world, Key key, bool pressed)
    {
        DevelopmentCheck.Require(key <= Elements.Key.Space, "Keyboard.Key requires a supported key.");
        FocusInputData.Enqueue(world, new FocusInput { Kind = FocusInputKind.Key, Key = key, Pressed = pressed });
    }
}
