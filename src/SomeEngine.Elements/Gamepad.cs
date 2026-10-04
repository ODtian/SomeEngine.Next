using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;

namespace SomeEngine.Elements;

/// <summary>Queues gamepad navigation and confirmation for the next input stage.</summary>
public static class Gamepad
{
    public static void Navigate(World world, Direction direction)
    {
        DevelopmentCheck.Require(direction <= Direction.Down, "Gamepad.Navigate requires a supported direction.");
        FocusInputData.Enqueue(world, new FocusInput { Kind = FocusInputKind.Navigate, Direction = direction });
    }

    public static void Confirm(World world) =>
        FocusInputData.Enqueue(world, new FocusInput { Kind = FocusInputKind.Confirm });
}
