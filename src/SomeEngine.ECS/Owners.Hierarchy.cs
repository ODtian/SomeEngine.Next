using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Owners;

internal sealed partial class Hierarchy
{
    private const int MissingParentBit = 1;
    private const int MissingDepthBit = 2;
    private const int MissingLinkBit = 4;

    private Entities _entities = null!;
    private Tables _tables = null!;
    private Components _components = null!;
    private Clock _clock = null!;

    internal int EditDepth;
    internal uint Tick;
    internal Archetype? ParentSource;
    internal int ParentMask;
    internal StructuralTransition ParentTransition;
    internal int ParentColumn;
    internal int DepthColumn;
    internal int LinkColumn;
    internal Entity[]? DirtyParents;
    internal int DirtyCount;
    internal uint DirtyVersion = 1;
    internal bool ScanNeeded;
    internal readonly List<SomeEngine.ECS.Hierarchy.HierarchyChange> Changes = new();

    internal void Bind(
        Entities entities,
        Tables tables,
        Components components,
        Clock clock)
    {
        _entities = entities;
        _tables = tables;
        _components = components;
        _clock = clock;
    }

    internal bool IsEditing => EditDepth > 0;

    internal uint LastTick => Tick;

    internal bool ShouldCollectDirty => DirtyCount != 0 && !ScanNeeded;

    internal ReadOnlySpan<Entity> DirtyEntities => DirtyParents.AsSpan(0, DirtyCount);

    internal void BeginEdit()
    {
        EditDepth++;
    }

    internal void EndEdit()
    {
        EditDepth--;
    }

    internal uint AcquireTick()
    {
        return _clock.Acquire();
    }

    internal ReadOnlySpan<SomeEngine.ECS.Hierarchy.HierarchyChange> ReadChanges(uint lastVersion)
    {
        var changes = CollectionsMarshal.AsSpan(Changes);
        for (int i = 0; i < changes.Length; i++)
        {
            if (VersionClock.IsNewer(changes[i].Version, lastVersion))
                return changes[i..];
        }

        return ReadOnlySpan<SomeEngine.ECS.Hierarchy.HierarchyChange>.Empty;
    }

    internal void WriteChange(
        SomeEngine.ECS.Hierarchy.HierarchyChangeKind kind,
        Entity child,
        Entity oldParent,
        Entity newParent,
        int oldIndex,
        int newIndex)
    {
        Changes.Add(new SomeEngine.ECS.Hierarchy.HierarchyChange(
            kind,
            child,
            oldParent,
            newParent,
            oldIndex,
            newIndex,
            _clock.Tick));
    }

    internal void CommitTick(uint updateVersion)
    {
        Tick = updateVersion;
        CommitDirty();
    }

    internal bool TryDirtyLocation(
        Entity entity,
        out Archetype archetype,
        out Chunk chunk,
        out int row)
    {
        archetype = null!;
        chunk = null!;
        row = -1;

        if (!_entities.Alive(entity))
            return false;

        ref var record = ref _entities.Row(entity);
        if (record.Archetype is null || record.Chunk is null)
            return false;

        archetype = record.Archetype;
        chunk = record.Chunk;
        row = record.RowInChunk;
        return true;
    }

    internal bool TryParent<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        if (typeof(T) != typeof(SomeEngine.ECS.Hierarchy.Parent))
            return false;

        ref readonly var parent =
            ref Unsafe.As<T, SomeEngine.ECS.Hierarchy.Parent>(ref Unsafe.AsRef(in value));
        if (parent.Value == Entity.Null)
            return false;

        AddDeferred(entity, in parent);
        return true;
    }

    internal void TrackParent<T>(Entity entity)
        where T : struct
    {
        if (typeof(T) == typeof(SomeEngine.ECS.Hierarchy.Parent))
            MarkDirty(entity);
    }

    internal void TrackParent(Entity entity, int componentId)
    {
        if (componentId == ComponentMetadata<SomeEngine.ECS.Hierarchy.Parent>.Id)
            MarkDirty(entity);
    }

    internal void RequireScan<T>()
        where T : struct
    {
        if (typeof(T) == typeof(SomeEngine.ECS.Hierarchy.Parent))
            RequireScan();
    }

    internal void RequireScan(int componentId)
    {
        if (componentId == ComponentMetadata<SomeEngine.ECS.Hierarchy.Parent>.Id)
            RequireScan();
    }

    internal void RequireScan(ReadOnlySpan<int> componentIds)
    {
        if (componentIds.BinarySearch(ComponentMetadata<SomeEngine.ECS.Hierarchy.Parent>.Id) >= 0)
            RequireScan();
    }

    internal void RequireScan(Archetype archetype)
    {
        if (archetype.HasComponent(ComponentMetadata<SomeEngine.ECS.Hierarchy.Parent>.Id))
            RequireScan();
    }

    internal void RequireScan()
    {
        if (!IsEditing)
            ScanNeeded = true;
    }

    internal void MarkDirty(Entity entity)
    {
        if (IsEditing || !_entities.Store.IsAlive(entity))
            return;

        ref var record = ref _entities.Store.GetRecord(entity);
        if (record.Archetype is null)
            return;

        if (record.ParentDirtyVersion == DirtyVersion)
            return;

        record.ParentDirtyVersion = DirtyVersion;
        ArrayGrowthExtensions.EnsureCapacity(
            ref DirtyParents,
            DirtyCount + 1,
            64);
        DirtyParents[DirtyCount++] = entity;
    }

    internal void CommitDirty()
    {
        DirtyCount = 0;
        ScanNeeded = false;
        DirtyVersion++;
        if (DirtyVersion == 0)
            DirtyVersion = 1;
    }

    internal bool TryAddShapes(
        Archetype sourceArchetype,
        Chunk sourceChunk,
        int missingMask,
        Entity parent,
        byte depth,
        int expectedEntityCount)
    {
        if (!CanMoveWholeChunk(sourceArchetype, sourceChunk, expectedEntityCount))
            return false;

        int effectiveMissingMask = EffectiveMask(sourceArchetype, missingMask);
        if (effectiveMissingMask == 0)
            return true;

        if (!TryCreateShapeTransition(sourceArchetype, effectiveMissingMask, out var transition, out var columns))
            return false;

        var targetArchetype = transition.Target;
        int count = sourceChunk.Count;
        _tables.EnsureCapacity(targetArchetype, count);

        for (int row = 0; row < count; row++)
            AddShapeRow(sourceArchetype, sourceChunk, row, transition, columns, parent, depth);

        FinishShapeMove(sourceArchetype, sourceChunk);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteExisting<T>(
        Entity entity,
        Chunk chunk,
        int row,
        int columnIndex,
        in T value)
        where T : struct, IComponent
    {
        _components.WriteExisting(entity, chunk, row, columnIndex, in value);
    }

    internal void Reset()
    {
        EditDepth = 0;
        Tick = 0;
        ParentSource = null;
        ParentMask = 0;
        ParentTransition = default;
        ParentColumn = 0;
        DepthColumn = 0;
        LinkColumn = 0;
        DirtyParents = null;
        DirtyCount = 0;
        DirtyVersion = 1;
        ScanNeeded = false;
        Changes.Clear();
    }
}

internal sealed partial class Hierarchy
{
    private void AddDeferred(Entity entity, in SomeEngine.ECS.Hierarchy.Parent parent)
    {
        ref var record = ref _entities.Store.GetRecord(entity);
        var sourceArchetype = record.Archetype!;
        var transition = GetDeferredTransition(sourceArchetype, out var columns);

        _tables.MoveEntity(entity, ref record, transition);
        var targetChunk = record.Chunk!;
        int targetRow = record.RowInChunk;
        var target = record.Archetype!;

        WriteDeferredShape(entity, parent, target, targetChunk, targetRow, columns);
    }

    private static bool CanMoveWholeChunk(
        Archetype sourceArchetype,
        Chunk sourceChunk,
        int expectedEntityCount)
    {
        return expectedEntityCount > 0 &&
               sourceChunk.Count == expectedEntityCount &&
               sourceArchetype.SharedComponentIds.Length == 0;
    }

    private bool TryCreateShapeTransition(
        Archetype sourceArchetype,
        int missingMask,
        out StructuralTransition transition,
        out ShapeColumns columns)
    {
        Span<int> componentIds = stackalloc int[3];
        int componentCount = FillIds(missingMask, componentIds);
        componentIds[..componentCount].Sort();
        RequireScan(componentIds[..componentCount]);

        transition = _tables.Registry.IncludeTransition(
            sourceArchetype,
            componentIds[..componentCount]);
        if (transition.IsIdentityFor(sourceArchetype) ||
            transition.Target.SharedComponentIds.Length != 0)
        {
            columns = default;
            return false;
        }

        columns = ShapeColumns.Resolve(transition.Target, missingMask);
        return true;
    }

    private void AddShapeRow(
        Archetype sourceArchetype,
        Chunk sourceChunk,
        int sourceRow,
        StructuralTransition transition,
        ShapeColumns columns,
        Entity parent,
        byte depth)
    {
        var entity = sourceChunk.Entities[sourceRow];
        var targetArchetype = transition.Target;
        var (targetChunk, targetRow) = _tables.AllocateInChunk(targetArchetype, entity);
        CopyShared(
            sourceArchetype,
            sourceChunk,
            sourceRow,
            targetArchetype,
            targetChunk,
            targetRow,
            transition.SharedColumns);

        WriteShapeComponents(entity, targetArchetype, targetChunk, targetRow, columns, parent, depth);

        ref var record = ref _entities.Store.GetRecord(entity);
        record.Archetype = targetArchetype;
        record.Chunk = targetChunk;
        record.RowInChunk = targetRow;
    }

    private void WriteShapeComponents(
        Entity entity,
        Archetype target,
        Chunk targetChunk,
        int targetRow,
        ShapeColumns columns,
        Entity parent,
        byte depth)
    {
        if (columns.Parent >= 0)
            WriteAdded(
                entity,
                target,
                targetChunk,
                targetRow,
                columns.Parent,
                new SomeEngine.ECS.Hierarchy.Parent { Value = parent });
        if (columns.Depth >= 0)
            WriteAdded(
                entity,
                target,
                targetChunk,
                targetRow,
                columns.Depth,
                new SomeEngine.ECS.Hierarchy.Depth { Value = depth });
        if (columns.Link >= 0)
            WriteAdded(
                entity,
                target,
                targetChunk,
                targetRow,
                columns.Link,
                new SomeEngine.ECS.Hierarchy.HierarchyLink { Parent = parent, ChildIndex = -1 });
    }

    private void FinishShapeMove(Archetype sourceArchetype, Chunk sourceChunk)
    {
        sourceChunk.Count = 0;
        sourceChunk.OrderVersion++;
        _tables.TryRecycleChunk(sourceArchetype, sourceChunk);
    }

    private StructuralTransition GetDeferredTransition(
        Archetype sourceArchetype,
        out ShapeColumns columns)
    {
        int missingMask = DeferredMissingMask(sourceArchetype);
        if (ReferenceEquals(ParentSource, sourceArchetype) && ParentMask == missingMask)
        {
            columns = new ShapeColumns(ParentColumn, DepthColumn, LinkColumn);
            return ParentTransition;
        }

        return CreateDeferredTransition(sourceArchetype, missingMask, out columns);
    }

    private StructuralTransition CreateDeferredTransition(
        Archetype sourceArchetype,
        int missingMask,
        out ShapeColumns columns)
    {
        Span<int> componentIds = stackalloc int[3];
        int componentCount = FillIds(missingMask, componentIds);
        componentIds[..componentCount].Sort();

        var transition = _tables.Registry.IncludeTransition(
            sourceArchetype,
            componentIds[..componentCount]);
        columns = ShapeColumns.Resolve(transition.Target, missingMask);
        CacheDeferredTransition(sourceArchetype, missingMask, transition, columns);
        return transition;
    }

    private void CacheDeferredTransition(
        Archetype sourceArchetype,
        int missingMask,
        StructuralTransition transition,
        ShapeColumns columns)
    {
        ParentSource = sourceArchetype;
        ParentMask = missingMask;
        ParentTransition = transition;
        ParentColumn = columns.Parent;
        DepthColumn = columns.Depth;
        LinkColumn = columns.Link;
    }

    private void WriteDeferredShape(
        Entity entity,
        in SomeEngine.ECS.Hierarchy.Parent parent,
        Archetype target,
        Chunk targetChunk,
        int targetRow,
        ShapeColumns columns)
    {
        WriteAdded(
            entity,
            target,
            targetChunk,
            targetRow,
            columns.Parent,
            parent);
        if (columns.Depth >= 0)
        {
            WriteAdded(
                entity,
                target,
                targetChunk,
                targetRow,
                columns.Depth,
                new SomeEngine.ECS.Hierarchy.Depth { Value = 0 });
        }

        if (columns.Link >= 0)
        {
            WriteAdded(
                entity,
                target,
                targetChunk,
                targetRow,
                columns.Link,
                new SomeEngine.ECS.Hierarchy.HierarchyLink { ChildIndex = -1 });
        }
    }

    private static int DeferredMissingMask(Archetype sourceArchetype)
    {
        int missingMask = MissingParentBit;
        if (!sourceArchetype.HasComponent(ComponentMetadata<SomeEngine.ECS.Hierarchy.Depth>.Id))
            missingMask |= MissingDepthBit;

        if (!sourceArchetype.HasComponent(ComponentMetadata<SomeEngine.ECS.Hierarchy.HierarchyLink>.Id))
            missingMask |= MissingLinkBit;

        return missingMask;
    }

    private static int EffectiveMask(Archetype sourceArchetype, int missingMask)
    {
        int effectiveMissingMask = 0;
        if ((missingMask & MissingParentBit) != 0 &&
            !sourceArchetype.HasComponent(ComponentMetadata<SomeEngine.ECS.Hierarchy.Parent>.Id))
        {
            effectiveMissingMask |= MissingParentBit;
        }

        if ((missingMask & MissingDepthBit) != 0 &&
            !sourceArchetype.HasComponent(ComponentMetadata<SomeEngine.ECS.Hierarchy.Depth>.Id))
        {
            effectiveMissingMask |= MissingDepthBit;
        }

        if ((missingMask & MissingLinkBit) != 0 &&
            !sourceArchetype.HasComponent(ComponentMetadata<SomeEngine.ECS.Hierarchy.HierarchyLink>.Id))
        {
            effectiveMissingMask |= MissingLinkBit;
        }

        return effectiveMissingMask;
    }

    private static int FillIds(int missingMask, Span<int> componentIds)
    {
        int componentCount = 0;
        if ((missingMask & MissingParentBit) != 0)
            componentIds[componentCount++] = ComponentMetadata<SomeEngine.ECS.Hierarchy.Parent>.Id;
        if ((missingMask & MissingDepthBit) != 0)
            componentIds[componentCount++] = ComponentMetadata<SomeEngine.ECS.Hierarchy.Depth>.Id;
        if ((missingMask & MissingLinkBit) != 0)
            componentIds[componentCount++] = ComponentMetadata<SomeEngine.ECS.Hierarchy.HierarchyLink>.Id;

        return componentCount;
    }

    private readonly struct ShapeColumns
    {
        public ShapeColumns(int parent, int depth, int link)
        {
            Parent = parent;
            Depth = depth;
            Link = link;
        }

        public int Parent { get; }

        public int Depth { get; }

        public int Link { get; }

        public static ShapeColumns Resolve(Archetype target, int missingMask)
        {
            return new ShapeColumns(
                ColumnOrMissing<SomeEngine.ECS.Hierarchy.Parent>(target, missingMask, MissingParentBit),
                ColumnOrMissing<SomeEngine.ECS.Hierarchy.Depth>(target, missingMask, MissingDepthBit),
                ColumnOrMissing<SomeEngine.ECS.Hierarchy.HierarchyLink>(target, missingMask, MissingLinkBit));
        }

        private static int ColumnOrMissing<T>(
            Archetype target,
            int missingMask,
            int bit)
            where T : struct
        {
            return (missingMask & bit) != 0
                ? target.Column(ComponentMetadata<T>.Id)
                : -1;
        }
    }

    private static void CopyShared(
        Archetype sourceArchetype,
        Chunk sourceChunk,
        int sourceRow,
        Archetype targetArchetype,
        Chunk targetChunk,
        int targetRow,
        ReadOnlySpan<SharedColumnMapping> mappings)
    {
        foreach (var mapping in mappings)
        {
            unsafe
            {
                mapping.Operations.CopyElement(
                    sourceChunk.Columns[mapping.SourceColumnIndex],
                    sourceRow,
                    targetChunk.Columns[mapping.DestinationColumnIndex],
                    targetRow);
            }

            sourceChunk.CopyVersions(
                mapping.SourceColumnIndex,
                sourceRow,
                mapping.DestinationColumnIndex,
                targetRow,
                targetChunk);

            int componentId = sourceArchetype.ColumnMetas[mapping.SourceColumnIndex].ComponentId;
            if (sourceArchetype.TryMask(componentId, out int sourceMaskIndex))
            {
                int targetMaskIndex = targetArchetype.EnableMask(componentId);
                bool enabled = sourceChunk.IsEnabled(sourceMaskIndex, sourceRow);
                targetChunk.WriteEnabled(targetMaskIndex, targetRow, enabled);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteAdded<T>(
        Entity entity,
        Archetype archetype,
        Chunk chunk,
        int row,
        int columnIndex,
        in T value)
        where T : struct, IComponent
    {
        _components.WriteAdded(entity, archetype, chunk, row, columnIndex, in value);
    }
}

