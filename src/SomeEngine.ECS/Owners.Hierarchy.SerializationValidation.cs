using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.ECS.Owners;

internal sealed partial class HierarchyDomainStore<TDomain>
    where TDomain : IHierarchyDomain
{
    private void ValidateParentEndpoints(Entity child, Entity parent)
    {
        _owner.EnsureAlive(child, "child");
        if (parent == Entity.Null)
            throw new InvalidOperationException("Parent cannot be Entity.Null; use Detach.");

        _owner.EnsureAlive(parent, "parent");
        if (child == parent)
            throw new InvalidOperationException("Entity cannot be its own parent.");
    }

    internal sealed partial class TopologyImport
    {
        private const byte Visiting = 1;
        private const byte Done = 2;

        private Dictionary<Entity, int>? _parentImportOrdinals;
        private bool _parentsSealed;
        private long _cycleValidationPasses;
        private long _cycleValidationEntityVisits;

        internal long CycleValidationPasses => _cycleValidationPasses;

        internal long CycleValidationEntityVisits => _cycleValidationEntityVisits;

        internal int RetainedCycleMetadataCount => _parentImportOrdinals?.Count ?? 0;

        internal long CanonicalParentFullScanCount =>
            _store.CanonicalParentFullScanCount;

        internal int RetainedAllocationMetadataCount =>
            _childrenPerParent?.Count ?? 0;

        internal void SetOrderedSequenceCount(int count)
        {
            RequireOpen();
            RequireParentsSealed();
            if (_expectedOrderedSequenceCount >= 0)
            {
                throw new InvalidOperationException(
                    "Hierarchy ordered sequence count is already set.");
            }
            _expectedOrderedSequenceCount = count;
        }

        internal void SealParents()
        {
            RequireOpen();
            RequireParentsOpen();
            if (_parentCount != _expectedParentCount)
            {
                throw new InvalidDataException(
                    $"Hierarchy Parent payload declared {_expectedParentCount} entries but supplied {_parentCount}.");
            }

            _cycleValidationPasses++;
            var states = new Dictionary<Entity, byte>(_parentCount);
            var path = new List<Entity>();
            int earliestClosingOrdinal = int.MaxValue;
            Entity earliestClosingChild = Entity.Null;

            foreach (Entity start in _store._appliedParents.Keys)
            {
                if (states.TryGetValue(start, out byte startState) && startState == Done)
                    continue;

                path.Clear();
                Entity current = start;
                while (_store._appliedParents.TryGetValue(current, out Entity parent))
                {
                    if (states.TryGetValue(current, out byte state))
                    {
                        if (state == Visiting)
                        {
                            int cycleStart = path.IndexOf(current);
                            if (cycleStart < 0)
                            {
                                throw new InvalidOperationException(
                                    "Hierarchy import cycle state is inconsistent.");
                            }

                            int closingOrdinal = -1;
                            Entity closingChild = Entity.Null;
                            for (int i = cycleStart; i < path.Count; i++)
                            {
                                Entity cycleChild = path[i];
                                int ordinal = _parentImportOrdinals![cycleChild];
                                if (ordinal > closingOrdinal)
                                {
                                    closingOrdinal = ordinal;
                                    closingChild = cycleChild;
                                }
                            }

                            if (closingOrdinal < earliestClosingOrdinal)
                            {
                                earliestClosingOrdinal = closingOrdinal;
                                earliestClosingChild = closingChild;
                            }
                        }
                        break;
                    }

                    states.Add(current, Visiting);
                    path.Add(current);
                    _cycleValidationEntityVisits++;
                    current = parent;
                }

                for (int i = 0; i < path.Count; i++)
                    states[path[i]] = Done;
            }

            if (earliestClosingChild != Entity.Null)
            {
                Entity closingParent = _store._appliedParents[earliestClosingChild];
                throw InvalidSerializedParent(
                    earliestClosingChild,
                    closingParent,
                    new InvalidOperationException("Parent would create a hierarchy cycle."));
            }

            _parentsSealed = true;
            _parentImportOrdinals = null;
        }

        private static InvalidDataException InvalidSerializedParent(
            Entity child,
            Entity parent,
            InvalidOperationException exception) =>
            new(
                $"Invalid serialized hierarchy Parent {child} -> {parent}.",
                exception);

        private void RequireParentsOpen()
        {
            if (_parentsSealed)
                throw new InvalidOperationException("Hierarchy Parent import is already sealed.");
        }

        private void RequireParentsSealed()
        {
            if (!_parentsSealed)
                throw new InvalidOperationException("Hierarchy Parent import has not been sealed.");
        }

        private void RequireOpen()
        {
            if (_completed)
                throw new InvalidOperationException("Hierarchy topology import is already complete.");
        }
    }
}
