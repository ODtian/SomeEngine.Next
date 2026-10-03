using System.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Views;

/// <summary>The thread's active view build, restored as nested child builds return.</summary>
public static class Scope
{
    [ThreadStatic]
    private static Stack<ScopeData>? s_scopes;
#if DEBUG
    [ThreadStatic]
    private static Dictionary<Entity, (ScopeData Data, int Version)>? s_childDeclarations;
#endif

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
                Kind = DependencyKind.Component,
                Entity = entity,
                Component = ComponentId<T>.Value,
                Version = current.World.ComponentDataWriteVersion<T>(entity),
            };
            RecordDependency(current, dependency);
        }
        return ref current.World.ReadComponentReference<T>(entity);
    }

    /// <summary>Checks component presence and records the result as a dependency of a Rerun build.</summary>
    public static bool Has<T>(Entity entity) where T : struct
    {
        ScopeData current = Current;
        DevelopmentCheck.Require(default(T) is IComponent or ISparseComponent or ITag or ISharedComponent,
            $"Scope.Has requires a component or tag type, received {typeof(T).Name}.");
        bool present = current.World.HasComponentData<T>(entity);
        if (current.DependenciesEnabled)
            RecordDependency(current, new Dependency
            {
                Kind = DependencyKind.Presence,
                Entity = entity,
                Component = ComponentId<T>.Value,
                Version = present ? 1u : 0u,
            });
        return present;
    }

    /// <summary>Enumerates query entities and records membership as a dependency of a Rerun build.</summary>
    /// <remarks>Each call captures the current structural membership in query order.</remarks>
    public static QueryRows Each(QueryHandle query)
    {
        ScopeData current = Current;
        ReadOnlySpan<Entity> entities = current.World.ReadQueryEntities(query, out uint version);
        if (current.DependenciesEnabled)
            RecordDependency(current, new Dependency
            {
                Kind = DependencyKind.Membership,
                Query = query,
                Version = version,
            });
        return new QueryRows(entities);
    }

    private static void RecordDependency(ScopeData current, Dependency dependency)
    {
        if (!current.World.HasBuffer<Dependency>(current.Entity))
            current.World.AddBuffer<Dependency>(current.Entity);
        current.World.ExecuteBufferWrite<Dependency, Dependency>(current.Entity, ref dependency, static (buffer, ref read) =>
        {
            foreach (Dependency existing in buffer.ReadSpan())
                if (existing.Kind == read.Kind &&
                    (read.Kind == DependencyKind.Membership
                        ? existing.Query.Equals(read.Query)
                        : existing.Entity == read.Entity && existing.Component == read.Component))
                    return;
            buffer.Add(read);
        });
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
        DevelopmentCheck.Require(current.DeclaresChildren,
            "ChildSource.Caller builds write their own data; declare their children through Content.");
        if (current.KeyPrefix is ulong prefix)
            key = key == 0 ? prefix : CombineKeys(prefix, key);
        current.Occurrences.TryGetValue(key, out int occurrence);
        current.Occurrences[key] = occurrence + 1;
        int childIndex = current.ChildIndex++;
        var children = Hierarchy<ElementTree>.GetChildren(current.World, current.Parent);
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
                Hierarchy<ElementTree>.SetParent(current.World, existing, current.Parent, childIndex);
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
        Hierarchy<ElementTree>.SetParent(current.World, child, current.Parent, childIndex);
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

    /// <summary>Suspends dependency tracking until the returned scope is disposed.</summary>
    /// <remarks>Nested view builds use their own build mode's dependency tracking.</remarks>
    public static UntrackScope Untrack()
    {
        ScopeData current = Current;
        var scope = new UntrackScope(current.DependenciesEnabled);
        current.DependenciesEnabled = false;
        return scope;
    }

    internal static void RestoreDependencyTracking(bool enabled) => Current.DependenciesEnabled = enabled;

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

    internal static BuildScope Enter(World world, Entity entity, bool declaresChildren, bool dependenciesEnabled)
    {
        if (world.HasBuffer<Dependency>(entity))
            world.ExecuteBufferWrite<Dependency>(entity, static buffer => buffer.Clear());
        ScopeData current = s_reusableData is { } reusable && reusable.TryPop(out var available)
            ? available : new();
        current.World = world;
        current.Entity = entity;
        current.ViewData = current;
        current.Parent = entity;
        current.DeclaresChildren = declaresChildren;
        current.DependenciesEnabled = dependenciesEnabled;
        current.Queue = BuildQueue.For(world);
        if (declaresChildren)
        {
            CaptureSiblingCounts(current);
        }
        (s_scopes ??= new Stack<ScopeData>()).Push(current);
        return new BuildScope(current, releaseOnExit: true);
    }

    internal static BuildScope Resume(ScopeData data)
    {
        if (data != data.ViewData)
            CheckChildDeclaration(data);
        (s_scopes ??= new Stack<ScopeData>()).Push(data);
        return new BuildScope(data, releaseOnExit: false);
    }

    internal static void Release(ScopeData data)
    {
        if (data.ContentData is { } content)
        {
            foreach (ScopeData declaration in content)
                Release(declaration);
            content.Clear();
        }
        data.World = null!;
        data.Entity = default;
        data.Parent = default;
        data.ViewData = null!;
        data.Queue = null!;
        data.Task = default;
        data.ParameterVersion = null;
        data.Suspended = false;
        data.ChildIndex = 0;
        data.KeyPrefix = null;
        data.Occurrences.Clear();
#if DEBUG
        data.PreviousSiblings.Clear();
#endif
        (s_reusableData ??= new Stack<ScopeData>()).Push(data);
    }

    internal static void RetainContent(ScopeData data)
    {
        ScopeData view = data.ViewData;
        if (data != view && !data.Suspended)
        {
            data.Suspended = true;
            (view.ContentData ??= new()).Add(data);
        }
    }

    internal static void CompleteBuild(ScopeData data)
    {
        if (data.ContentData is { } content)
            foreach (ScopeData declaration in content)
                CompleteChildren(declaration);
        CompleteChildren(data);
        if (data.ParameterVersion is uint version)
            data.World.Replace(data.Entity, new ParameterVersion(version));
    }

    private static void CompleteChildren(ScopeData data)
    {
        if (!data.DeclaresChildren)
            return;
        CheckSiblingCounts(data);
        var children = Hierarchy<ElementTree>.GetChildren(data.World, data.Parent);
        for (int index = children.Count - 1; index >= data.ChildIndex; index--)
            Hierarchy<ElementTree>.DestroySubtree(data.World, children[index]);
    }

    internal static ParentScope EnterContent(Entity entity)
    {
        ScopeData build = Current;
        ViewTypes.CheckContent(build.World, entity);
        ScopeData current = s_reusableData is { } reusable && reusable.TryPop(out var available)
            ? available : new();
        current.World = build.World;
        current.Entity = build.Entity;
        current.ViewData = build.ViewData;
        current.Parent = entity;
        current.DeclaresChildren = true;
        current.DependenciesEnabled = build.DependenciesEnabled;
        current.Queue = build.Queue;
#if DEBUG
        current.ContentVersion++;
#endif
        CheckChildDeclaration(current);
        CaptureSiblingCounts(current);
        s_scopes!.Push(current);
        return new ParentScope(entity, new BuildScope(current, releaseOnExit: true));
    }

#if DEBUG
    internal static RebuildScope EnterRebuild()
    {
        var scope = new RebuildScope(s_childDeclarations);
        s_childDeclarations = new Dictionary<Entity, (ScopeData Data, int Version)>();
        return scope;
    }
#endif

    [Conditional("DEBUG")]
    private static void CheckChildDeclaration(ScopeData data)
    {
#if DEBUG
        if (s_childDeclarations!.TryGetValue(data.Parent, out var previous))
            DevelopmentCheck.Require(previous.Data == data && previous.Version == data.ContentVersion,
                $"The children of {data.Parent} were declared by two scopes in the same rebuild.");
        else
            s_childDeclarations.Add(data.Parent, (data, data.ContentVersion));
#endif
    }

    [Conditional("DEBUG")]
    private static void CaptureSiblingCounts(ScopeData current)
    {
#if DEBUG
        foreach (Entity child in Hierarchy<ElementTree>.GetChildren(current.World, current.Parent))
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
        var children = Hierarchy<ElementTree>.GetChildren(current.World, current.Parent);
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
        internal Entity Parent;
        internal ScopeData ViewData = null!;
        internal List<ScopeData>? ContentData;
        internal bool DeclaresChildren;
        internal bool DependenciesEnabled;
        internal BuildQueue Queue = null!;
        internal BuildTask Task;
        internal uint? ParameterVersion;
        internal bool Suspended;
        internal Dictionary<ulong, int> Occurrences { get; } = new();
        internal int ChildIndex;
        internal ulong? KeyPrefix;
#if DEBUG
        internal int ContentVersion;
        internal Dictionary<ulong, SiblingGroup> PreviousSiblings { get; } = new();
#endif
    }

    internal readonly struct BuildScope(ScopeData data, bool releaseOnExit) : IDisposable
    {
        internal void Finish(BuildTask task, uint? parameterVersion)
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
            if (data == data.ViewData)
                CompleteBuild(data);
            else if (!data.Suspended)
                CompleteChildren(data);
        }

        public void Dispose()
        {
            s_scopes!.Pop();
            if (releaseOnExit && !data.Suspended)
                Release(data);
        }
    }

#if DEBUG
    internal readonly struct RebuildScope : IDisposable
    {
        private readonly Dictionary<Entity, (ScopeData Data, int Version)>? _previousDeclarations;

        internal RebuildScope(Dictionary<Entity, (ScopeData Data, int Version)>? previousDeclarations) =>
            _previousDeclarations = previousDeclarations;

        public void Dispose() => s_childDeclarations = _previousDeclarations;
    }
#endif
}
