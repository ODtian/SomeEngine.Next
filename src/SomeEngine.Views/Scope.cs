using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Components;
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

    /// <summary>Reads a table or sparse component and records it as a dependency of a Rerun build.</summary>
    /// <remarks>Consume the reference before a structural change can move the component.</remarks>
    public static ref readonly T Read<T>(Entity entity) where T : struct
    {
        ScopeData current = Current;
        DevelopmentCheck.Require(default(T) is IComponent or ISparseComponent,
            $"Scope.Read requires table or sparse component {typeof(T).Name}.");
        if (current.TrackReads)
        {
            var dependency = new Dependency
            {
                Entity = entity,
                Component = ComponentId<T>.Value,
                Version = current.World.ComponentDataWriteVersion<T>(entity),
            };
            if (!current.World.HasBuffer<Dependency>(current.Entity))
                current.World.AddBuffer<Dependency>(current.Entity);
            current.World.ExecuteBufferWrite<Dependency, Dependency>(current.Entity, ref dependency, static (buffer, ref read) =>
            {
                foreach (Dependency existing in buffer.ReadSpan())
                    if (existing.Entity == read.Entity && existing.Component == read.Component)
                        return;
                buffer.Add(read);
            });
        }
        return ref current.World.ReadComponentReference<T>(entity);
    }

    /// <summary>Declares a child at this build's call index and updates its parameters.</summary>
    /// <remarks>
    /// Equal parameters overwrite the stored value while preserving its write version.
    /// Changed parameters rebuild Rerun views immediately. Each new child completes its first build.
    /// </remarks>
    public static Entity View<T>(in T view)
        where T : struct, IView, IEquatable<T>
    {
        ScopeData current = Current;
        s_scopes!.Pop();
        s_scopes.Push(current with { ChildIndex = current.ChildIndex + 1 });
        var children = Hierarchy<ElementTree>.GetChildren(current.World, current.Entity);
        if (current.ChildIndex < children.Count)
        {
            Entity existing = children[current.ChildIndex];
            DevelopmentCheck.Require(current.World.Read<ViewType>(existing).Id == ViewRegistration<T>.Id,
                "View declarations require the same view type at each child index.");
            T previous = current.World.ReadComponentData<T>(existing);
            if (previous.Equals(view))
            {
                current.World.OverwriteComponentData(existing, in view);
                return existing;
            }

            current.World.ReplaceComponentData(existing, in view);
            if (T.Mode == BuildMode.Rerun)
                ViewRegistration<T>.Build(current.World, existing);
            return existing;
        }
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

    internal static BuildScope Enter(World world, Entity entity, bool trackReads)
    {
        if (world.HasBuffer<Dependency>(entity))
            world.ExecuteBufferWrite<Dependency>(entity, static buffer => buffer.Clear());
        (s_scopes ??= new Stack<ScopeData>()).Push(new ScopeData(world, entity, 0, trackReads));
        return default;
    }

    private readonly record struct ScopeData(World World, Entity Entity, int ChildIndex, bool TrackReads);

    internal readonly struct BuildScope : IDisposable
    {
        public void Dispose() => s_scopes!.Pop();
    }
}
