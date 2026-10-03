using System.Diagnostics;
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

    /// <summary>Declares a child whose call-site key is supplied by the view generator.</summary>
    /// <remarks>
    /// Equal parameters overwrite the stored value while preserving its write version.
    /// Changed parameters rebuild Rerun views immediately. Each new child completes its first build.
    /// </remarks>
    public static Entity View<T>(in T view)
        where T : struct, IView, IEquatable<T>
    {
        DevelopmentCheck.Fail("View calls require source-generator interception. Use View(in T, ulong) for an explicit node identity.");
        return View(in view, 0);
    }

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

    internal static BuildScope Enter(World world, Entity entity)
    {
        var current = new ScopeData(world, entity);
        CaptureSiblingCounts(current);
        (s_scopes ??= new Stack<ScopeData>()).Push(current);
        return default;
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

    private readonly record struct SiblingGroup(int Count, bool LocalState);
#endif

    private sealed class ScopeData(World world, Entity entity)
    {
        internal World World { get; } = world;
        internal Entity Entity { get; } = entity;
        internal Dictionary<ulong, int> Occurrences { get; } = new();
        internal int ChildIndex;
        internal ulong? KeyPrefix;
#if DEBUG
        internal Dictionary<ulong, SiblingGroup> PreviousSiblings { get; } = new();
#endif
    }

    internal readonly struct BuildScope : IDisposable
    {
        internal void Complete()
        {
            ScopeData current = Current;
            CheckSiblingCounts(current);
            var children = Hierarchy<ElementTree>.GetChildren(current.World, current.Entity);
            for (int index = current.ChildIndex; index < children.Count; index++)
                Hierarchy<ElementTree>.DestroySubtree(current.World, children[index]);
        }

        public void Dispose() => s_scopes!.Pop();
    }
}
