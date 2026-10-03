using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Components;

namespace SomeEngine.Views;

public static partial class Scope
{
    /// <summary>Retains a value on the current view at a call-site key and occurrence.</summary>
    /// <remarks>The Compose generator supplies the key. Values live until the view is released.</remarks>
    public static T Remember<T>(Func<T> init, ulong key)
    {
        ScopeData current = Current;
        DevelopmentCheck.Require(init is not null, "Remember requires an initializer.");
        if (current.KeyPrefix is ulong prefix)
            key = CombineKeys(prefix, key);
        var occurrences = (current.Calculation ?? current).RememberOccurrences ??= new();
        occurrences.TryGetValue(key, out int occurrence);
        occurrences[key] = occurrence + 1;

        if (!current.World.HasSparse<RememberedValues>(current.Entity))
        {
            current.World.AddSparse(current.Entity, new RememberedValues(new()));
            if (!current.World.Has<LocalState>(current.Entity))
                current.World.AddTag<LocalState>(current.Entity);
        }
        var values = current.World.ReadSparse<RememberedValues>(current.Entity).Values;
        if (values.TryGetValue((key, occurrence), out object? value))
            return (T)value!;
        T initialized = init();
        values.Add((key, occurrence), initialized);
        return initialized;
    }

    private readonly record struct RememberedValues(Dictionary<(ulong Key, int Occurrence), object?> Values) : ISparseComponent;
}
