using System.Buffers;
using System.Collections.Generic;
using SomeEngine.ECS;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Hierarchy;

internal static partial class Hierarchy
{
    private const int MissingParentMask = 1;
    private const int MissingDepthMask = 2;
    private const int MissingLinkMask = 4;

    private readonly record struct ParentTransition(
        Entity Child,
        Entity PreviousParent,
        Entity CurrentParent,
        int PreviousIndex,
        Archetype ChildArchetype,
        Chunk ChildChunk,
        int ChildRow,
        bool HadParent,
        bool HadDepth,
        bool HadLink,
        bool HadChildBuffer,
        bool IsPendingCleanup);

    private readonly record struct ParentChildMutation(Entity Parent, Entity Child);

    private struct ChildShapeMutation
    {
        public ChildShapeMutation(
            Archetype sourceArchetype,
            Chunk sourceChunk,
            int missingMask,
            Entity parent,
            byte depth,
            int count)
        {
            SourceArchetype = sourceArchetype;
            SourceChunk = sourceChunk;
            MissingMask = missingMask;
            Parent = parent;
            Depth = depth;
            Count = count;
        }

        public Archetype SourceArchetype { get; }

        public Chunk SourceChunk { get; }

        public int MissingMask { get; }

        public Entity Parent { get; }

        public byte Depth { get; }

        public int Count;
    }

    private readonly record struct HierarchyComponentIds(
        int Parent,
        int Depth,
        int ChildBuffer,
        int Link)
    {
        public static HierarchyComponentIds Create()
        {
            return new HierarchyComponentIds(
                ComponentMetadata<Parent>.Id,
                ComponentMetadata<Depth>.Id,
                ComponentMetadata<ChildBuffer>.Id,
                ComponentMetadata<HierarchyLink>.Id);
        }
    }

    private struct ParentNormalizationCache
    {
        private Entity _lastParent;
        private Entity _lastDesiredParent;
        private bool _hasLastParent;

        public Entity Normalize(World world, Entity parent)
        {
            if (parent == Entity.Null)
                return Entity.Null;

            if (_hasLastParent && parent == _lastParent)
                return _lastDesiredParent;

            _lastParent = parent;
            _lastDesiredParent = NormalizeParent(world, parent);
            _hasLastParent = true;
            return _lastDesiredParent;
        }

        private static Entity NormalizeParent(World world, Entity parent)
        {
            if (parent == Entity.Null)
                return Entity.Null;

            if (!world.IsAlive(parent) || world.IsPendingCleanup(parent))
                return Entity.Null;

            return parent;
        }
    }

    private readonly struct HierarchyScanColumns
    {
        private readonly bool _scanParentChanges;
        private readonly bool _scanLinkWithoutParent;

        private HierarchyScanColumns(
            bool hasParent,
            int parentColumn,
            bool hasDepth,
            bool hasChildBuffer,
            bool hasLink,
            int linkColumn,
            bool scanParentChanges,
            bool scanLinkWithoutParent,
            bool scanCleanupChildCache,
            bool isPendingCleanup)
        {
            HasParent = hasParent;
            ParentColumn = parentColumn;
            HasDepth = hasDepth;
            HasChildBuffer = hasChildBuffer;
            HasLink = hasLink;
            LinkColumn = linkColumn;
            _scanParentChanges = scanParentChanges;
            _scanLinkWithoutParent = scanLinkWithoutParent;
            ScanCleanupChildCache = scanCleanupChildCache;
            IsPendingCleanup = isPendingCleanup;
        }

        public bool HasParent { get; }

        public int ParentColumn { get; }

        public bool HasDepth { get; }

        public bool HasChildBuffer { get; }

        public bool HasLink { get; }

        public int LinkColumn { get; }

        public bool ScanCleanupChildCache { get; }

        public bool IsPendingCleanup { get; }

        public bool HasRelevantShape => HasParent || HasChildBuffer || HasLink;

        public static HierarchyScanColumns Resolve(
            Archetype archetype,
            HierarchyComponentIds ids,
            bool useDirtyParentEntities)
        {
            bool hasParent = archetype.TryColumn(ids.Parent, out int parentColumn);
            bool hasDepth = archetype.HasComponent(ids.Depth);
            bool hasChildBuffer = archetype.HasComponent(ids.ChildBuffer);
            bool hasLink = archetype.TryColumn(ids.Link, out int linkColumn);
            bool cleanupOnly = IsCleanupOnly(archetype);
            return new HierarchyScanColumns(
                hasParent,
                parentColumn,
                hasDepth,
                hasChildBuffer,
                hasLink,
                linkColumn,
                !useDirtyParentEntities && hasParent,
                hasLink && !hasParent && !useDirtyParentEntities,
                hasChildBuffer && cleanupOnly,
                cleanupOnly);
        }

        public bool ShouldScan(Chunk chunk, uint lastUpdateVersion)
        {
            return ParentChanged(chunk, lastUpdateVersion) ||
                   _scanLinkWithoutParent ||
                   ScanCleanupChildCache;
        }

        public bool NeedsTransition(Entity previousParent, Entity desiredParent)
        {
            return IsPendingCleanup ||
                   previousParent != desiredParent ||
                   !HasLink ||
                   (HasParent && desiredParent == Entity.Null);
        }

        private bool ParentChanged(Chunk chunk, uint lastUpdateVersion)
        {
            return _scanParentChanges &&
                   VersionClock.IsNewer(chunk.ChangeVersions[ParentColumn], lastUpdateVersion);
        }

        private static bool IsCleanupOnly(Archetype archetype)
        {
            return archetype.HasCleanupComponents &&
                   archetype.CleanupComponentIds.Length == archetype.ComponentIds.Length;
        }
    }

    private sealed class ParentMutationComparer : IComparer<ParentChildMutation>
    {
        public static readonly ParentMutationComparer Instance = new();

        public int Compare(ParentChildMutation x, ParentChildMutation y)
        {
            int parentCompare = CompareEntities(x.Parent, y.Parent);
            return parentCompare != 0 ? parentCompare : CompareEntities(x.Child, y.Child);
        }
    }

    internal delegate void AttachChild(
        World world,
        Entity parent,
        Entity child,
        int? insertIndex
    );

    internal delegate void DetachChild(World world, Entity parent, Entity child);

    internal static void ValidateParent(World world, Entity child, Entity parent)
    {
        ParentRules.ValidateDirect(world, child, parent);

        if (parent == Entity.Null)
            throw new InvalidOperationException("Parent cannot be Entity.Null. Use Detach.");
    }

    internal static void AddHierarchyShape(World world, Entity child, Entity parent)
    {
        Shape.AddParentShape(world, parent);
        Shape.AddChildShape(world, child);
    }

    private static class ParentRules
    {
        public static void ValidateDirect(World world, Entity child, Entity parent)
        {
            world.ThrowIfIterating();
            world.ThrowIfDead(child);

            if (world.IsPendingCleanup(child))
                throw new InvalidOperationException("Cannot mutate hierarchy parent on a pending-cleanup entity.");

            if (parent == Entity.Null)
                return;

            world.ThrowIfDead(parent);

            if (world.IsPendingCleanup(parent))
                throw new InvalidOperationException("Cannot parent an entity to a pending-cleanup entity.");

            if (child == parent)
                throw new InvalidOperationException("Entity cannot be its own parent.");

            if (WouldCycle(world, child, parent))
                throw new InvalidOperationException(
                    "This parent would create a hierarchy cycle."
                );
        }

        public static bool WouldCycle(World world, Entity child, Entity candidateParent)
        {
            var current = candidateParent;
            int remaining = world.EntityCount + 1;
            while (current != Entity.Null && world.IsAlive(current))
            {
                if (current == child)
                    return true;

                if (remaining-- <= 0)
                    throw new InvalidOperationException("Hierarchy parent chain is corrupted.");

                current = ReadStoredParent(world, current);
            }

            return false;
        }
    }

    private static class Shape
    {
        public static void AddParentShape(World world, Entity parent)
        {
            bool parentHadChildBuffer = world.Has<ChildBuffer>(parent);
            bool parentHadDepth = world.Has<Depth>(parent);
            if (!parentHadChildBuffer || !parentHadDepth)
            {
                Span<int> parentComponentIds = stackalloc int[
                    (parentHadDepth ? 0 : 1) +
                    (parentHadChildBuffer ? 0 : 1)];

                int index = 0;
                if (!parentHadDepth)
                    parentComponentIds[index++] = ComponentMetadata<Depth>.Id;
                if (!parentHadChildBuffer)
                    parentComponentIds[index++] = ComponentMetadata<ChildBuffer>.Id;

                var parentContext = world.CreateAddWriter(parent, parentComponentIds);
                if (!parentHadChildBuffer)
                    parentContext.Write(new ChildBuffer());
                if (!parentHadDepth)
                    parentContext.Write(new Depth { Value = 0 });
            }
        }

        public static void AddChildShape(World world, Entity child)
        {
            AddChildShape(
                world,
                child,
                world.Has<Parent>(child),
                world.Has<Depth>(child),
                world.Has<HierarchyLink>(child));
        }

        public static void AddChildShape(
            World world,
            Entity child,
            bool childHadParent,
            bool childHadDepth,
            bool childHadLink)
        {
            bool hasParent = childHadParent || world.Has<Parent>(child);
            bool hasDepth = childHadDepth || world.Has<Depth>(child);
            bool hasLink = childHadLink || world.Has<HierarchyLink>(child);
            if (hasParent && hasDepth && hasLink)
                return;

            Span<int> childComponentIds = stackalloc int[
                (hasParent ? 0 : 1) +
                (hasDepth ? 0 : 1) +
                (hasLink ? 0 : 1)];
            int count = FillChildShapeIds(childComponentIds, hasParent, hasDepth, hasLink);

            var childContext = world.CreateAddWriter(child, childComponentIds[..count]);
            WriteChildShape(childContext, hasParent, hasDepth, hasLink);
        }

        private static int FillChildShapeIds(
            Span<int> componentIds,
            bool hasParent,
            bool hasDepth,
            bool hasLink)
        {
            int count = 0;
            if (!hasParent)
                componentIds[count++] = ComponentMetadata<Parent>.Id;
            if (!hasDepth)
                componentIds[count++] = ComponentMetadata<Depth>.Id;
            if (!hasLink)
                componentIds[count++] = ComponentMetadata<HierarchyLink>.Id;

            return count;
        }

        private static void WriteChildShape(
            BundleWriter childContext,
            bool hasParent,
            bool hasDepth,
            bool hasLink)
        {
            if (!hasParent)
                childContext.Write(new Parent());
            if (!hasDepth)
                childContext.Write(new Depth { Value = 0 });
            if (!hasLink)
                childContext.Write(new HierarchyLink { ChildIndex = -1 });
        }
    }

    internal static Entity ReadStoredParent(World world, Entity child)
    {
        return world.Has<Parent>(child) ? world.Read<Parent>(child).Value : Entity.Null;
    }

    private static int ReadIndex(World world, Entity child, Entity parent)
    {
        if (parent == Entity.Null ||
            !world.IsAlive(child) ||
            !world.Has<HierarchyLink>(child))
        {
            return -1;
        }

        var link = world.Read<HierarchyLink>(child);
        return link.Parent == parent ? link.ChildIndex : -1;
    }

    private static void RefreshIndexes(World world, Entity parent)
    {
        if (parent == Entity.Null ||
            !world.IsAlive(parent) ||
            !world.Has<ChildBuffer>(parent))
        {
            return;
        }

        ref readonly var childBuffer = ref world.ReadRef<ChildBuffer>(parent);
        var children = childBuffer.Children.ReadSpan();
        for (int index = 0; index < children.Length; index++)
        {
            var child = children[index];
            if (!world.IsAlive(child) || world.IsPendingCleanup(child))
                continue;

            if (!world.Has<HierarchyLink>(child))
            {
                WriteLink(world, child, parent);
                continue;
            }

            var link = world.Read<HierarchyLink>(child);
            if (link.Parent == parent && link.ChildIndex == index)
                continue;

            world.Replace(child, new HierarchyLink { Parent = parent, ChildIndex = index });
        }
    }

    private static void WriteChange(
        World world,
        Entity child,
        Entity oldParent,
        Entity newParent,
        int oldIndex)
    {
        int newIndex = ReadIndex(world, child, newParent);

        HierarchyChangeKind kind;
        if (oldParent == newParent)
        {
            if (oldParent == Entity.Null || oldIndex == newIndex)
                return;

            kind = HierarchyChangeKind.Reordered;
        }
        else if (oldParent == Entity.Null)
        {
            kind = HierarchyChangeKind.Added;
        }
        else if (newParent == Entity.Null)
        {
            kind = HierarchyChangeKind.Removed;
        }
        else
        {
            kind = HierarchyChangeKind.Changed;
        }

        world.Hierarchy.WriteChange(
            kind,
            child,
            oldParent,
            newParent,
            oldIndex,
            newIndex);
    }

    internal static void AttachParent(
        World world,
        Entity child,
        Entity parent,
        int? insertIndex,
        AttachChild attach,
        DetachChild detach
    )
    {
        ValidateParent(world, child, parent);
        var currentParent = ReadStoredParent(world, child);
        if (currentParent != Entity.Null)
            throw new InvalidOperationException("Child already has a parent. Use Move.");

        WriteParent(world, child, parent, currentParent, -1, insertIndex, attach, detach);
    }

    internal static void MoveParent(
        World world,
        Entity child,
        Entity parent,
        int? insertIndex,
        AttachChild attach,
        DetachChild detach
    )
    {
        ValidateParent(world, child, parent);
        var currentParent = ReadStoredParent(world, child);
        if (currentParent == Entity.Null)
            throw new InvalidOperationException("Child has no parent. Use Attach.");
        if (currentParent == parent)
        {
            if (insertIndex is null)
                return;

            throw new InvalidOperationException("Child already belongs to this parent. Use Reorder.");
        }

        int oldIndex = ReadIndex(world, child, currentParent);
        WriteParent(world, child, parent, currentParent, oldIndex, insertIndex, attach, detach);
    }

    internal static void ReorderChild(
        World world,
        Entity child,
        int insertIndex,
        AttachChild attach,
        DetachChild detach
    )
    {
        ParentRules.ValidateDirect(world, child, Entity.Null);
        var parent = ReadStoredParent(world, child);
        if (parent == Entity.Null)
            throw new InvalidOperationException("Child has no parent to reorder.");
        if (!world.IsAlive(parent) || world.IsPendingCleanup(parent))
            throw new InvalidOperationException("Child parent is not live.");

        world.Hierarchy.BeginEdit();
        try
        {
            int oldIndex = ReadIndex(world, child, parent);
            detach(world, parent, child);
            attach(world, parent, child, insertIndex);
            RefreshIndexes(world, parent);
            WriteChange(world, child, parent, parent, oldIndex);
        }
        finally
        {
            world.Hierarchy.EndEdit();
        }
    }

    internal static void DetachParent(World world, Entity child, DetachChild detach)
    {
        world.ThrowIfIterating();
        world.ThrowIfDead(child);

        world.Hierarchy.BeginEdit();
        try
        {
            var parent = ReadStoredParent(world, child);
            if (parent == Entity.Null)
                return;

            int oldIndex = ReadIndex(world, child, parent);
            if (world.IsAlive(parent))
            {
                detach(world, parent, child);
                RefreshIndexes(world, parent);
            }

            world.Remove<Parent>(child);
            WriteDepthTree(world, child, 0);
            WriteLink(world, child, Entity.Null);
            WriteChange(world, child, parent, Entity.Null, oldIndex);
        }
        finally
        {
            world.Hierarchy.EndEdit();
        }
    }

    private static void WriteParent(
        World world,
        Entity child,
        Entity parent,
        Entity oldParent,
        int oldIndex,
        int? insertIndex,
        AttachChild attach,
        DetachChild detach)
    {
        world.Hierarchy.BeginEdit();
        try
        {
            AddHierarchyShape(world, child, parent);

            if (oldParent != Entity.Null && world.IsAlive(oldParent))
            {
                detach(world, oldParent, child);
                RefreshIndexes(world, oldParent);
            }

            world.Replace(child, new Parent { Value = parent });
            attach(world, parent, child, insertIndex);
            WriteDepthTree(world, child, IncrementDepthFor(parent, world));
            WriteLink(world, child, parent);
            RefreshIndexes(world, parent);
            WriteChange(world, child, oldParent, parent, oldIndex);
        }
        finally
        {
            world.Hierarchy.EndEdit();
        }
    }

    internal static Entity GetParent(World world, Entity child)
    {
        world.ThrowIfDead(child);
        var parent = ReadStoredParent(world, child);
        return world.IsAlive(parent) && !world.IsPendingCleanup(parent) ? parent : Entity.Null;
    }

    internal static ReadOnlySpan<Entity> GetChildren(World world, Entity parent)
    {
        world.ThrowIfDead(parent);
        if (world.IsPendingCleanup(parent))
            return ReadOnlySpan<Entity>.Empty;
        if (!world.Has<ChildBuffer>(parent))
            return ReadOnlySpan<Entity>.Empty;

        world.Hierarchy.BeginEdit();
        try
        {
            ref readonly var childBuffer = ref world.ReadRef<ChildBuffer>(parent);
            return childBuffer.Children.ReadSpan();
        }
        finally
        {
            world.Hierarchy.EndEdit();
        }
    }

    internal static void DestroySubtree(World world, Entity root, DetachChild detach)
    {
        world.ThrowIfIterating();
        world.ThrowIfDead(root);

        world.Hierarchy.BeginEdit();
        try
        {
            var buffer = ArrayPool<Entity>.Shared.Rent(4);
            try
            {
                int bufferCount = 1;
                buffer[0] = root;
                CollectSubtree(world, ref buffer, ref bufferCount);

                for (int index = bufferCount - 1; index >= 0; index--)
                {
                    var entity = buffer[index];
                    if (!world.IsAlive(entity))
                        continue;

                    var parent = ReadStoredParent(world, entity);
                    if (parent != Entity.Null && world.IsAlive(parent))
                        detach(world, parent, entity);

                    world.DestroyEntityImmediate(entity);
                }
            }
            finally
            {
                ArrayPool<Entity>.Shared.Return(buffer);
            }
        }
        finally
        {
            world.Hierarchy.EndEdit();
        }
    }

    private interface TransitionTrait
    {
        void Commit(World world, ReadOnlySpan<ParentTransition> transitions);
    }

    private readonly struct OrderedTrait : TransitionTrait
    {
        private readonly AttachChild _attach;
        private readonly DetachChild _detach;

        public OrderedTrait(AttachChild attach, DetachChild detach)
        {
            _attach = attach;
            _detach = detach;
        }

        public void Commit(World world, ReadOnlySpan<ParentTransition> transitions)
        {
            CommitParents(world, transitions, _attach, _detach);
        }
    }

    private readonly struct UnorderedTrait : TransitionTrait
    {
        public void Commit(World world, ReadOnlySpan<ParentTransition> transitions)
        {
            TransitionWriter.WriteUnordered(world, transitions);
        }
    }

    internal static void Update(
        World world,
        AttachChild attach,
        DetachChild detach)
    {
        Update(world, new OrderedTrait(attach, detach));
    }

    internal static void UpdateUnordered(World world)
    {
        Update(world, new UnorderedTrait());
    }

    private static void Update<HierarchyTrait>(
        World world,
        HierarchyTrait trait)
        where HierarchyTrait : struct, TransitionTrait
    {
        world.ThrowIfIterating();

        uint lastUpdateVersion = world.Hierarchy.LastTick;
        uint updateVersion = world.Hierarchy.AcquireTick();
        ParentTransition[]? transitions = null;
        int transitionCount = 0;
        Entity[]? cleanupParents = null;
        int cleanupParentCount = 0;
        bool completed = false;

        try
        {
            CollectHierarchyUpdates(
                world,
                lastUpdateVersion,
                ref transitions,
                ref transitionCount,
                ref cleanupParents,
                ref cleanupParentCount);
            if (transitionCount == 0 && cleanupParentCount == 0)
            {
                completed = true;
                return;
            }

            world.Hierarchy.BeginEdit();
            try
            {
                var transitionSpan = transitionCount == 0
                    ? ReadOnlySpan<ParentTransition>.Empty
                    : transitions!.AsSpan(0, transitionCount);
                ValidateParentTransitions(world, transitionSpan);
                trait.Commit(world, transitionSpan);
                CleanupCaches.Process(
                    world,
                    cleanupParentCount == 0
                        ? ReadOnlySpan<Entity>.Empty
                        : cleanupParents!.AsSpan(0, cleanupParentCount));
                completed = true;
            }
            finally
            {
                world.Hierarchy.EndEdit();
            }
        }
        finally
        {
            if (completed)
                world.Hierarchy.CommitTick(updateVersion);

            if (transitions is not null)
                ArrayPool<ParentTransition>.Shared.Return(transitions);

            if (cleanupParents is not null)
                ArrayPool<Entity>.Shared.Return(cleanupParents);
        }
    }

}

internal static partial class Hierarchy
{
    private static void CollectHierarchyUpdates(
        World world,
        uint lastUpdateVersion,
        ref ParentTransition[]? transitions,
        ref int transitionCount,
        ref Entity[]? cleanupParents,
        ref int cleanupParentCount)
    {
        var ids = HierarchyComponentIds.Create();
        var parentCache = new ParentNormalizationCache();
        bool useDirtyParentEntities = world.Hierarchy.ShouldCollectDirty;

        if (useDirtyParentEntities)
        {
            UpdateCollector.CollectDirty(
                world,
                ids,
                ref parentCache,
                ref transitions,
                ref transitionCount);
        }

        ScanHierarchyTables(
            world,
            lastUpdateVersion,
            ids,
            useDirtyParentEntities,
            ref parentCache,
            ref transitions,
            ref transitionCount,
            ref cleanupParents,
            ref cleanupParentCount);
    }

    private static void ScanHierarchyTables(
        World world,
        uint lastUpdateVersion,
        HierarchyComponentIds ids,
        bool useDirtyParentEntities,
        ref ParentNormalizationCache parentCache,
        ref ParentTransition[]? transitions,
        ref int transitionCount,
        ref Entity[]? cleanupParents,
        ref int cleanupParentCount)
    {
        var archetypes = world.AllArchetypes;
        for (int archetypeIndex = 0; archetypeIndex < archetypes.Count; archetypeIndex++)
        {
            ScanArchetype(
                world,
                archetypes[archetypeIndex],
                lastUpdateVersion,
                ids,
                useDirtyParentEntities,
                ref parentCache,
                ref transitions,
                ref transitionCount,
                ref cleanupParents,
                ref cleanupParentCount);
        }
    }

    private static void ScanArchetype(
        World world,
        Archetype archetype,
        uint lastUpdateVersion,
        HierarchyComponentIds ids,
        bool useDirtyParentEntities,
        ref ParentNormalizationCache parentCache,
        ref ParentTransition[]? transitions,
        ref int transitionCount,
        ref Entity[]? cleanupParents,
        ref int cleanupParentCount)
    {
        var columns = HierarchyScanColumns.Resolve(archetype, ids, useDirtyParentEntities);
        if (!columns.HasRelevantShape)
            return;

        var chunks = archetype.Chunks;
        for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
        {
            var chunk = chunks[chunkIndex];
            if (!columns.ShouldScan(chunk, lastUpdateVersion))
                continue;

            ScanChunkRows(
                world,
                archetype,
                chunk,
                columns,
                ref parentCache,
                ref transitions,
                ref transitionCount,
                ref cleanupParents,
                ref cleanupParentCount);
        }
    }

    private static void ScanChunkRows(
        World world,
        Archetype archetype,
        Chunk chunk,
        HierarchyScanColumns columns,
        ref ParentNormalizationCache parentCache,
        ref ParentTransition[]? transitions,
        ref int transitionCount,
        ref Entity[]? cleanupParents,
        ref int cleanupParentCount)
    {
        Parent[]? parents = columns.HasParent ? (Parent[])chunk.Columns[columns.ParentColumn] : null;
        HierarchyLink[]? links = columns.HasLink ? (HierarchyLink[])chunk.Columns[columns.LinkColumn] : null;
        for (int row = 0; row < chunk.Count; row++)
        {
            if (!TryCreateScannedTransition(
                    world,
                    archetype,
                    chunk,
                    row,
                    columns,
                    parents,
                    links,
                    ref parentCache,
                    ref cleanupParents,
                    ref cleanupParentCount,
                    out var transition))
            {
                continue;
            }

            AddParentTransition(ref transitions, ref transitionCount, transition);
        }
    }

    private static bool TryCreateScannedTransition(
        World world,
        Archetype archetype,
        Chunk chunk,
        int row,
        HierarchyScanColumns columns,
        Parent[]? parents,
        HierarchyLink[]? links,
        ref ParentNormalizationCache parentCache,
        ref Entity[]? cleanupParents,
        ref int cleanupParentCount,
        out ParentTransition transition)
    {
        var entity = chunk.Entities[row];
        if (columns.ScanCleanupChildCache)
            AddCleanupParent(ref cleanupParents, ref cleanupParentCount, entity);

        transition = default;
        if (!columns.HasParent && !columns.HasLink)
            return false;

        if (!world.IsAlive(entity))
            return false;

        var previousParent = links is null ? Entity.Null : links[row].Parent;
        var currentParent = parents is null ? Entity.Null : parents[row].Value;
        var desiredParent = columns.IsPendingCleanup
            ? Entity.Null
            : parentCache.Normalize(world, currentParent);

        if (!columns.NeedsTransition(previousParent, desiredParent))
            return false;

        transition = new ParentTransition(
            entity,
            previousParent,
            desiredParent,
            links is null ? -1 : links[row].ChildIndex,
            archetype,
            chunk,
            row,
            columns.HasParent,
            columns.HasDepth,
            columns.HasLink,
            columns.HasChildBuffer,
            columns.IsPendingCleanup);
        return true;
    }

    private static class UpdateCollector
    {
        public static void CollectDirty(
            World world,
            HierarchyComponentIds ids,
            ref ParentNormalizationCache parentCache,
            ref ParentTransition[]? transitions,
            ref int transitionCount)
        {
            var dirtyEntities = world.Hierarchy.DirtyEntities;
            for (int i = 0; i < dirtyEntities.Length; i++)
            {
                if (!TryCreateDirtyTransition(
                        world,
                        dirtyEntities[i],
                        ids,
                        ref parentCache,
                        out var transition))
                {
                    continue;
                }

                AddParentTransition(ref transitions, ref transitionCount, transition);
            }
        }

        private static bool TryCreateDirtyTransition(
            World world,
            Entity entity,
            HierarchyComponentIds ids,
            ref ParentNormalizationCache parentCache,
            out ParentTransition transition)
        {
            transition = default;
            if (!world.Hierarchy.TryDirtyLocation(entity, out var archetype, out var chunk, out int row))
                return false;

            var columns = HierarchyScanColumns.Resolve(archetype, ids, useDirtyParentEntities: true);
            if (!CanUseDirtyColumns(columns))
                return false;

            Parent[]? parents = columns.HasParent ? (Parent[])chunk.Columns[columns.ParentColumn] : null;
            HierarchyLink[]? links = columns.HasLink ? (HierarchyLink[])chunk.Columns[columns.LinkColumn] : null;
            return TryBuildDirtyTransition(
                world,
                entity,
                archetype,
                chunk,
                row,
                columns,
                parents,
                links,
                ref parentCache,
                out transition);
        }

        private static bool CanUseDirtyColumns(HierarchyScanColumns columns)
        {
            return (columns.HasParent || columns.HasLink) &&
                   (!columns.IsPendingCleanup || !columns.HasChildBuffer);
        }

        private static bool TryBuildDirtyTransition(
            World world,
            Entity entity,
            Archetype archetype,
            Chunk chunk,
            int row,
            HierarchyScanColumns columns,
            Parent[]? parents,
            HierarchyLink[]? links,
            ref ParentNormalizationCache parentCache,
            out ParentTransition transition)
        {
            var previousParent = links is null ? Entity.Null : links[row].Parent;
            var currentParent = parents is null ? Entity.Null : parents[row].Value;
            var desiredParent = columns.IsPendingCleanup
                ? Entity.Null
                : parentCache.Normalize(world, currentParent);

            if (!columns.NeedsTransition(previousParent, desiredParent))
            {
                transition = default;
                return false;
            }

            transition = new ParentTransition(
                entity,
                previousParent,
                desiredParent,
                links is null ? -1 : links[row].ChildIndex,
                archetype,
                chunk,
                row,
                columns.HasParent,
                columns.HasDepth,
                columns.HasLink,
                columns.HasChildBuffer,
                columns.IsPendingCleanup);
            return true;
        }
    }

    private static void ValidateParentTransitions(
        World world,
        ReadOnlySpan<ParentTransition> transitions)
    {
        Entity lastParent = Entity.Null;
        bool hasLastParent = false;
        bool lastParentHasParent = false;

        for (int i = 0; i < transitions.Length; i++)
        {
            var transition = transitions[i];
            var parent = transition.CurrentParent;
            if (parent == Entity.Null)
                continue;

            world.ThrowIfDead(transition.Child);

            if (transition.Child == parent)
                throw new InvalidOperationException("Entity cannot be its own parent.");

            if (!hasLastParent || parent != lastParent)
            {
                world.ThrowIfDead(parent);

                if (world.IsPendingCleanup(parent))
                    throw new InvalidOperationException("Cannot parent an entity to a pending-cleanup entity.");

                lastParent = parent;
                lastParentHasParent = world.Has<Parent>(parent);
                hasLastParent = true;
            }

            if (lastParentHasParent && ParentRules.WouldCycle(world, transition.Child, parent))
                throw new InvalidOperationException(
                    "Setting this parent would create a hierarchy cycle."
                );
        }
    }

    private static void CommitParents(
        World world,
        ReadOnlySpan<ParentTransition> transitions,
        AttachChild attach,
        DetachChild detach)
    {
        TransitionWriter.AddChildShapes(world, transitions);
        DetachPreviousParents(world, transitions, detach);
        AttachCurrentParents(world, transitions, attach);
        RefreshTransitionParents(world, transitions);
        TransitionFacts.Write(world, transitions);
        CommitParentLinks(world, transitions);
        WriteChanges(world, transitions);
    }

    private static void RefreshTransitionParents(
        World world,
        ReadOnlySpan<ParentTransition> transitions)
    {
        for (int i = 0; i < transitions.Length; i++)
        {
            var transition = transitions[i];
            RefreshIndexes(world, transition.PreviousParent);
            RefreshIndexes(world, transition.CurrentParent);
        }
    }

    private static void WriteChanges(
        World world,
        ReadOnlySpan<ParentTransition> transitions)
    {
        for (int i = 0; i < transitions.Length; i++)
        {
            var transition = transitions[i];
            if (!world.IsAlive(transition.Child))
                continue;

            var newParent = transition.IsPendingCleanup
                ? Entity.Null
                : transition.CurrentParent;
            WriteChange(
                world,
                transition.Child,
                transition.PreviousParent,
                newParent,
                transition.PreviousIndex);
        }
    }

    private static class TransitionWriter
    {
        public static void WriteUnordered(
            World world,
            ReadOnlySpan<ParentTransition> transitions)
        {
            bool childrenReady = AddChildShapes(world, transitions);
            if (TryLeafAdds(world, transitions, childrenReady))
            {
                WriteChanges(world, transitions);
                return;
            }

            WriteBuffers(world, transitions);
            TransitionFacts.Write(world, transitions);
            CommitParentLinks(world, transitions);
            WriteChanges(world, transitions);
        }

        public static bool AddChildShapes(
            World world,
            ReadOnlySpan<ParentTransition> transitions)
        {
            ChildShapeMutation[]? mutations = null;
            int mutationCount = 0;

            try
            {
                CollectChildShapeMutations(world, transitions, ref mutations, ref mutationCount);
                return ApplyChildShapeMutations(world, mutations, mutationCount);
            }
            finally
            {
                if (mutations is not null)
                    ArrayPool<ChildShapeMutation>.Shared.Return(mutations);
            }
        }

        private static void CollectChildShapeMutations(
            World world,
            ReadOnlySpan<ParentTransition> transitions,
            ref ChildShapeMutation[]? mutations,
            ref int mutationCount)
        {
            Entity lastDepthParent = Entity.Null;
            byte lastChildDepth = 0;
            bool hasLastDepthParent = false;

            for (int i = 0; i < transitions.Length; i++)
                AddChildShapeMutation(
                    world,
                    transitions[i],
                    ref lastDepthParent,
                    ref lastChildDepth,
                    ref hasLastDepthParent,
                    ref mutations,
                    ref mutationCount);
        }

        private static void AddChildShapeMutation(
            World world,
            ParentTransition transition,
            ref Entity lastDepthParent,
            ref byte lastChildDepth,
            ref bool hasLastDepthParent,
            ref ChildShapeMutation[]? mutations,
            ref int mutationCount)
        {
            if (transition.CurrentParent == Entity.Null || transition.IsPendingCleanup)
                return;

            int missingMask = MissingMask(transition);
            if (missingMask == 0)
                return;

            byte childDepth = (missingMask & MissingDepthMask) != 0
                ? TransitionFacts.GetDepth(
                    world,
                    transition.CurrentParent,
                    ref lastDepthParent,
                    ref lastChildDepth,
                    ref hasLastDepthParent)
                : (byte)0;

            ChunkMutations.Add(
                ref mutations,
                ref mutationCount,
                transition.ChildArchetype,
                transition.ChildChunk,
                missingMask,
                transition.CurrentParent,
                childDepth);
        }

        private static bool ApplyChildShapeMutations(
            World world,
            ChildShapeMutation[]? mutations,
            int mutationCount)
        {
            bool allEnsured = true;
            for (int i = 0; i < mutationCount; i++)
            {
                var mutation = mutations![i];
                allEnsured &= world.Hierarchy.TryAddShapes(
                    mutation.SourceArchetype,
                    mutation.SourceChunk,
                    mutation.MissingMask,
                    mutation.Parent,
                    mutation.Depth,
                    mutation.Count);
            }

            return allEnsured;
        }

        private static bool TryLeafAdds(
            World world,
            ReadOnlySpan<ParentTransition> transitions,
            bool childrenReady)
        {
            if (!CanUseLeafAdds(world, transitions, childrenReady, out var parent))
                return false;

            Shape.AddParentShape(world, parent);
            ref var childBuffer = ref world.Get<ChildBuffer>(parent);
            if (childBuffer.Children.Count != 0)
                return false;

            byte depth = IncrementDepthFor(parent, world);
            childBuffer.Children.EnsureCapacity(transitions.Length);
            WriteLeafAdds(world, transitions, parent, depth, ref childBuffer);
            return true;
        }

        private static bool CanUseLeafAdds(
            World world,
            ReadOnlySpan<ParentTransition> transitions,
            bool childrenReady,
            out Entity parent)
        {
            parent = transitions.Length == 0 ? Entity.Null : transitions[0].CurrentParent;
            if (!childrenReady || parent == Entity.Null)
                return false;

            for (int i = 0; i < transitions.Length; i++)
            {
                if (!CanUseLeafAdd(world, transitions[i], parent))
                    return false;
            }

            return true;
        }

        private static bool CanUseLeafAdd(World world, ParentTransition transition, Entity parent)
        {
            return transition.CurrentParent == parent &&
                   transition.PreviousParent == Entity.Null &&
                   !transition.IsPendingCleanup &&
                   !transition.HadChildBuffer &&
                   world.IsAlive(transition.Child);
        }

        private static void WriteLeafAdds(
            World world,
            ReadOnlySpan<ParentTransition> transitions,
            Entity parent,
            byte depth,
            ref ChildBuffer childBuffer)
        {
            Archetype? lastFactArchetype = null;
            int depthColumn = -1;
            int linkColumn = -1;
            for (int i = 0; i < transitions.Length; i++)
            {
                var transition = transitions[i];
                var child = transition.Child;
                childBuffer.Children.Add(child);
                if (transition.HadParent && transition.HadDepth && transition.HadLink)
                {
                    if (!ReferenceEquals(lastFactArchetype, transition.ChildArchetype))
                    {
                        lastFactArchetype = transition.ChildArchetype;
                        depthColumn = transition.ChildArchetype.Column(ComponentMetadata<Depth>.Id);
                        linkColumn = transition.ChildArchetype.Column(ComponentMetadata<HierarchyLink>.Id);
                    }

                    world.Hierarchy.WriteExisting(
                        child,
                        transition.ChildChunk,
                        transition.ChildRow,
                        depthColumn,
                        new Depth { Value = depth });
                    world.Hierarchy.WriteExisting(
                        child,
                        transition.ChildChunk,
                        transition.ChildRow,
                        linkColumn,
                        new HierarchyLink { Parent = parent, ChildIndex = childBuffer.Children.Count - 1 });
                    continue;
                }

                if (world.Has<Depth>(child))
                    WriteDepth(world, child, depth);
                if (world.Has<HierarchyLink>(child))
                    world.Replace(child, new HierarchyLink { Parent = parent, ChildIndex = childBuffer.Children.Count - 1 });
            }
        }

        private static int MissingMask(ParentTransition transition)
        {
            int missingMask = 0;
            if (!transition.HadParent)
                missingMask |= MissingParentMask;
            if (!transition.HadDepth)
                missingMask |= MissingDepthMask;
            if (!transition.HadLink)
                missingMask |= MissingLinkMask;

            return missingMask;
        }

        private static void WriteBuffers(
            World world,
            ReadOnlySpan<ParentTransition> transitions)
        {
            var mutations = ParentBufferMutations.Create();

            try
            {
                for (int i = 0; i < transitions.Length; i++)
                    mutations.Collect(world, transitions[i]);

                mutations.Apply(world);
            }
            finally
            {
                mutations.Return();
            }
        }

        private struct ParentBufferMutations
        {
            private ParentChildMutation[]? _removals;
            private ParentChildMutation[]? _additions;
            private int _removalCount;
            private int _additionCount;
            private ParentChildMutation _lastRemoval;
            private ParentChildMutation _lastAddition;
            private bool _hasLastRemoval;
            private bool _hasLastAddition;
            private bool _removalsAreSorted;
            private bool _additionsAreSorted;

            public static ParentBufferMutations Create()
            {
                return new ParentBufferMutations
                {
                    _removalsAreSorted = true,
                    _additionsAreSorted = true,
                };
            }

            public void Collect(World world, ParentTransition transition)
            {
                CollectRemoval(world, transition);
                CollectAddition(world, transition);
            }

            public void Apply(World world)
            {
                ApplyRemovals(world);
                ApplyAdditions(world);
            }

            public void Return()
            {
                if (_removals is not null)
                    ArrayPool<ParentChildMutation>.Shared.Return(_removals);

                if (_additions is not null)
                    ArrayPool<ParentChildMutation>.Shared.Return(_additions);
            }

            private void CollectRemoval(World world, ParentTransition transition)
            {
                if (transition.PreviousParent == Entity.Null ||
                    !world.IsAlive(transition.PreviousParent) ||
                    !world.Has<ChildBuffer>(transition.PreviousParent))
                {
                    return;
                }

                AddRemoval(new ParentChildMutation(transition.PreviousParent, transition.Child));
            }

            private void CollectAddition(World world, ParentTransition transition)
            {
                if (transition.CurrentParent == Entity.Null || transition.IsPendingCleanup)
                    return;

                Shape.AddChildShape(
                    world,
                    transition.Child,
                    transition.HadParent,
                    transition.HadDepth,
                    transition.HadLink);
                AddAddition(new ParentChildMutation(transition.CurrentParent, transition.Child));
            }

            private void AddRemoval(ParentChildMutation removal)
            {
                _removalsAreSorted &= IsSorted(_hasLastRemoval, _lastRemoval, removal);
                _lastRemoval = removal;
                _hasLastRemoval = true;
                ChildMutations.Add(ref _removals, ref _removalCount, removal);
            }

            private void AddAddition(ParentChildMutation addition)
            {
                _additionsAreSorted &= IsSorted(_hasLastAddition, _lastAddition, addition);
                _lastAddition = addition;
                _hasLastAddition = true;
                ChildMutations.Add(ref _additions, ref _additionCount, addition);
            }

            private void ApplyRemovals(World world)
            {
                if (_removalCount == 0)
                    return;

                if (!_removalsAreSorted)
                    Array.Sort(_removals!, 0, _removalCount, ParentMutationComparer.Instance);
                RemoveUnordered(world, _removals!.AsSpan(0, _removalCount));
            }

            private void ApplyAdditions(World world)
            {
                if (_additionCount == 0)
                    return;

                if (!_additionsAreSorted)
                    Array.Sort(_additions!, 0, _additionCount, ParentMutationComparer.Instance);
                AddUnordered(world, _additions!.AsSpan(0, _additionCount));
            }

            private static bool IsSorted(
                bool hasLast,
                ParentChildMutation last,
                ParentChildMutation next)
            {
                return !hasLast ||
                       ParentMutationComparer.Instance.Compare(last, next) <= 0;
            }
        }
    }

}

internal static partial class Hierarchy
{
    private static void RemoveUnordered(
        World world,
        ReadOnlySpan<ParentChildMutation> removals)
    {
        int start = 0;
        while (start < removals.Length)
        {
            var parent = removals[start].Parent;
            int end = start + 1;
            while (end < removals.Length && removals[end].Parent == parent)
                end++;

            ChildBuffers.RemoveFromParent(world, parent, removals[start..end]);
            start = end;
        }
    }

    private static void AddUnordered(
        World world,
        ReadOnlySpan<ParentChildMutation> additions)
    {
        int start = 0;
        while (start < additions.Length)
        {
            var parent = additions[start].Parent;
            int end = start + 1;
            while (end < additions.Length && additions[end].Parent == parent)
                end++;

            ChildBuffers.AddToParent(world, parent, additions[start..end]);
            start = end;
        }
    }

    internal static void AttachUnorderedChild(World world, Entity parent, Entity child)
        => ChildBuffers.Attach(world, parent, child);

    internal static void DetachUnorderedChild(World world, Entity parent, Entity child)
        => ChildBuffers.Detach(world, parent, child);

    private static class ChildBuffers
    {
        public static void RemoveFromParent(
            World world,
            Entity parent,
            ReadOnlySpan<ParentChildMutation> removals)
        {
            if (!world.IsAlive(parent) || !world.Has<ChildBuffer>(parent) || removals.Length == 0)
                return;

            ref var childBuffer = ref world.Get<ChildBuffer>(parent);
            if (childBuffer.Children.Count == 0)
                return;

            if (removals.Length == 1)
            {
                RemoveSwap(world, ref childBuffer, removals[0].Child);
                return;
            }

            if (TryStored(world, parent, ref childBuffer, removals))
                return;

            var children = childBuffer.Children.AsSpan();
            if (AllRemoved(children, removals))
            {
                childBuffer.Children.Clear();
                return;
            }

            int writeIndex = 0;
            for (int readIndex = 0; readIndex < children.Length; readIndex++)
            {
                var child = children[readIndex];
                if (BinarySearch(removals, child) >= 0)
                    continue;

                children[writeIndex] = child;
                WriteIndex(world, child, writeIndex);
                writeIndex++;
            }

            while (childBuffer.Children.Count > writeIndex)
                childBuffer.Children.RemoveAt(childBuffer.Children.Count - 1);
        }

        public static void AddToParent(
            World world,
            Entity parent,
            ReadOnlySpan<ParentChildMutation> additions)
        {
            if (!world.IsAlive(parent) || additions.Length == 0)
                return;

            Shape.AddParentShape(world, parent);
            ref var childBuffer = ref world.Get<ChildBuffer>(parent);

            if (childBuffer.Children.Count == 0)
            {
                childBuffer.Children.EnsureCapacity(additions.Length);
                AddUnique(world, parent, ref childBuffer, additions, null);
                return;
            }

            if (additions.Length == 1)
            {
                var child = additions[0].Child;
                if (KnownAbsent(world, parent, child))
                {
                    int childIndex = childBuffer.Children.Count;
                    childBuffer.Children.Add(child);
                    WriteLink(world, child, parent, childIndex);
                }
                else if (!Contains(childBuffer.Children.AsSpan(), child))
                {
                    int childIndex = childBuffer.Children.Count;
                    childBuffer.Children.Add(child);
                    WriteLink(world, child, parent, childIndex);
                }

                return;
            }

            if (TryAppend(world, parent, ref childBuffer, additions))
                return;

            bool[]? skip = null;
            try
            {
                skip = ArrayPool<bool>.Shared.Rent(additions.Length);
                Array.Clear(skip, 0, additions.Length);

                foreach (var existingChild in childBuffer.Children.AsSpan())
                {
                    int matchIndex = BinarySearch(additions, existingChild);
                    if (matchIndex >= 0)
                        MarkMatches(additions, matchIndex, skip);
                }

                childBuffer.Children.EnsureCapacity(childBuffer.Children.Count + additions.Length);
                AddUnique(world, parent, ref childBuffer, additions, skip);
            }
            finally
            {
                if (skip is not null)
                    ArrayPool<bool>.Shared.Return(skip);
            }
        }

        public static void Attach(World world, Entity parent, Entity child)
        {
            ref var childBuffer = ref world.Get<ChildBuffer>(parent);
            if (KnownAbsent(world, parent, child))
            {
                int childIndex = childBuffer.Children.Count;
                childBuffer.Children.Add(child);
                WriteLink(world, child, parent, childIndex);
                return;
            }

            if (TryIndex(world, parent, child, childBuffer.Children.Count, out int existingIndex) &&
                childBuffer.Children[existingIndex] == child)
            {
                return;
            }

            if (!Contains(childBuffer.Children.AsSpan(), child))
            {
                int childIndex = childBuffer.Children.Count;
                childBuffer.Children.Add(child);
                WriteLink(world, child, parent, childIndex);
            }
        }

        public static void Detach(World world, Entity parent, Entity child)
        {
            if (!world.IsAlive(parent) || !world.Has<ChildBuffer>(parent))
                return;

            ref var childBuffer = ref world.Get<ChildBuffer>(parent);
            if (TryIndex(world, parent, child, childBuffer.Children.Count, out int childIndex) &&
                childBuffer.Children[childIndex] == child)
            {
                RemoveAt(world, ref childBuffer, childIndex);
                return;
            }

            RemoveSwap(world, ref childBuffer, child);
        }

        public static void RemoveAt(World world, ref ChildBuffer childBuffer, int index)
        {
            int lastIndex = childBuffer.Children.Count - 1;
            if (index != lastIndex)
            {
                var movedChild = childBuffer.Children[lastIndex];
                childBuffer.Children[index] = movedChild;
                WriteIndex(world, movedChild, index);
            }

            childBuffer.Children.RemoveAt(lastIndex);
        }

        private static void RemoveSwap(World world, ref ChildBuffer childBuffer, Entity child)
        {
            int count = childBuffer.Children.Count;
            for (int i = 0; i < count; i++)
            {
                if (childBuffer.Children[i] != child)
                    continue;

                RemoveAt(world, ref childBuffer, i);
                return;
            }
        }

        private static bool TryStored(
            World world,
            Entity parent,
            ref ChildBuffer childBuffer,
            ReadOnlySpan<ParentChildMutation> removals)
        {
            Entity lastChild = Entity.Null;
            bool hasLastChild = false;
            for (int i = 0; i < removals.Length; i++)
            {
                var child = removals[i].Child;
                if (hasLastChild && child == lastChild)
                    return false;

                if (!TryIndex(world, parent, child, childBuffer.Children.Count, out int childIndex))
                    return false;

                if (childBuffer.Children[childIndex] != child)
                    return false;

                lastChild = child;
                hasLastChild = true;
            }

            for (int i = 0; i < removals.Length; i++)
            {
                var child = removals[i].Child;
                if (!TryIndex(world, parent, child, childBuffer.Children.Count, out int childIndex) ||
                    childBuffer.Children[childIndex] != child)
                {
                    return false;
                }

                RemoveAt(world, ref childBuffer, childIndex);
            }

            return true;
        }

        private static bool TryIndex(
            World world,
            Entity parent,
            Entity child,
            int childCount,
            out int childIndex)
        {
            childIndex = -1;
            if (!world.IsAlive(child) || !world.Has<HierarchyLink>(child))
                return false;

            var link = world.Read<HierarchyLink>(child);
            if (link.Parent != parent || (uint)link.ChildIndex >= (uint)childCount)
                return false;

            childIndex = link.ChildIndex;
            return true;
        }

        private static bool AllRemoved(
            ReadOnlySpan<Entity> children,
            ReadOnlySpan<ParentChildMutation> removals)
        {
            if (children.Length > removals.Length)
                return false;

            for (int i = 0; i < children.Length; i++)
            {
                if (BinarySearch(removals, children[i]) < 0)
                    return false;
            }

            return true;
        }

        private static bool Contains(ReadOnlySpan<Entity> children, Entity child)
        {
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i] == child)
                    return true;
            }

            return false;
        }

        private static bool TryAppend(
            World world,
            Entity parent,
            ref ChildBuffer childBuffer,
            ReadOnlySpan<ParentChildMutation> additions)
        {
            Entity lastChild = Entity.Null;
            bool hasLastChild = false;
            for (int i = 0; i < additions.Length; i++)
            {
                var child = additions[i].Child;
                if ((hasLastChild && child == lastChild) ||
                    !KnownAbsent(world, parent, child))
                {
                    return false;
                }

                lastChild = child;
                hasLastChild = true;
            }

            childBuffer.Children.EnsureCapacity(childBuffer.Children.Count + additions.Length);
            for (int i = 0; i < additions.Length; i++)
            {
                var child = additions[i].Child;
                int childIndex = childBuffer.Children.Count;
                childBuffer.Children.Add(child);
                WriteLink(world, child, parent, childIndex);
            }

            return true;
        }

        private static bool KnownAbsent(World world, Entity parent, Entity child)
        {
            if (!world.IsAlive(child) || !world.Has<HierarchyLink>(child))
                return false;

            return world.Read<HierarchyLink>(child).Parent != parent;
        }

        private static void AddUnique(
            World world,
            Entity parent,
            ref ChildBuffer childBuffer,
            ReadOnlySpan<ParentChildMutation> additions,
            bool[]? skip)
        {
            Entity lastAdded = Entity.Null;
            bool hasLastAdded = false;
            for (int i = 0; i < additions.Length; i++)
            {
                if (skip is not null && skip[i])
                    continue;

                var child = additions[i].Child;
                if (hasLastAdded && child == lastAdded)
                    continue;

                int childIndex = childBuffer.Children.Count;
                childBuffer.Children.Add(child);
                WriteLink(world, child, parent, childIndex);
                lastAdded = child;
                hasLastAdded = true;
            }
        }

        private static void MarkMatches(
            ReadOnlySpan<ParentChildMutation> additions,
            int matchIndex,
            bool[] skip)
        {
            var child = additions[matchIndex].Child;
            skip[matchIndex] = true;

            for (int i = matchIndex - 1; i >= 0 && additions[i].Child == child; i--)
                skip[i] = true;

            for (int i = matchIndex + 1; i < additions.Length && additions[i].Child == child; i++)
                skip[i] = true;
        }

        private static void WriteLink(
            World world,
            Entity child,
            Entity parent,
            int childIndex)
        {
            if (world.IsAlive(child) && world.Has<HierarchyLink>(child))
                world.Replace(child, new HierarchyLink { Parent = parent, ChildIndex = childIndex });
        }

        private static void WriteIndex(World world, Entity child, int childIndex)
        {
            if (!world.IsAlive(child) || !world.Has<HierarchyLink>(child))
                return;

            var link = world.Read<HierarchyLink>(child);
            world.Replace(child, new HierarchyLink { Parent = link.Parent, ChildIndex = childIndex });
        }

        private static int BinarySearch(
            ReadOnlySpan<ParentChildMutation> mutations,
            Entity child)
        {
            int lower = 0;
            int upper = mutations.Length - 1;

            while (lower <= upper)
            {
                int middle = lower + ((upper - lower) / 2);
                int compare = CompareEntities(mutations[middle].Child, child);
                if (compare == 0)
                    return middle;

                if (compare < 0)
                    lower = middle + 1;
                else
                    upper = middle - 1;
            }

            return -1;
        }
    }

    private static void DetachPreviousParents(
        World world,
        ReadOnlySpan<ParentTransition> transitions,
        DetachChild detach)
    {
        for (int i = 0; i < transitions.Length; i++)
        {
            var previousParent = transitions[i].PreviousParent;
            if (previousParent == Entity.Null ||
                !world.IsAlive(previousParent) ||
                !world.Has<ChildBuffer>(previousParent))
            {
                continue;
            }

            detach(world, previousParent, transitions[i].Child);
        }
    }

    private static void AttachCurrentParents(
        World world,
        ReadOnlySpan<ParentTransition> transitions,
        AttachChild attach)
    {
        for (int i = 0; i < transitions.Length; i++)
        {
            var transition = transitions[i];
            if (transition.CurrentParent == Entity.Null || transition.IsPendingCleanup)
                continue;

            Shape.AddParentShape(world, transition.CurrentParent);
            Shape.AddChildShape(
                world,
                transition.Child,
                transition.HadParent,
                transition.HadDepth,
                transition.HadLink);
            attach(world, transition.CurrentParent, transition.Child, null);
        }
    }

    private static class TransitionFacts
    {
        public static void Write(
            World world,
            ReadOnlySpan<ParentTransition> transitions)
        {
            Entity lastParent = Entity.Null;
            byte lastChildDepth = 0;
            bool hasLastParent = false;

            for (int i = 0; i < transitions.Length; i++)
            {
                var transition = transitions[i];
                if (!world.IsAlive(transition.Child))
                    continue;

                if (transition.CurrentParent == Entity.Null)
                {
                    if (transition.HadParent && world.Has<Parent>(transition.Child))
                        world.Remove<Parent>(transition.Child);

                    if (transition.HadDepth && world.IsAlive(transition.Child))
                        WriteDepthFact(world, transition, 0);

                    continue;
                }

                byte depth = GetDepth(
                    world,
                    transition.CurrentParent,
                    ref lastParent,
                    ref lastChildDepth,
                    ref hasLastParent);
                WriteDepthFact(world, transition, depth);
            }
        }

        public static byte GetDepth(
            World world,
            Entity parent,
            ref Entity lastParent,
            ref byte lastChildDepth,
            ref bool hasLastParent)
        {
            if (hasLastParent && parent == lastParent)
                return lastChildDepth;

            Shape.AddParentShape(world, parent);
            lastParent = parent;
            lastChildDepth = IncrementDepthFor(parent, world);
            hasLastParent = true;
            return lastChildDepth;
        }

        private static void WriteDepthFact(
            World world,
            ParentTransition transition,
            byte depth)
        {
            if (transition.HadChildBuffer)
                WriteDepthTree(world, transition.Child, depth);
            else
                WriteDepth(world, transition.Child, depth);
        }
    }

    private static void CommitParentLinks(
        World world,
        ReadOnlySpan<ParentTransition> transitions)
    {
        for (int i = 0; i < transitions.Length; i++)
        {
            var transition = transitions[i];
            if (!world.IsAlive(transition.Child))
                continue;

            if (transition.IsPendingCleanup)
            {
                if (world.Has<HierarchyLink>(transition.Child))
                    world.Remove<HierarchyLink>(transition.Child);

                continue;
            }

            if (transition.CurrentParent != Entity.Null)
            {
                if (world.Has<HierarchyLink>(transition.Child))
                {
                    var link = world.Read<HierarchyLink>(transition.Child);
                    if (link.Parent == transition.CurrentParent)
                        continue;
                }

                world.Replace(transition.Child, new HierarchyLink
                {
                    Parent = transition.CurrentParent,
                    ChildIndex = -1,
                });
                continue;
            }

            if (world.Has<HierarchyLink>(transition.Child))
                world.Remove<HierarchyLink>(transition.Child);
        }
    }

    private static class CleanupCaches
    {
        public static void Process(World world, ReadOnlySpan<Entity> cleanupParents)
        {
            for (int i = 0; i < cleanupParents.Length; i++)
            {
                var parent = cleanupParents[i];
                if (world.IsAlive(parent) && world.Has<ChildBuffer>(parent))
                {
                    ProcessCleanupParent(world, parent);
                }
            }
        }
    }

    private static void ProcessCleanupParent(World world, Entity parent)
    {
        if (!world.IsAlive(parent) || !world.Has<ChildBuffer>(parent))
            return;

        ref var childBuffer = ref world.Get<ChildBuffer>(parent);
        DetachCleanupChildren(world, parent, ref childBuffer);
        PruneCleanupBuffer(world, parent, ref childBuffer);

        if (world.IsAlive(parent) && world.Has<ChildBuffer>(parent) && childBuffer.Children.Count == 0)
            world.Remove<ChildBuffer>(parent);
    }

    private static void DetachCleanupChildren(
        World world,
        Entity parent,
        ref ChildBuffer childBuffer)
    {
        int snapshotCount = childBuffer.Children.Count;
        var snapshot = ArrayPool<Entity>.Shared.Rent(Math.Max(snapshotCount, 1));
        int written = 0;
        try
        {
            foreach (var child in childBuffer.Children.AsSpan())
                snapshot[written++] = child;

            for (int i = 0; i < written; i++)
            {
                DetachCleanupChild(world, parent, snapshot[i]);
            }
        }
        finally
        {
            ArrayPool<Entity>.Shared.Return(snapshot);
        }
    }

    private static void DetachCleanupChild(World world, Entity parent, Entity child)
    {
        if (!world.IsAlive(child) || !world.Has<Parent>(child))
            return;

        if (world.Read<Parent>(child).Value != parent)
            return;

        world.Remove<Parent>(child);

        if (world.IsAlive(child) && world.Has<Depth>(child))
            WriteDepthTree(world, child, 0);
    }

    private static void PruneCleanupBuffer(
        World world,
        Entity parent,
        ref ChildBuffer childBuffer)
    {
        int index = 0;
        while (index < childBuffer.Children.Count)
        {
            var child = childBuffer.Children[index];
            if (IsStillChild(world, child, parent))
            {
                index++;
                continue;
            }

            ChildBuffers.RemoveAt(world, ref childBuffer, index);
        }
    }

    private static bool IsStillChild(World world, Entity child, Entity parent)
    {
        return world.IsAlive(child) &&
               world.Has<Parent>(child) &&
               world.Read<Parent>(child).Value == parent;
    }

    internal static void CollectSubtree(World world, ref Entity[] buffer, ref int bufferCount)
    {
        for (int index = 0; index < bufferCount; index++)
        {
            var current = buffer[index];
            if (!world.IsAlive(current) || !world.Has<ChildBuffer>(current))
                continue;

            ref var childBuffer = ref world.Get<ChildBuffer>(current);
            foreach (var child in childBuffer.Children.AsSpan())
            {
                if (!world.IsAlive(child))
                    continue;

                if (bufferCount == buffer.Length)
                    GrowPooledBuffer(ref buffer, bufferCount + 1);

                buffer[bufferCount++] = child;
                if (bufferCount > world.EntityCount + 1)
                    throw new InvalidOperationException(
                        "Hierarchy contains a cycle or corrupted child references."
                    );
            }
        }
    }

    internal static void WriteDepthTree(World world, Entity entity, byte depth)
    {
        WriteDepth(world, entity, depth);
        if (!world.Has<ChildBuffer>(entity))
            return;

        var buffer = ArrayPool<Entity>.Shared.Rent(4);
        try
        {
            int bufferCount = 1;
            buffer[0] = entity;

            for (int index = 0; index < bufferCount; index++)
            {
                var current = buffer[index];
                if (!world.IsAlive(current) || !world.Has<ChildBuffer>(current))
                    continue;

                byte childDepth = IncrementDepth(world.Read<Depth>(current).Value);
                ref var childBuffer = ref world.Get<ChildBuffer>(current);
                foreach (var child in childBuffer.Children.AsSpan())
                {
                    if (!world.IsAlive(child))
                        continue;

                    WriteDepth(world, child, childDepth);
                    if (bufferCount == buffer.Length)
                        GrowPooledBuffer(ref buffer, bufferCount + 1);

                    buffer[bufferCount++] = child;
                    if (bufferCount > world.EntityCount + 1)
                        throw new InvalidOperationException(
                            "Hierarchy contains a cycle or corrupted child references."
                        );
                }
            }
        }
        finally
        {
            ArrayPool<Entity>.Shared.Return(buffer);
        }
    }

    internal static byte IncrementDepthFor(Entity parent, World world)
    {
        return IncrementDepth(world.Read<Depth>(parent).Value);
    }

    private static void WriteDepth(World world, Entity entity, byte depth)
    {
        world.Replace(entity, new Depth { Value = depth });
    }

    private static void WriteLink(World world, Entity entity, Entity parent)
    {
        if (!world.IsAlive(entity) || world.IsPendingCleanup(entity))
            return;

        if (parent == Entity.Null)
        {
            if (world.Has<HierarchyLink>(entity))
                world.Remove<HierarchyLink>(entity);

            return;
        }

        if (!world.Has<HierarchyLink>(entity))
        {
            Span<int> componentIds = [ComponentMetadata<HierarchyLink>.Id];
            var context = world.CreateAddWriter(entity, componentIds);
            context.Write(new HierarchyLink { Parent = parent, ChildIndex = -1 });
            return;
        }

        var existing = world.Read<HierarchyLink>(entity);
        int childIndex = existing.Parent == parent ? existing.ChildIndex : -1;
        world.Replace(entity, new HierarchyLink { Parent = parent, ChildIndex = childIndex });
    }

    private static int CompareEntities(Entity left, Entity right)
    {
        int indexCompare = left.Index.CompareTo(right.Index);
        return indexCompare != 0 ? indexCompare : left.Generation.CompareTo(right.Generation);
    }

    private static byte IncrementDepth(byte depth)
    {
        if (depth == byte.MaxValue)
            throw new InvalidOperationException(
                "Hierarchy depth exceeds the supported byte range."
            );

        return (byte)(depth + 1);
    }

    private static void GrowPooledBuffer<T>(ref T[] buffer, int requiredCapacity)
    {
        var replacement = ArrayPool<T>.Shared.Rent(Math.Max(buffer.Length * 2, requiredCapacity));
        Array.Copy(buffer, replacement, buffer.Length);
        ArrayPool<T>.Shared.Return(buffer);
        buffer = replacement;
    }

    private static void AddParentTransition(
        ref ParentTransition[]? transitions,
        ref int transitionCount,
        ParentTransition transition)
    {
        if (transitions is null)
        {
            transitions = ArrayPool<ParentTransition>.Shared.Rent(4);
        }
        else if (transitionCount == transitions.Length)
        {
            var grown = transitions;
            GrowPooledBuffer(ref grown, transitionCount + 1);
            transitions = grown;
        }

        transitions[transitionCount++] = transition;
    }

    private static void AddCleanupParent(
        ref Entity[]? cleanupParents,
        ref int cleanupParentCount,
        Entity cleanupParent)
    {
        if (cleanupParents is null)
        {
            cleanupParents = ArrayPool<Entity>.Shared.Rent(4);
        }
        else if (cleanupParentCount == cleanupParents.Length)
        {
            var grown = cleanupParents;
            GrowPooledBuffer(ref grown, cleanupParentCount + 1);
            cleanupParents = grown;
        }

        cleanupParents[cleanupParentCount++] = cleanupParent;
    }

    private static class ChunkMutations
    {
        public static void Add(
            ref ChildShapeMutation[]? mutations,
            ref int mutationCount,
            Archetype sourceArchetype,
            Chunk sourceChunk,
            int missingMask,
            Entity parent,
            byte depth)
        {
            for (int i = 0; i < mutationCount; i++)
            {
                if (ReferenceEquals(mutations![i].SourceChunk, sourceChunk) &&
                    mutations[i].MissingMask == missingMask &&
                    mutations[i].Parent == parent &&
                    mutations[i].Depth == depth)
                {
                    mutations[i].Count++;
                    return;
                }
            }

            if (mutations is null)
            {
                mutations = ArrayPool<ChildShapeMutation>.Shared.Rent(4);
            }
            else if (mutationCount == mutations.Length)
            {
                var grown = mutations;
                GrowPooledBuffer(ref grown, mutationCount + 1);
                mutations = grown;
            }

            mutations[mutationCount++] = new ChildShapeMutation(
                sourceArchetype,
                sourceChunk,
                missingMask,
                parent,
                depth,
                1);
        }
    }

    private static class ChildMutations
    {
        public static void Add(
            ref ParentChildMutation[]? mutations,
            ref int mutationCount,
            ParentChildMutation mutation)
        {
            if (mutations is null)
            {
                mutations = ArrayPool<ParentChildMutation>.Shared.Rent(4);
            }
            else if (mutationCount == mutations.Length)
            {
                var grown = mutations;
                GrowPooledBuffer(ref grown, mutationCount + 1);
                mutations = grown;
            }

            mutations[mutationCount++] = mutation;
        }
    }
}

