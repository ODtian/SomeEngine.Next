using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Views;

/// <summary>The thread's active view build, restored as nested child builds return.</summary>
public static class Scope
{
    [ThreadStatic]
    private static Stack<ScopeData>? s_scopes;

    /// <summary>The World containing the view being built.</summary>
    public static World World => Current.World;
    /// <summary>The entity of the view being built.</summary>
    public static Entity Entity => Current.Entity;

    /// <summary>Appends a child view and completes its first build in a nested scope.</summary>
    public static Entity View<T>(in T view)
        where T : struct, IView, IEquatable<T>
    {
        ScopeData current = Current;
        Entity child = Views.Create(current.World, in view);
        Hierarchy<ElementTree>.SetParent(current.World, child, current.Entity);
        ViewRegistration<T>.Build(current.World, child);
        return child;
    }

    private static ScopeData Current
    {
        get
        {
            DevelopmentCheck.Require(s_scopes is { Count: > 0 }, "Scope operations require an active view build.");
            return s_scopes!.Peek();
        }
    }

    internal static BuildScope Enter(World world, Entity entity)
    {
        (s_scopes ??= new Stack<ScopeData>()).Push(new ScopeData(world, entity));
        return default;
    }

    private readonly record struct ScopeData(World World, Entity Entity);

    internal readonly struct BuildScope : IDisposable
    {
        public void Dispose() => s_scopes!.Pop();
    }
}
