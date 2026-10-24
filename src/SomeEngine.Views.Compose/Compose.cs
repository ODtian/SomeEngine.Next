using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Views.Compose;

/// <summary>View-local values and shared reactive state for Compose methods.</summary>
public static class Compose
{
    /// <summary>Retains the initializer's value at this source call site on the current view.</summary>
    public static T Remember<T>(Func<T> init)
    {
        DevelopmentCheck.Fail("Remember calls require source-generator interception in a Composable method.");
        return Scope.Remember(init, 0);
    }

    /// <summary>Creates shared state whose reads participate in the current calculation's dependencies.</summary>
    public static State<T> MutableStateOf<T>(T value) => new(value);
}
