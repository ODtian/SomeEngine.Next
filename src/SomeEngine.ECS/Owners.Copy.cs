using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Owners;

internal sealed class Copy
{
    private Entities _entities = null!;
    private Tables _tables = null!;
    private Components _components = null!;
    private Buffers _buffers = null!;
    private Sparse _sparse = null!;
    private Indices _indices = null!;
    private Clock _clock = null!;
    private Iteration _iteration = null!;
    private Hierarchy _hierarchy = null!;

    internal void Bind(
        Entities entities,
        Tables tables,
        Components components,
        Buffers buffers,
        Sparse sparse,
        Indices indices,
        Clock clock,
        Iteration iteration,
        Hierarchy hierarchy)
    {
        _entities = entities;
        _tables = tables;
        _components = components;
        _buffers = buffers;
        _sparse = sparse;
        _indices = indices;
        _clock = clock;
        _iteration = iteration;
        _hierarchy = hierarchy;
    }

    /// <summary>
    /// Creates a new entity in this world and shallow-copies the source entity's standard logical storage surface.
    /// Cleanup components, outgoing relations, incoming relations, child/subtree state, and Entity field remapping are excluded.
    /// </summary>
    internal Entity Clone(Entity source)
    {
        return Clone(source, EntityCopyOptions.Default);
    }

    /// <summary>
    /// Creates a new entity in this world and shallow-copies the selected source entity storage surface.
    /// Passing <see cref="EntityCopyOptions.Default"/> uses <see cref="EntityCopyOptions.Standard"/>.
    /// </summary>
    internal Entity Clone(Entity source, EntityCopyOptions options)
    {
        _iteration.Throw();
        CopyGuard.ThrowIfCandidate(this, source, nameof(source));

        var surface = NormalizeCopyOptions(options);
        ValidatePublicRelationshipSurface(source, surface, "World.CloneEntity");
        return CloneDirect(source, surface);
    }

    /// <summary>
    /// Replaces the target entity's standard logical storage surface with a shallow copy of the source entity's surface.
    /// The target entity identity is preserved.
    /// </summary>
    internal void CopyInto(Entity source, Entity target)
    {
        CopyInto(source, target, EntityCopyOptions.Default);
    }

    /// <summary>
    /// Replaces the target entity's selected logical storage surface with a shallow copy of the source entity's surface.
    /// The target entity identity is preserved. Incoming relation edges are never copied.
    /// </summary>
    internal void CopyInto(Entity source, Entity target, EntityCopyOptions options)
    {
        _iteration.Throw();
        CopyGuard.ThrowIfCandidate(this, source, nameof(source));
        CopyGuard.ThrowIfCandidate(this, target, nameof(target));

        if (source == target)
            return;

        var surface = NormalizeCopyOptions(options);
        ValidatePublicRelationshipSurface(source, surface, "World.CopyEntity(source)");
        ValidatePublicRelationshipSurface(target, surface, "World.CopyEntity(target)");
        EntityRecord sourceRecord = _entities.Store.GetRecordReadOnly(source);
        EntityRecord targetRecord = _entities.Store.GetRecordReadOnly(target);
        var sourceArchetype = sourceRecord.Archetype!;
        var targetArchetype = targetRecord.Archetype!;
        var sourceChunk = sourceRecord.Chunk!;
        var targetChunk = targetRecord.Chunk!;
        int sourceRow = sourceRecord.RowInChunk;
        int targetRow = targetRecord.RowInChunk;

        var destinationIds = CopyShape.CopyIds(sourceArchetype, targetArchetype, surface);
        var destinationArchetype = _tables.Registry.GetOrCreate(destinationIds);
        int[]? destinationSharedValues = CopyShape.CopyShared(
            sourceArchetype,
            sourceChunk,
            targetArchetype,
            targetChunk,
            destinationArchetype,
            surface);

        TableSurface.Replace(
            this,
            target,
            sourceArchetype,
            sourceChunk,
            sourceRow,
            targetArchetype,
            targetChunk,
            targetRow,
            destinationArchetype,
            destinationSharedValues,
            surface); 

        CopyExtraSurfaces(source, target, sourceArchetype, targetArchetype, destinationArchetype, surface);
    }

    private Entity CloneDirect(Entity source, EntityCopyOptions surface)
    {
        EntityRecord sourceRecord = _entities.Store.GetRecordReadOnly(source);
        var sourceArchetype = sourceRecord.Archetype!;
        var sourceChunk = sourceRecord.Chunk!;
        int sourceRow = sourceRecord.RowInChunk;

        var destinationArchetype = ResolveCloneArchetype(source, sourceArchetype, surface);

        EntityRecordWriter targetRecord = _entities.Store.Allocate(out var target);
        var (destinationChunk, destinationRow) = AllocateCloneDestination(
            target,
            sourceArchetype,
            sourceChunk,
            destinationArchetype);

        targetRecord.Archetype = destinationArchetype;
        targetRecord.Chunk = destinationChunk;
        targetRecord.RowInChunk = destinationRow;
        CopyCloneColumns(
            target,
            sourceArchetype,
            sourceChunk,
            sourceRow,
            destinationArchetype,
            destinationChunk,
            destinationRow,
            surface);
        CopyExtraSurfaces(source, target, sourceArchetype, _tables.Empty, destinationArchetype, surface);

        return target;
    }

    private Archetype ResolveCloneArchetype(
        Entity source,
        Archetype sourceArchetype,
        EntityCopyOptions surface)
    {
        int sourceComponentCount = sourceArchetype.ComponentIds.Length;
        Span<int> destinationComponentIds = sourceComponentCount <= 64
            ? stackalloc int[sourceComponentCount]
            : new int[sourceComponentCount];
        int destinationComponentCount = CopyShape.CloneIds(
            sourceArchetype,
            surface,
            destinationComponentIds);
        return _tables.Registry.GetOrCreate(destinationComponentIds[..destinationComponentCount]);
    }

    private (Chunk Chunk, int Row) AllocateCloneDestination(
        Entity target,
        Archetype sourceArchetype,
        Chunk sourceChunk,
        Archetype destinationArchetype)
    {
        int sharedCount = destinationArchetype.SharedComponentIds.Length;
        if (sharedCount == 0)
            return _tables.AllocateInChunk(destinationArchetype, target);

        Span<int> destinationSharedValues = sharedCount <= 16
            ? stackalloc int[sharedCount]
            : new int[sharedCount];
        CopyShape.CloneShared(sourceArchetype, sourceChunk, destinationArchetype, destinationSharedValues);
        return _tables.AllocateShared(destinationArchetype, target, destinationSharedValues);
    }

    private void CopyCloneColumns(
        Entity target,
        Archetype sourceArchetype,
        Chunk sourceChunk,
        int sourceRow,
        Archetype destinationArchetype,
        Chunk destinationChunk,
        int destinationRow,
        EntityCopyOptions surface)
    {
        CopyDestinationColumns(
            sourceArchetype,
            sourceChunk,
            sourceRow,
            _tables.Empty,
            destinationChunk,
            destinationRow,
            destinationArchetype,
            destinationChunk,
            destinationRow,
            surface);
        CopyEnableableState(
            sourceArchetype,
            sourceChunk,
            sourceRow,
            _tables.Empty,
            destinationChunk,
            destinationRow,
            destinationArchetype,
            destinationChunk,
            destinationRow,
            surface);

        FinalizeCopiedColumns(
            target,
            sourceArchetype,
            _tables.Empty,
            destinationArchetype,
            destinationChunk,
            destinationRow,
            surface);
    }

    private void CopyExtraSurfaces(
        Entity source,
        Entity target,
        Archetype sourceArchetype,
        Archetype targetArchetype,
        Archetype destinationArchetype,
        EntityCopyOptions surface)
    {
        if (surface.HasFlag(EntityCopyOptions.DynamicBuffers))
            ExtraSurface.CopyBuffers(
                _buffers,
                source,
                target,
                sourceArchetype,
                targetArchetype,
                destinationArchetype);

        if (surface.HasFlag(EntityCopyOptions.SparseComponents))
            _sparse.Copy(source, target);

    }

    private static EntityCopyOptions NormalizeCopyOptions(EntityCopyOptions options)
    {
        return options == EntityCopyOptions.Default ? EntityCopyOptions.Standard : options;
    }

    private static class CopyGuard
    {
        public static void ThrowIfCandidate(Copy copy, Entity entity, string paramName)
        {
            if (!copy._entities.Store.IsAlive(entity))
                throw new InvalidOperationException($"{paramName} entity {entity} is not alive in this World.");

            if (copy._entities.Pending(entity))
                throw new InvalidOperationException($"{paramName} entity {entity} is pending cleanup and cannot be copied.");
        }
    }

    private void ValidatePublicRelationshipSurface(
        Entity entity,
        EntityCopyOptions surface,
        string operation)
    {
        EntityRecord record = _entities.Store.GetRecordReadOnly(entity);
        PublicComponentMutationGuard.CopySurface(
            record.Archetype!.ComponentIds,
            surface.HasFlag(EntityCopyOptions.TableComponents),
            surface.HasFlag(EntityCopyOptions.CleanupComponents),
            operation);
    }

    private static class CopyShape
    {
        public static int CloneIds(
            Archetype sourceArchetype,
            EntityCopyOptions surface,
            Span<int> destinationIds)
        {
            int count = 0;
            foreach (int componentId in sourceArchetype.ComponentIds)
            {
                if (CopyRules.CopySource(componentId, surface))
                    destinationIds[count++] = componentId;
            }

            destinationIds[..count].Sort();
            return count;
        }

        public static void CloneShared(
            Archetype sourceArchetype,
            Chunk sourceChunk,
            Archetype destinationArchetype,
            Span<int> destinationSharedValues)
        {
            for (int i = 0; i < destinationArchetype.SharedComponentIds.Length; i++)
            {
                int componentId = destinationArchetype.SharedComponentIds[i];
                if (!sourceArchetype.HasComponent(componentId))
                    throw new InvalidOperationException(
                        $"Cannot resolve shared component ID {componentId} for cloned entity destination.");

                destinationSharedValues[i] = Shared.EntityIndex(sourceArchetype, sourceChunk, componentId);
            }
        }

        public static int[] CopyIds(
            Archetype sourceArchetype,
            Archetype targetArchetype,
            EntityCopyOptions surface)
        {
            var ids = new List<int>(sourceArchetype.ComponentIds.Length + targetArchetype.ComponentIds.Length);

            foreach (int componentId in targetArchetype.ComponentIds)
            {
                if (CopyRules.PreserveTarget(componentId, surface))
                    CopyRules.AddUnique(ids, componentId);
            }

            foreach (int componentId in sourceArchetype.ComponentIds)
            {
                if (CopyRules.CopySource(componentId, surface))
                    CopyRules.AddUnique(ids, componentId);
            }

            ids.Sort();
            return ids.ToArray();
        }

        public static int[]? CopyShared(
            Archetype sourceArchetype,
            Chunk sourceChunk,
            Archetype targetArchetype,
            Chunk targetChunk,
            Archetype destinationArchetype,
            EntityCopyOptions surface)
        {
            if (destinationArchetype.SharedComponentIds.Length == 0)
                return null;

            var values = new int[destinationArchetype.SharedComponentIds.Length];
            for (int i = 0; i < destinationArchetype.SharedComponentIds.Length; i++)
            {
                int componentId = destinationArchetype.SharedComponentIds[i];
                if (surface.HasFlag(EntityCopyOptions.SharedComponents) &&
                    sourceArchetype.HasComponent(componentId))
                {
                    values[i] = Shared.EntityIndex(sourceArchetype, sourceChunk, componentId);
                    continue;
                }

                if (!targetArchetype.HasComponent(componentId))
                    throw new InvalidOperationException(
                        $"Cannot resolve shared component ID {componentId} for copied entity destination.");

                values[i] = Shared.EntityIndex(targetArchetype, targetChunk, componentId);
            }

            return values;
        }
    }

    private static class TableSurface
    {
        public static void Replace(
            Copy copy,
            Entity target,
            Archetype sourceArchetype,
            Chunk sourceChunk,
            int sourceRow,
            Archetype targetArchetype,
            Chunk targetChunk,
            int targetRow,
            Archetype destinationArchetype,
            int[]? destinationSharedValues,
            EntityCopyOptions surface)
        {
            if (CanReuse(targetArchetype, targetChunk, destinationArchetype, destinationSharedValues))
            {
                CopyInPlace(
                    copy,
                    target,
                    sourceArchetype,
                    sourceChunk,
                    sourceRow,
                    targetArchetype,
                    targetChunk,
                    targetRow,
                    surface);
                copy.CopyEnableableState(
                    sourceArchetype,
                    sourceChunk,
                    sourceRow,
                    targetArchetype,
                    targetChunk,
                    targetRow,
                    targetArchetype,
                    targetChunk,
                    targetRow,
                    surface);
                return;
            }

            ReplaceByMove(
                copy,
                target,
                sourceArchetype,
                sourceChunk,
                sourceRow,
                targetArchetype,
                targetChunk,
                targetRow,
                destinationArchetype,
                destinationSharedValues,
                surface);
        }

        private static void ReplaceByMove(
            Copy copy,
            Entity target,
            Archetype sourceArchetype,
            Chunk sourceChunk,
            int sourceRow,
            Archetype targetArchetype,
            Chunk targetChunk,
            int targetRow,
            Archetype destinationArchetype,
            int[]? destinationSharedValues,
            EntityCopyOptions surface)
        {
            var removed = ComponentChanges.CaptureRemoved(copy, target, targetArchetype, targetChunk, targetRow, destinationArchetype);
            var replaced = ComponentChanges.CaptureReplaced(
                copy,
                target,
                sourceArchetype,
                targetArchetype,
                targetChunk,
                targetRow,
                destinationArchetype,
                surface);
            var (destinationChunk, destinationRow) = AllocateDestination(
                copy,
                target,
                destinationArchetype,
                destinationSharedValues);

            CopyMovedColumns(
                copy,
                sourceArchetype,
                sourceChunk,
                sourceRow,
                targetArchetype,
                targetChunk,
                targetRow,
                destinationArchetype,
                destinationChunk,
                destinationRow,
                surface);

            RemoveTargetRow(copy, targetArchetype, targetChunk, targetRow);

            EntityRecordWriter targetRecord = copy._entities.Store.GetRecord(target);
            targetRecord.Archetype = destinationArchetype;
            targetRecord.Chunk = destinationChunk;
            targetRecord.RowInChunk = destinationRow;

            CommitMoveReplace(
                copy,
                target,
                sourceArchetype,
                targetArchetype,
                destinationArchetype,
                destinationChunk,
                destinationRow,
                surface,
                removed,
                replaced);
        }

        private static (Chunk Chunk, int Row) AllocateDestination(
            Copy copy,
            Entity target,
            Archetype destinationArchetype,
            int[]? destinationSharedValues)
        {
            return destinationSharedValues is { Length: > 0 }
                ? copy._tables.AllocateShared(destinationArchetype, target, destinationSharedValues)
                : copy._tables.AllocateInChunk(destinationArchetype, target);
        }

        private static void CopyMovedColumns(
            Copy copy,
            Archetype sourceArchetype,
            Chunk sourceChunk,
            int sourceRow,
            Archetype targetArchetype,
            Chunk targetChunk,
            int targetRow,
            Archetype destinationArchetype,
            Chunk destinationChunk,
            int destinationRow,
            EntityCopyOptions surface)
        {
            copy.CopyDestinationColumns(
                sourceArchetype,
                sourceChunk,
                sourceRow,
                targetArchetype,
                targetChunk,
                targetRow,
                destinationArchetype,
                destinationChunk,
                destinationRow,
                surface);
            copy.CopyEnableableState(
                sourceArchetype,
                sourceChunk,
                sourceRow,
                targetArchetype,
                targetChunk,
                targetRow,
                destinationArchetype,
                destinationChunk,
                destinationRow,
                surface);
        }

        private static void RemoveTargetRow(
            Copy copy,
            Archetype targetArchetype,
            Chunk targetChunk,
            int targetRow)
        {
            var movedEntity =
                targetChunk.RemoveRow(targetRow, targetArchetype.ColumnOperations);
            if (movedEntity != Entity.Null)
            {
                EntityRecordWriter movedRecord = copy._entities.Store.GetRecord(movedEntity);
                movedRecord.RowInChunk = targetRow;
            }

            copy._tables.TryRecycleChunk(targetArchetype, targetChunk);
        }

        private static void CommitMoveReplace(
            Copy copy,
            Entity target,
            Archetype sourceArchetype,
            Archetype targetArchetype,
            Archetype destinationArchetype,
            Chunk destinationChunk,
            int destinationRow,
            EntityCopyOptions surface,
            List<ComponentChanges.OldComponent>? removed,
            List<ComponentChanges.OldComponent>? replaced)
        {
            copy.FinalizeCopiedColumns(
                target,
                sourceArchetype,
                targetArchetype,
                destinationArchetype,
                destinationChunk,
                destinationRow,
                surface);
            ComponentChanges.CommitReplaced(
                copy,
                target,
                replaced,
                destinationArchetype,
                destinationChunk,
                destinationRow);
            ComponentChanges.CommitRemoved(copy, target, removed);
        }

        private static bool CanReuse(
            Archetype targetArchetype,
            Chunk targetChunk,
            Archetype destinationArchetype,
            int[]? destinationSharedValues)
        {
            if (!ReferenceEquals(targetArchetype, destinationArchetype))
                return false;

            if (destinationArchetype.SharedComponentIds.Length == 0)
                return true;

            return destinationSharedValues is not null &&
                   Shared.ValuesMatch(targetChunk.SharedValues, destinationSharedValues);
        }

        private static void CopyInPlace(
            Copy copy,
            Entity target,
            Archetype sourceArchetype,
            Chunk sourceChunk,
            int sourceRow,
            Archetype targetArchetype,
            Chunk targetChunk,
            int targetRow,
            EntityCopyOptions surface)
        {
            bool writePrepared = false;
            for (int destinationColumn = 0; destinationColumn < targetArchetype.TableComponentIds.Length; destinationColumn++)
            {
                int componentId = targetArchetype.TableComponentIds[destinationColumn];
                if (!CopyRules.CopyColumn(componentId, sourceArchetype, surface))
                    continue;

                // The erased ref copy writes directly into the selected row. Detach immediately
                // before the first selected column, while a surface with no selected columns can
                // remain shared and let equal enable-bit writes stay no-ops as well.
                if (!writePrepared)
                {
                    targetChunk.EnsureWritable();
                    writePrepared = true;
                }

                int sourceColumn = sourceArchetype.Column(componentId);
                ref readonly ComponentOperations operations =
                    ref targetArchetype.ColumnOperations[destinationColumn];
                Array oldValueSnapshot = targetChunk.CaptureComponentValue(
                    destinationColumn,
                    targetRow,
                    in operations);
                copy._indices.Drop(
                    target,
                    componentId,
                    targetChunk,
                    destinationColumn,
                    targetRow);
                sourceChunk.CopyComponentTo(
                    sourceColumn,
                    sourceRow,
                    targetChunk,
                    destinationColumn,
                    targetRow,
                    in operations);

                copy.MarkWrite(targetChunk, destinationColumn, targetRow);
                copy._components.CommitReplace(
                    componentId,
                    target,
                    oldValueSnapshot,
                    targetChunk,
                    destinationColumn,
                    targetRow);
            }
        }
    }

    private void CopyDestinationColumns(
        Archetype sourceArchetype,
        Chunk sourceChunk,
        int sourceRow,
        Archetype targetArchetype,
        Chunk targetChunk,
        int targetRow,
        Archetype destinationArchetype,
        Chunk destinationChunk,
        int destinationRow,
        EntityCopyOptions surface)
    {
        for (int destinationColumn = 0; destinationColumn < destinationArchetype.TableComponentIds.Length; destinationColumn++)
        {
            int componentId = destinationArchetype.TableComponentIds[destinationColumn];
            if (CopyRules.CopyColumn(componentId, sourceArchetype, surface))
            {
                int sourceColumn = sourceArchetype.Column(componentId);
                ref readonly ComponentOperations operations =
                    ref destinationArchetype.ColumnOperations[destinationColumn];
                sourceChunk.CopyComponentTo(
                    sourceColumn,
                    sourceRow,
                    destinationChunk,
                    destinationColumn,
                    destinationRow,
                    in operations);

                if (targetArchetype.HasComponent(componentId))
                    MarkWrite(destinationChunk, destinationColumn, destinationRow);
                else
                    MarkAdd(destinationChunk, destinationColumn, destinationRow);
                continue;
            }

            if (!targetArchetype.TryColumn(componentId, out int targetColumn))
                continue;

            ref readonly ComponentOperations destinationOperations =
                ref destinationArchetype.ColumnOperations[destinationColumn];
            targetChunk.CopyComponentTo(
                targetColumn,
                targetRow,
                destinationChunk,
                destinationColumn,
                destinationRow,
                in destinationOperations);

            targetChunk.CopyVersions(
                targetColumn,
                targetRow,
                destinationColumn,
                destinationRow,
                destinationChunk);
        }
    }

    private void CopyEnableableState(
        Archetype sourceArchetype,
        Chunk sourceChunk,
        int sourceRow,
        Archetype targetArchetype,
        Chunk targetChunk,
        int targetRow,
        Archetype destinationArchetype,
        Chunk destinationChunk,
        int destinationRow,
        EntityCopyOptions surface)
    {
        for (int i = 0; i < destinationArchetype.EnableableComponentIds.Length; i++)
        {
            int componentId = destinationArchetype.EnableableComponentIds[i];
            bool enabled = true;
            if (surface.HasFlag(EntityCopyOptions.EnableableState) &&
                CopyRules.CopySource(componentId, surface) &&
                sourceArchetype.TryMask(componentId, out int sourceMaskIndex))
            {
                enabled = sourceChunk.IsEnabled(sourceMaskIndex, sourceRow);
            }
            else if (targetArchetype.TryMask(componentId, out int targetMaskIndex))
            {
                enabled = targetChunk.IsEnabled(targetMaskIndex, targetRow);
            }

            int destinationMaskIndex = destinationArchetype.EnableMask(componentId);
            destinationChunk.WriteEnabled(destinationMaskIndex, destinationRow, enabled);
        }
    }

    private void FinalizeCopiedColumns(
        Entity target,
        Archetype sourceArchetype,
        Archetype targetArchetype,
        Archetype destinationArchetype,
        Chunk destinationChunk,
        int destinationRow,
        EntityCopyOptions surface)
    {
        for (int column = 0; column < destinationArchetype.TableComponentIds.Length; column++)
        {
            int componentId = destinationArchetype.TableComponentIds[column];
            if (!CopyRules.CopyColumn(componentId, sourceArchetype, surface))
                continue;

            bool alreadyHadComponent = targetArchetype.HasComponent(componentId);
            if (alreadyHadComponent)
                continue;

            _components.CommitAdd(
                target,
                componentId,
                destinationChunk,
                column,
                destinationRow);
        }
    }

    private void MarkAdd(Chunk chunk, int columnIndex, int row)
    {
        chunk.MarkAdd(columnIndex, row, _clock.Tick);
    }

    private void MarkWrite(Chunk chunk, int columnIndex, int row)
    {
        chunk.MarkWrite(columnIndex, row, _clock.Tick);
    }

    private static class ComponentChanges
    {
        public static List<OldComponent>? CaptureRemoved(
            Copy copy,
            Entity target,
            Archetype targetArchetype,
            Chunk targetChunk,
            int targetRow,
            Archetype destinationArchetype)
        {
            List<OldComponent>? removed = null;
            for (int column = 0; column < targetArchetype.TableComponentIds.Length; column++)
            {
                int componentId = targetArchetype.TableComponentIds[column];
                if (destinationArchetype.HasComponent(componentId))
                    continue;

                copy._hierarchy.TrackParent(target, componentId);
                copy._indices.Drop(target, componentId, targetChunk, column, targetRow);

                if (CopyRules.TryBufferCopier(componentId, out _))
                {
                    continue;
                }
                else if (!CopyRules.IsBufferPart(componentId))
                {
                    removed ??= new List<OldComponent>();
                    removed.Add(new OldComponent(
                        componentId,
                        targetChunk.CaptureComponentValue(
                            column,
                            targetRow,
                            in targetArchetype.ColumnOperations[column])));
                }
            }

            return removed;
        }

        public static void CommitRemoved(Copy copy, Entity target, List<OldComponent>? removed)
        {
            if (removed is null)
                return;

            for (int i = 0; i < removed.Count; i++)
                copy._components.CommitRemove(removed[i].ComponentId, target, removed[i].Column);
        }

        public static List<OldComponent>? CaptureReplaced(
            Copy copy,
            Entity target,
            Archetype sourceArchetype,
            Archetype targetArchetype,
            Chunk targetChunk,
            int targetRow,
            Archetype destinationArchetype,
            EntityCopyOptions surface)
        {
            List<OldComponent>? replaced = null;
            for (int column = 0; column < targetArchetype.TableComponentIds.Length; column++)
            {
                int componentId = targetArchetype.TableComponentIds[column];
                if (!destinationArchetype.HasComponent(componentId) ||
                    !CopyRules.CopyColumn(componentId, sourceArchetype, surface) ||
                    CopyRules.IsBufferPart(componentId))
                {
                    continue;
                }

                copy._hierarchy.TrackParent(target, componentId);
                copy._indices.Drop(target, componentId, targetChunk, column, targetRow);
                replaced ??= new List<OldComponent>();
                replaced.Add(new OldComponent(
                    componentId,
                    targetChunk.CaptureComponentValue(
                        column,
                        targetRow,
                        in targetArchetype.ColumnOperations[column])));
            }

            return replaced;
        }

        public static void CommitReplaced(
            Copy copy,
            Entity target,
            List<OldComponent>? replaced,
            Archetype destinationArchetype,
            Chunk destinationChunk,
            int destinationRow)
        {
            if (replaced is null)
                return;

            for (int i = 0; i < replaced.Count; i++)
            {
                int column = destinationArchetype.Column(replaced[i].ComponentId);
                copy._components.CommitReplace(
                    replaced[i].ComponentId,
                    target,
                    replaced[i].Column,
                    destinationChunk,
                    column,
                    destinationRow);
            }
        }

        public readonly record struct OldComponent(int ComponentId, Array Column);
    }

    private static class ExtraSurface
    {
        public static void CopyBuffers(
            Buffers buffers,
            Entity source,
            Entity target,
            Archetype sourceArchetype,
            Archetype targetArchetype,
            Archetype destinationArchetype)
        {
            foreach (int componentId in sourceArchetype.ComponentIds)
            {
                if (!CopyRules.TryBufferCopier(componentId, out var operations))
                    continue;

                if (!destinationArchetype.HasComponent(operations.HeaderComponentId) ||
                    !destinationArchetype.HasComponent(operations.InlineComponentId))
                {
                    continue;
                }

                bool targetHadBuffer =
                    targetArchetype.HasComponent(operations.HeaderComponentId) &&
                    targetArchetype.HasComponent(operations.InlineComponentId);
                if (targetHadBuffer)
                    operations.ReplaceCopy(buffers, source, target);
                else
                    operations.AddCopy(buffers, source, target);
            }
        }
    }

    private static class CopyRules
    {
        public static bool PreserveTarget(int componentId, EntityCopyOptions surface)
        {
            ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentId);
            return info.Storage switch
            {
                StoragePath.Table => IsBufferPart(componentId)
                    ? !surface.HasFlag(EntityCopyOptions.DynamicBuffers)
                    : info.IsCleanup
                        ? !surface.HasFlag(EntityCopyOptions.CleanupComponents)
                        : !surface.HasFlag(EntityCopyOptions.TableComponents),
                StoragePath.Tag => !surface.HasFlag(EntityCopyOptions.Tags),
                StoragePath.Shared => !surface.HasFlag(EntityCopyOptions.SharedComponents),
                _ => false,
            };
        }

        public static bool CopySource(int componentId, EntityCopyOptions surface)
        {
            ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentId);
            return info.Storage switch
            {
                StoragePath.Table => IsBufferPart(componentId)
                    ? surface.HasFlag(EntityCopyOptions.DynamicBuffers)
                    : info.IsCleanup
                        ? surface.HasFlag(EntityCopyOptions.CleanupComponents)
                        : surface.HasFlag(EntityCopyOptions.TableComponents),
                StoragePath.Tag => surface.HasFlag(EntityCopyOptions.Tags),
                StoragePath.Shared => surface.HasFlag(EntityCopyOptions.SharedComponents),
                _ => false,
            };
        }

        public static bool CopyColumn(
            int componentId,
            Archetype sourceArchetype,
            EntityCopyOptions surface)
        {
            return sourceArchetype.HasComponent(componentId) &&
                   CopySource(componentId, surface) &&
                   !IsBufferPart(componentId);
        }

        public static bool IsBufferPart(int componentId)
        {
            return BufferRegistry.IsGraphId(componentId);
        }

        public static bool TryBufferCopier(
            int componentId,
            out IBufferCopier operations)
        {
            return BufferRegistry.TryHeader(componentId, out operations);
        }

        public static void AddUnique(List<int> ids, int componentId)
        {
            if (!ids.Contains(componentId))
                ids.Add(componentId);
        }
    }
}

