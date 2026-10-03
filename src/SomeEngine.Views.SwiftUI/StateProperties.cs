using System.ComponentModel;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;

namespace SomeEngine.Views.SwiftUI;

/// <summary>Accesses the active view's state properties for generated accessors.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class StateProperties
{
    public static State<T> Get<TView, T>(int propertyNumber, T initial = default!)
        where TView : struct, IView
    {
        World world = Scope.World;
        var entity = Scope.Entity;
        DevelopmentCheck.Require(propertyNumber >= 0, "State properties require a nonnegative property number.");
        DevelopmentCheck.Require(TView.Mode == BuildMode.Rerun, "State properties require a Rerun view.");
        DevelopmentCheck.Require(default(TView) is ISparseComponent ? world.HasSparse<TView>(entity) : world.Has<TView>(entity),
            $"State properties of {typeof(TView).Name} require that view's active build.");
        if (!world.HasSparse<StateValues>(entity))
        {
            world.AddSparse(entity, new StateValues(new List<object?>()));
            if (!world.Has<LocalState>(entity))
                world.AddTag<LocalState>(entity);
        }
        List<object?> values = world.ReadSparse<StateValues>(entity).Values;
        while (values.Count <= propertyNumber)
            values.Add(null);
        if (values[propertyNumber] is { } value)
            return (State<T>)value;
        var state = new State<T>(initial);
        values[propertyNumber] = state;
        return state;
    }
}

internal readonly record struct StateValues(List<object?> Values) : ISparseComponent;
