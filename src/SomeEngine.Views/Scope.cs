using System.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Views;

/// <summary>The thread's active view build, restored as nested child builds return.</summary>
public static partial class Scope
{
    [ThreadStatic]
    private static Stack<ScopeData>? s_scopes;
#if DEBUG
    [ThreadStatic]
    private static Dictionary<Entity, (Entity Entity, ScopeData Data, int Version, int BuildNumber)>? s_childDeclarations;
    [ThreadStatic]
    private static int s_buildNumber;
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

    internal static ulong Frame => s_scopes is { Count: > 0 } ? s_scopes.Peek().Frame : 0;

    /// <summary>Runs a view-owned calculation now and again when its reads change.</summary>
    public static void Compute(Action run)
    {
        DevelopmentCheck.Require(run is not null, "Scope.Compute requires a calculation.");
        Computation computation = AddComputation(run);
        computation.Execute(Current.Frame);
    }

    /// <summary>Creates a Memo whose result changes its version only when its value changes.</summary>
    public static Memo<T> Memo<T>(Func<T> compute)
    {
        DevelopmentCheck.Require(compute is not null, "Scope.Memo requires a calculation.");
        var memo = new Memo<T>(compute);
        memo.Refresh(Current.Frame);
        return memo;
    }

    internal static Computation AddComputation(Action run, bool evaluatesOnRead = false)
    {
        ScopeData current = Current;
        if (!current.World.HasSparse<Computations>(current.Entity))
            current.World.AddSparse(current.Entity, new Computations(new ComputationList()));
        ComputationList list = current.World.ReadSparse<Computations>(current.Entity).List;
        var computation = new Computation(current.World, current.Entity, current.Computation, run,
            current.Frame, evaluatesOnRead);
        list.Items.Add(computation);
        return computation;
    }

    /// <summary>Records an object source read by a Rerun build.</summary>
    /// <remarks>Calls outside a computation keep the source independent of any view.</remarks>
    public static void Depend(VersionCell cell)
    {
        if (s_scopes is not { Count: > 0 } || !s_scopes.Peek().DependenciesEnabled)
            return;
        DevelopmentCheck.Require(cell is not null, "Scope.Depend requires a version cell.");
        Depend(cell, Volatile.Read(ref cell.Version));
    }

    internal static void Depend(VersionCell cell, uint version)
    {
        if (s_scopes is not { Count: > 0 })
            return;
        ScopeData current = s_scopes.Peek();
        if (!current.DependenciesEnabled)
            return;

        if (current.Computation is { } computation)
        {
            computation.ObjectReads.Add(cell, version);
            return;
        }

        if (!current.World.Has<ObjectDependencies>(current.Entity))
            current.World.Add(current.Entity, new ObjectDependencies(new ObjectDependencyList(current.Frame)));
        current.World.Read<ObjectDependencies>(current.Entity).Reads.Add(cell, version);
    }

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

    /// <summary>Reads the nearest environment component on this view or its ElementTree ancestors.</summary>
    /// <remarks>Returns the default value when the component is absent. Consume stored references before structural changes.</remarks>
    public static ref readonly T ReadUp<T>() where T : struct
    {
        ScopeData current = Current;
        DevelopmentCheck.Require(default(T) is IComponent or ISparseComponent,
            $"Scope.ReadUp requires table or sparse component {typeof(T).Name}.");
        ushort component = ComponentId<T>.Value;
        Entity source = EnvironmentLookup.FindSource(current.World, current.Entity, component);
        if (current.DependenciesEnabled)
        {
            var dependency = new Dependency
            {
                Entity = source,
                Component = component,
                Version = source == Entity.Null ? 0 : current.World.ComponentDataWriteVersion<T>(source),
                Kind = DependencyKind.Environment,
            };
            RecordDependency(current, dependency);
        }
        if (source == Entity.Null)
            return ref EnvironmentDefault<T>.Value;
        return ref current.World.ReadComponentReference<T>(source);
    }

    /// <summary>Returns a component copy that is compared and committed when the build completes.</summary>
    /// <remarks>The reference remains valid through structural changes in this build.</remarks>
    public static ref T Write<T>(Entity entity) where T : struct
    {
        ScopeData current = Current;
        DevelopmentCheck.Require(default(T) is IComponent or ISparseComponent,
            $"Scope.Write requires table or sparse component {typeof(T).Name}.");
        var key = (entity, ComponentId<T>.Value);
        var copies = current.ViewData.ComponentCopies ??= new();
        if (!copies.TryGetValue(key, out ComponentCopy? copy))
        {
            T value = ComponentCopy<T>.HasComponent(current.World, entity)
                ? current.World.ReadComponentData<T>(entity)
                : default;
            copy = new ComponentCopy<T>(entity, value);
            copies.Add(key, copy);
        }
        return ref ((ComponentCopy<T>)copy).Value;
    }

    /// <summary>Reads a component dependency and returns its copy for writing, adding default data when required.</summary>
    public static ref T ReadWrite<T>(Entity entity) where T : struct
    {
        Ensure<T>(entity);
        Read<T>(entity);
        return ref Write<T>(entity);
    }

    /// <summary>Adds default component data when the entity first requires it.</summary>
    public static void Ensure<T>(Entity entity) where T : struct
    {
        World world = Current.World;
        DevelopmentCheck.Require(default(T) is IComponent or ISparseComponent,
            $"Scope.Ensure requires table or sparse component {typeof(T).Name}.");
        if (ComponentCopy<T>.HasComponent(world, entity))
            return;
        T value = default;
        if (default(T) is ISparseComponent)
            world.AddSparse(entity, in value);
        else
            world.Add(entity, in value);
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
        if (current.Computation is { } computation)
        {
            computation.Add(dependency);
            return;
        }
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

    private static class EnvironmentDefault<T> where T : struct
    {
        internal static readonly T Value;
    }

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
        DevelopmentCheck.Require(current.DeclaresChildren,
            "ChildSource.Caller builds write their own data; declare their children through Content.");
        if (current.KeyPrefix is ulong prefix)
            key = key == 0 ? prefix : CombineKeys(prefix, key);
        current.Occurrences.TryGetValue(key, out int occurrence);
        current.Occurrences[key] = occurrence + 1;
        var children = current.PreviousChildren;
        for (int index = 0; index < children.Count; index++)
        {
            Entity existing = children[index];
            ElementKey identity = current.World.Read<ElementKey>(existing);
            if (identity.Key != key || identity.Occurrence != occurrence)
                continue;

            if (current.World.Read<ViewType>(existing).Id != ViewRegistration<T>.Id)
                break;
            current.DeclaredChildren.Add(existing);
            T previous = current.World.ReadComponentData<T>(existing);
            current.Changes.RememberParameters(existing, in previous);
            if (previous.Equals(view))
            {
                current.World.OverwriteComponentData(existing, in view);
                return existing;
            }

            current.World.ReplaceComponentData(existing, in view);
            if (T.Mode == BuildMode.Rerun)
                ViewRegistration<T>.Build(current.World, existing, current.Frame, firstBuild: false);
            return existing;
        }
        Entity child = Views.Create(current.World, in view, new ElementKey { Key = key, Occurrence = occurrence });
        current.Changes.RememberAddedChild(child);
        current.DeclaredChildren.Add(child);
        Hierarchy<ElementTree>.SetParent(current.World, child, current.Parent);
        ViewRegistration<T>.Build(current.World, child, current.Frame, firstBuild: true);
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

    internal static BuildScope Enter(World world, Entity entity, ulong frame, bool declaresChildren, bool dependenciesEnabled)
    {
        if (world.HasSparse<Computations>(entity))
            world.ReadSparse<Computations>(entity).List.ReleaseChildren();
        if (world.HasBuffer<Dependency>(entity))
            world.ExecuteBufferWrite<Dependency>(entity, static buffer => buffer.Clear());
        if (world.Has<ObjectDependencies>(entity))
            world.Read<ObjectDependencies>(entity).Reads.Clear(frame);
        ScopeData current = s_reusableData is { } reusable && reusable.TryPop(out var available)
            ? available : new();
        current.World = world;
        current.Entity = entity;
        current.ViewData = current;
        current.Parent = entity;
        current.DeclaresChildren = declaresChildren;
        current.DependenciesEnabled = dependenciesEnabled;
        current.Queue = BuildQueue.For(world);
        ScopeData? parent = s_scopes is { Count: > 0 } ? s_scopes.Peek() : null;
        current.BeginChildren(frame, parent?.World == world ? parent.Changes : null);
#if DEBUG
        current.ContentVersion++;
        current.BuildNumber = ++s_buildNumber;
#endif
        if (declaresChildren)
        {
            CheckChildDeclaration(current);
            CaptureSiblingCounts(current);
        }
        (s_scopes ??= new Stack<ScopeData>()).Push(current);
        return new BuildScope(current, releaseOnExit: true);
    }

    internal static BuildScope Enter(Computation computation, ulong frame)
    {
        ScopeData current = s_reusableData is { } reusable && reusable.TryPop(out var available)
            ? available : new();
        current.World = computation.World;
        current.Entity = computation.Entity;
        current.Parent = computation.Entity;
        current.ViewData = current;
        current.Computation = computation;
        current.DeclaresChildren = false;
        current.DependenciesEnabled = true;
        current.Queue = BuildQueue.For(computation.World);
        ScopeData? parent = s_scopes is { Count: > 0 } ? s_scopes.Peek() : null;
        current.BeginChildren(frame, parent?.World == computation.World ? parent.Changes : null);
#if DEBUG
        current.BuildNumber = ++s_buildNumber;
#endif
        (s_scopes ??= new Stack<ScopeData>()).Push(current);
        return new BuildScope(current, releaseOnExit: true);
    }

    internal static BuildScope Resume(ScopeData data)
    {
        if (data.World.Has<ObjectDependencies>(data.Entity))
            data.World.Read<ObjectDependencies>(data.Entity).Reads.BeginFrame(data.Frame);
        if (data.DeclaresChildren)
            CheckChildDeclaration(data);
        (s_scopes ??= new Stack<ScopeData>()).Push(data);
        return new BuildScope(data, releaseOnExit: false);
    }

    internal static void Release(ScopeData data)
    {
        if (data == data.ViewData && data.StateMachines != 0)
            return;
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
        data.Computation = null;
        data.ComponentCopies?.Clear();
        data.ParameterVersion = null;
        data.Suspended = false;
        if (data.OutermostBuild)
            data.Changes.Clear();
        data.Changes = null!;
        data.PreviousChildren = default;
        data.DeclaredChildren.Clear();
        data.Completed = false;
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
        if (!view.OutermostBuild)
        {
            int firstData = view.FirstData;
            int firstRemoval = view.FirstRemoval;
            ChildChanges changes = view.OwnChanges ??= new();
            view.Changes.CopyTo(changes, firstData, firstRemoval);
            if (s_scopes is { } scopes)
                foreach (ScopeData scope in scopes)
                    if (scope.ViewData == view)
                        scope.ReplaceChanges(changes, firstData, firstRemoval);
            if (view.ContentData is { } content)
                foreach (ScopeData scope in content)
                    if (scope.Changes != changes)
                        scope.ReplaceChanges(changes, firstData, firstRemoval);
            view.OutermostBuild = true;
        }
    }

    internal static void CompleteBuild(ScopeData data)
    {
        if (data.ContentData is { } content)
            foreach (ScopeData declaration in content)
                CompleteChildren(declaration);
        CompleteChildren(data);
        CommitWrites(data);
        if (data.Computation is null && data.World.HasSparse<BuildError>(data.Entity))
            data.World.RemoveSparse<BuildError>(data.Entity);
    }

    private static void CompleteChildren(ScopeData data)
    {
        if (!data.DeclaresChildren)
        {
            data.Completed = true;
            return;
        }
        CheckSiblingCounts(data);
        var children = Hierarchy<ElementTree>.GetChildren(data.World, data.Parent);
        for (int index = 0; index < data.DeclaredChildren.Count; index++)
        {
            if (children[index] == data.DeclaredChildren[index])
                continue;
            Hierarchy<ElementTree>.SetParent(data.World, data.DeclaredChildren[index], data.Parent, index);
            children = Hierarchy<ElementTree>.GetChildren(data.World, data.Parent);
        }
        foreach (Entity child in data.PreviousChildren)
            if (!data.DeclaredChildren.Contains(child))
                data.Changes.RemoveChild(child);
        data.Completed = true;
    }

    internal static void CommitWrites(ScopeData data)
    {
        if (data.ComponentCopies is { } copies)
        {
            foreach (ComponentCopy copy in copies.Values)
                copy.Commit(data.World);
            copies.Clear();
        }
    }

    internal static void FinishChildren(ScopeData data)
    {
        if (!data.Completed)
            data.Changes.Restore(data.World, data.FirstData, data.FirstRemoval);
        else if (data.OutermostBuild)
            data.Changes.ReleaseChildren(data.World);
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
        current.Computation = build.Computation;
        current.Parent = entity;
        current.DeclaresChildren = true;
        current.DependenciesEnabled = build.DependenciesEnabled;
        current.Queue = build.Queue;
        current.BeginChildren(build.Frame, build.Changes);
#if DEBUG
        current.ContentVersion++;
        current.BuildNumber = build.ViewData.BuildNumber;
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
        s_childDeclarations = new();
        return scope;
    }
#endif

    [Conditional("DEBUG")]
    private static void CheckChildDeclaration(ScopeData data)
    {
#if DEBUG
        if (s_childDeclarations!.TryGetValue(data.Parent, out var previous))
            DevelopmentCheck.Require(previous.Data == data && previous.Version == data.ContentVersion ||
                previous.Entity == data.Entity && previous.BuildNumber != data.ViewData.BuildNumber,
                $"The children of {data.Parent} were declared by two scopes in the same rebuild.");
        s_childDeclarations[data.Parent] = (data.Entity, data, data.ContentVersion, data.ViewData.BuildNumber);
#endif
    }

    [Conditional("DEBUG")]
    private static void CaptureSiblingCounts(ScopeData current)
    {
#if DEBUG
        foreach (Entity child in current.PreviousChildren)
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
        var children = current.DeclaredChildren;
        foreach (var (key, previous) in current.PreviousSiblings)
        {
            current.Occurrences.TryGetValue(key, out int count);
            if (count == previous.Count || Math.Max(count, previous.Count) <= 1)
                continue;

            bool localState = previous.LocalState;
            for (int index = 0; !localState && index < children.Count; index++)
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
        internal Computation? Computation;
        internal Dictionary<(Entity Entity, ushort Component), ComponentCopy>? ComponentCopies;
        internal ulong Frame;
        internal HierarchyChildrenSnapshot<ElementTree> PreviousChildren;
        internal List<Entity> DeclaredChildren { get; } = new();
        internal ChildChanges Changes = null!;
        internal ChildChanges? OwnChanges;
        internal int FirstData;
        internal int FirstRemoval;
        internal bool OutermostBuild;
        internal bool Completed;
        internal int StateMachines;
        internal bool DeclaresChildren;
        internal bool DependenciesEnabled;
        internal BuildQueue Queue = null!;
        internal BuildTask Task;
        internal uint? ParameterVersion;
        internal bool Suspended;
        internal Dictionary<ulong, int> Occurrences { get; } = new();
        internal Dictionary<ulong, int>? RememberOccurrences;
        internal ulong? KeyPrefix;
#if DEBUG
        internal int ContentVersion;
        internal int BuildNumber;
        internal Dictionary<ulong, SiblingGroup> PreviousSiblings { get; } = new();
#endif

        internal void BeginChildren(ulong frame, ChildChanges? changes)
        {
            Frame = frame;
            PreviousChildren = DeclaresChildren ? Hierarchy<ElementTree>.GetChildren(World, Parent) : default;
            OutermostBuild = changes is null;
            Changes = changes ?? (OwnChanges ??= new());
            FirstData = Changes.DataCount;
            FirstRemoval = Changes.RemovedCount;
            if (DeclaresChildren)
                Changes.RememberOrder(Parent, PreviousChildren);
        }

        internal void ReplaceChanges(ChildChanges changes, int firstData, int firstRemoval)
        {
            Changes = changes;
            FirstData -= firstData;
            FirstRemoval -= firstRemoval;
        }
    }

    internal readonly struct BuildScope(ScopeData data, bool releaseOnExit) : IDisposable
    {
        internal bool IsSuspended => data.Suspended;
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
                CommitWrites(data);
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
            {
                FinishChildren(data);
                Release(data);
            }
        }
    }

#if DEBUG
    internal readonly struct RebuildScope : IDisposable
    {
        private readonly Dictionary<Entity, (Entity Entity, ScopeData Data, int Version, int BuildNumber)>? _previousDeclarations;

        internal RebuildScope(Dictionary<Entity, (Entity Entity, ScopeData Data, int Version, int BuildNumber)>? previousDeclarations) =>
            _previousDeclarations = previousDeclarations;

        public void Dispose() => s_childDeclarations = _previousDeclarations;
    }
#endif
}
