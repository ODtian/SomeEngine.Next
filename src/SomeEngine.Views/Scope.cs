using System.Diagnostics;
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

    [ThreadStatic]
    private static Stack<ScopeData>? s_reusableData;

    /// <summary>The World containing the view being built.</summary>
    public static World World => Current.World;
    /// <summary>The entity of the view being built.</summary>
    public static Entity Entity => Current.Entity;

    /// <summary>Waits until this World's next view rebuild phase.</summary>
    public static FrameAwaitable NextFrame() => new(Current.Queue);

    internal static ScopeData Data => Current;

    /// <summary>Reads a table or sparse component and records it as a dependency of a Rerun build.</summary>
    /// <remarks>Consume the reference before a structural change can move the component.</remarks>
    public static ref readonly T Read<T>(Entity entity) where T : struct
    {
        ScopeData current = Current;
        DevelopmentCheck.Require(default(T) is IComponent or ISparseComponent,
            $"Scope.Read requires table or sparse component {typeof(T).Name}.");
        if (current.DependenciesEnabled)
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

    /// <summary>Declares a child with key zero, or the surrounding Key scope's key.</summary>
    /// <remarks>
    /// Equal parameters overwrite the stored value while preserving its write version.
    /// Changed parameters rebuild Rerun views immediately. Each new child completes its first build.
    /// </remarks>
    public static Entity View<T>(in T view)
        where T : struct, IView, IEquatable<T> => View(in view, 0);

    /// <summary>Matches a child by key and occurrence and updates its parameters.</summary>
    public static Entity View<T>(in T view, ulong key)
        where T : struct, IView, IEquatable<T>
    {
        ScopeData current = Current;
        if (current.KeyPrefix is ulong prefix)
            key = key == 0 ? prefix : CombineKeys(prefix, key);
        current.Occurrences.TryGetValue(key, out int occurrence);
        current.Occurrences[key] = occurrence + 1;
        int childIndex = current.ChildIndex++;
        var children = Hierarchy<ElementTree>.GetChildren(current.World, current.Entity);
        for (int index = childIndex; index < children.Count; index++)
        {
            Entity existing = children[index];
            ElementKey identity = current.World.Read<ElementKey>(existing);
            if (identity.Key != key || identity.Occurrence != occurrence)
                continue;

            if (current.World.Read<ViewType>(existing).Id != ViewRegistration<T>.Id)
            {
                Hierarchy<ElementTree>.DestroySubtree(current.World, existing);
                break;
            }
            if (index != childIndex)
                Hierarchy<ElementTree>.SetParent(current.World, existing, current.Entity, childIndex);
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
        Entity child = Views.Create(current.World, in view, new ElementKey { Key = key, Occurrence = occurrence });
        Hierarchy<ElementTree>.SetParent(current.World, child, current.Entity, childIndex);
        ViewRegistration<T>.Build(current.World, child);
        return child;
    }

    /// <summary>Groups child declaration keys until the returned scope is disposed.</summary>
    public static KeyScope Key(ulong key)
    {
        ScopeData current = Current;
        var scope = new KeyScope(current.KeyPrefix);
        current.KeyPrefix = current.KeyPrefix is ulong prefix ? CombineKeys(prefix, key) : key;
        return scope;
    }

    internal static void RestoreKey(ulong? key) => Current.KeyPrefix = key;

    private static ulong CombineKeys(ulong prefix, ulong key) =>
        unchecked((prefix + 0x9E3779B97F4A7C15UL) * 0xBF58476D1CE4E5B9UL ^ key);

    private static ScopeData Current
    {
        get
        {
            DevelopmentCheck.Require(s_scopes is { Count: > 0 }, "Scope operations require an active view build.");
            return s_scopes!.Peek();
        }
    }

    internal static BuildScope Enter(World world, Entity entity, bool dependenciesEnabled)
    {
        if (world.HasBuffer<Dependency>(entity))
            world.ExecuteBufferWrite<Dependency>(entity, static buffer => buffer.Clear());
        ScopeData current = s_reusableData is { } reusable && reusable.TryPop(out var available)
            ? available : new();
        current.World = world;
        current.Entity = entity;
        current.DependenciesEnabled = dependenciesEnabled;
        current.Queue = BuildQueue.For(world);
        CaptureSiblingCounts(current);
        (s_scopes ??= new Stack<ScopeData>()).Push(current);
        return new BuildScope(current, releaseOnExit: true);
    }

    internal static BuildScope Resume(ScopeData data)
    {
        (s_scopes ??= new Stack<ScopeData>()).Push(data);
        return new BuildScope(data, releaseOnExit: false);
    }

    internal static void Release(ScopeData data)
    {
        data.World = null!;
        data.Entity = default;
        data.Queue = null!;
        data.Task = default;
        data.Suspended = false;
        data.ChildIndex = 0;
        data.KeyPrefix = null;
        data.Occurrences.Clear();
#if DEBUG
        data.PreviousSiblings.Clear();
#endif
        (s_reusableData ??= new Stack<ScopeData>()).Push(data);
    }

    [Conditional("DEBUG")]
    private static void CaptureSiblingCounts(ScopeData current)
    {
#if DEBUG
        foreach (Entity child in Hierarchy<ElementTree>.GetChildren(current.World, current.Entity))
        {
            ulong key = current.World.Read<ElementKey>(child).Key;
            current.PreviousSiblings.TryGetValue(key, out SiblingGroup group);
            current.PreviousSiblings[key] = new SiblingGroup(group.Count + 1,
                group.LocalState || ContainsLocalState(current.World, child));
        }
#endif
    }

    [Conditional("DEBUG")]
    private static void CheckSiblingCounts(ScopeData current)
    {
#if DEBUG
        var children = Hierarchy<ElementTree>.GetChildren(current.World, current.Entity);
        foreach (var (key, previous) in current.PreviousSiblings)
        {
            current.Occurrences.TryGetValue(key, out int count);
            if (count == previous.Count || Math.Max(count, previous.Count) <= 1)
                continue;

            bool localState = previous.LocalState;
            for (int index = 0; !localState && index < current.ChildIndex; index++)
            {
                Entity child = children[index];
                if (current.World.Read<ElementKey>(child).Key == key)
                    localState = ContainsLocalState(current.World, child);
            }
            DevelopmentCheck.Require(!localState,
                $"Sibling key {key} with local state changed from {previous.Count} to {count} children. " +
                "Use distinct Key scopes for stateful list items.");
        }
#endif
    }

#if DEBUG
    private static bool ContainsLocalState(World world, Entity entity)
    {
        if (world.Has<LocalState>(entity))
            return true;
        foreach (Entity child in Hierarchy<ElementTree>.GetChildren(world, entity))
        {
            if (ContainsLocalState(world, child))
                return true;
        }
        return false;
    }

    internal readonly record struct SiblingGroup(int Count, bool LocalState);
#endif

    internal sealed class ScopeData
    {
        internal World World = null!;
        internal Entity Entity;
        internal bool DependenciesEnabled;
        internal BuildQueue Queue = null!;
        internal BuildTask Task;
        internal uint ParameterVersion;
        internal bool Suspended;
        internal Dictionary<ulong, int> Occurrences { get; } = new();
        internal int ChildIndex;
        internal ulong? KeyPrefix;
#if DEBUG
        internal Dictionary<ulong, SiblingGroup> PreviousSiblings { get; } = new();
#endif
    }

    internal readonly struct BuildScope(ScopeData data, bool releaseOnExit) : IDisposable
    {
        internal void Finish(BuildTask task, uint parameterVersion)
        {
            data.Task = task;
            data.ParameterVersion = parameterVersion;
            if (task.IsCompleted)
            {
                task.Complete();
                Complete();
            }
            else
            {
                data.Suspended = true;
                data.Queue.Add(data);
            }
        }

        internal void Complete()
        {
            ScopeData current = Current;
            CheckSiblingCounts(current);
            var children = Hierarchy<ElementTree>.GetChildren(current.World, current.Entity);
            for (int index = children.Count - 1; index >= current.ChildIndex; index--)
                Hierarchy<ElementTree>.DestroySubtree(current.World, children[index]);
            if (current.DependenciesEnabled)
                current.World.Replace(current.Entity, new ParameterVersion(current.ParameterVersion));
        }

        public void Dispose()
        {
            s_scopes!.Pop();
            if (releaseOnExit && !data.Suspended)
                Release(data);
        }
    }
}
