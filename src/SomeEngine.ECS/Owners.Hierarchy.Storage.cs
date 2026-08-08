using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.ECS.Owners;

internal sealed partial class HierarchyDomainStore<TDomain>
    where TDomain : IHierarchyDomain
{
    private int CompareMaintenanceOrder(Entity left, Entity right)
    {
        bool leftPending = _pendingPlacements.TryGetValue(left, out var leftPlacement);
        bool rightPending = _pendingPlacements.TryGetValue(right, out var rightPlacement);
        if (leftPending && rightPending)
        {
            int sequence = leftPlacement.Sequence.CompareTo(rightPlacement.Sequence);
            if (sequence != 0)
                return sequence;
        }
        else if (leftPending != rightPending)
        {
            return leftPending ? -1 : 1;
        }

        return EntityComparer.Instance.Compare(left, right);
    }

    private readonly record struct PendingChildPlacement(int? InsertIndex, long Sequence);

    private Entity AppliedParent(Entity child) =>
        _appliedParents.TryGetValue(child, out var parent) ? parent : Entity.Null;

    private ChildOrderPolicy Policy(Entity parent) =>
        _policies.TryGetValue(parent, out var policy)
            ? policy
            : ChildOrderPolicy.Unordered;

    private bool TryGetShard(Entity parent, out ChildShard shard)
    {
        if (Policy(parent) == ChildOrderPolicy.Ordered)
        {
            if (_ordered.TryGetValue(parent, out var ordered))
            {
                shard = ordered;
                return true;
            }
        }
        else if (_unordered.TryGetValue(parent, out var unordered))
        {
            shard = unordered;
            return true;
        }

        shard = null!;
        return false;
    }

    private ulong NextGeneration()
    {
        ulong next = ++_generation;
        if (next == 0)
            next = _generation = 1;
        return next;
    }

    private static long NextInverseRevision()
    {
        long revision = Interlocked.Increment(ref s_nextInverseRevision);
        return revision > 0
            ? revision
            : throw new InvalidOperationException(
                "Hierarchy inverse revision space was exhausted.");
    }

    private static void ValidatePermutation(
        ReadOnlySpan<Entity> current,
        ReadOnlySpan<Entity> permutation)
    {
        if (current.Length != permutation.Length)
            throw new InvalidOperationException("Child permutation must contain every child exactly once.");

        for (int i = 0; i < permutation.Length; i++)
        {
            bool found = false;
            for (int j = 0; j < current.Length; j++)
            {
                if (permutation[i] == current[j])
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                throw new InvalidOperationException("Child permutation contains an unknown entity.");

            for (int j = 0; j < i; j++)
            {
                if (permutation[i] == permutation[j])
                    throw new InvalidOperationException("Child permutation contains a duplicate entity.");
            }
        }
    }

    private static Entity[] StableEntities(ReadOnlySpan<Entity> entities)
    {
        Entity[] result = entities.ToArray();
        Array.Sort(result, EntityComparer.Instance);
        return result;
    }

    private static void ValidateReorderIndex(int count, int index)
    {
        int maximum = Math.Max(0, count - 1);
        if (count == 0 || (uint)index > (uint)maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                $"Reorder index must be in [0, {maximum}].");
        }
    }

    private static ParentPreimage ToPreimage(CanonicalParent value) =>
        new(value.HasParent, value.Parent);

    private static CanonicalParent FromPreimage(ParentPreimage value) =>
        new(value.HadParent, value.Parent);

    private readonly record struct CanonicalParent(bool HasParent, Entity Parent)
    {
        public static CanonicalParent None => new(false, Entity.Null);
    }

    private readonly record struct ParentPreimage(bool HadParent, Entity Parent);

    /// <summary>
    /// Shared marker for the set of mutable containers above. CloneDetached marks the generation
    /// before handing it to another wrapper, which forces either the source or the candidate to
    /// copy before modifying a collection or a physical child shard.
    /// </summary>
    private sealed class HierarchyDomainGeneration
    {
        private int _shared;

        internal bool IsShared => Volatile.Read(ref _shared) != 0;

        internal void MarkShared() => Volatile.Write(ref _shared, 1);
    }

    private sealed class EntityComparer : IComparer<Entity>
    {
        internal static readonly EntityComparer Instance = new();

        public int Compare(Entity left, Entity right)
        {
            int index = left.Index.CompareTo(right.Index);
            return index != 0 ? index : left.Generation.CompareTo(right.Generation);
        }
    }

    private abstract class ChildShard
    {
        public abstract int Count { get; }

        public abstract ReadOnlySpan<Entity> Items { get; }

        public abstract bool Contains(Entity child);

        public abstract void Add(Entity child, int? insertIndex);

        public abstract bool Remove(Entity child);

        public abstract Entity[] PublishSnapshot();
    }

    /// <summary>
    /// Packed swap-remove shard. The dictionary is a physical lookup index, not semantic order;
    /// no order key, sequence or reorder path exists in this type.
    /// </summary>
    private sealed class UnorderedChildShard : ChildShard
    {
        private Entity[] _items;
        private int _count;
        private bool _published;
        private readonly Dictionary<Entity, int> _indices;

        public UnorderedChildShard()
        {
            _items = Array.Empty<Entity>();
            _indices = new Dictionary<Entity, int>();
        }

        public UnorderedChildShard(ReadOnlySpan<Entity> children) : this()
        {
            EnsureWritable(children.Length);
            for (int i = 0; i < children.Length; i++)
                Add(children[i], insertIndex: null);
        }

        public UnorderedChildShard(UnorderedChildShard source)
        {
            _items = source.Items.ToArray();
            _count = _items.Length;
            _indices = new Dictionary<Entity, int>(source._indices);
        }

        private UnorderedChildShard(int importCount)
        {
            _items = importCount == 0 ? Array.Empty<Entity>() : new Entity[importCount];
            _indices = new Dictionary<Entity, int>(importCount);
        }

        internal static UnorderedChildShard CreateForImport(int count) =>
            new(count);

        public override ReadOnlySpan<Entity> Items => _items.AsSpan(0, _count);

        public override int Count => _count;

        internal bool IsImportComplete => _count == _items.Length;

        public override bool Contains(Entity child) => _indices.ContainsKey(child);

        public override void Add(Entity child, int? insertIndex)
        {
            if (insertIndex is not null)
                throw new InvalidOperationException("Unordered child shards do not accept an index.");
            if (_indices.ContainsKey(child))
                throw new InvalidOperationException("Child already exists in the unordered shard.");

            EnsureWritable(checked(_count + 1));
            _indices.Add(child, _count);
            _items[_count++] = child;
        }

        internal void AddImported(Entity child)
        {
            if (_published || _count >= _items.Length)
                throw new InvalidDataException("Hierarchy import exceeded its final unordered shard allocation.");
            if (!_indices.TryAdd(child, _count))
                throw new InvalidDataException($"Hierarchy import repeats unordered child {child}.");
            _items[_count++] = child;
        }

        public override bool Remove(Entity child)
        {
            if (!_indices.Remove(child, out int index))
                return false;

            EnsureWritable(_count);
            int last = _count - 1;
            if (index != last)
            {
                Entity moved = _items[last];
                _items[index] = moved;
                _indices[moved] = index;
            }
            _items[last] = default;
            _count--;
            return true;
        }

        public override Entity[] PublishSnapshot()
        {
            if (_count == 0)
                return Array.Empty<Entity>();
            if (_items.Length != _count)
                _items = Items.ToArray();
            _published = true;
            return _items;
        }

        private void EnsureWritable(int requiredCapacity)
        {
            if (!_published && requiredCapacity <= _items.Length)
                return;

            int capacity = Math.Max(requiredCapacity, Math.Max(4, _count * 2));
            var writable = new Entity[capacity];
            Items.CopyTo(writable);
            _items = writable;
            _published = false;
        }
    }

    private sealed class OrderedChildShard : ChildShard
    {
        private Entity[] _items;
        private int _count;
        private bool _published;
        private readonly Dictionary<Entity, int> _indices;
        private readonly TopologyOrderDiagnosticCounter _diagnostics;

        public OrderedChildShard(TopologyOrderDiagnosticCounter diagnostics)
        {
            _diagnostics = diagnostics;
            _items = Array.Empty<Entity>();
            _indices = new Dictionary<Entity, int>();
        }

        public OrderedChildShard(
            ReadOnlySpan<Entity> children,
            TopologyOrderDiagnosticCounter diagnostics) : this(diagnostics)
        {
            for (int i = 0; i < children.Length; i++)
                Add(children[i], insertIndex: null);
        }

        public OrderedChildShard(
            OrderedChildShard source,
            TopologyOrderDiagnosticCounter diagnostics)
            : this(source, diagnostics, recordCloneWork: true)
        {
        }

        internal OrderedChildShard(
            OrderedChildShard source,
            TopologyOrderDiagnosticCounter diagnostics,
            bool recordCloneWork)
        {
            _diagnostics = diagnostics;
            _items = source.Items.ToArray();
            _count = _items.Length;
            _indices = new Dictionary<Entity, int>(source._indices);
            if (recordCloneWork)
            {
                _diagnostics.RecordOrderedPath();
                _diagnostics.RecordOrderedIndexWork(_indices.Count);
            }
        }

        private OrderedChildShard(
            Entity[] ownedChildren,
            TopologyOrderDiagnosticCounter diagnostics)
        {
            _diagnostics = diagnostics;
            _items = ownedChildren;
            _count = ownedChildren.Length;
            _indices = new Dictionary<Entity, int>(ownedChildren.Length);
            for (int i = 0; i < ownedChildren.Length; i++)
                _indices.Add(ownedChildren[i], i);
        }

        internal static OrderedChildShard TakeOwnership(
            Entity[] ownedChildren,
            TopologyOrderDiagnosticCounter diagnostics) =>
            new(ownedChildren, diagnostics);

        public override ReadOnlySpan<Entity> Items => _items.AsSpan(0, _count);

        public override int Count => _count;

        public override bool Contains(Entity child) => _indices.ContainsKey(child);

        public int IndexOf(Entity child) =>
            _indices.TryGetValue(child, out int index) ? index : -1;

        public override void Add(Entity child, int? insertIndex)
        {
            _diagnostics.RecordOrderedPath();
            if (_indices.ContainsKey(child))
                throw new InvalidOperationException("Child already exists in the ordered shard.");

            int index = insertIndex ?? _count;
            if ((uint)index > (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(insertIndex));

            EnsureWritable(checked(_count + 1));
            if (index < _count)
                Array.Copy(_items, index, _items, index + 1, _count - index);
            _items[index] = child;
            _count++;
            RefreshIndices(index);
        }

        public override bool Remove(Entity child)
        {
            _diagnostics.RecordOrderedPath();
            if (!_indices.Remove(child, out int index))
                return false;

            _diagnostics.RecordOrderedIndexWork(1);
            EnsureWritable(_count);
            if (index < _count - 1)
                Array.Copy(_items, index + 1, _items, index, _count - index - 1);
            _items[--_count] = default;
            RefreshIndices(index);
            return true;
        }

        public void Reorder(Entity child, int insertIndex)
        {
            _diagnostics.RecordOrderedPath();
            if (!_indices.TryGetValue(child, out int oldIndex))
                throw new InvalidOperationException("Child is not present in the ordered shard.");
            ValidateReorderIndex(_count, insertIndex);
            if (oldIndex == insertIndex)
                return;

            EnsureWritable(_count);
            if (oldIndex < insertIndex)
                Array.Copy(_items, oldIndex + 1, _items, oldIndex, insertIndex - oldIndex);
            else
                Array.Copy(_items, insertIndex, _items, insertIndex + 1, oldIndex - insertIndex);
            _items[insertIndex] = child;
            RefreshIndices(Math.Min(oldIndex, insertIndex));
        }

        public override Entity[] PublishSnapshot()
        {
            if (_count == 0)
                return Array.Empty<Entity>();
            if (_items.Length != _count)
                _items = Items.ToArray();
            _published = true;
            return _items;
        }

        internal Entity[] PublishedBacking
        {
            get
            {
                if (!_published || _items.Length != _count)
                    throw new InvalidOperationException("Ordered hierarchy shard is not published.");
                return _items;
            }
        }

        private void EnsureWritable(int requiredCapacity)
        {
            if (!_published && requiredCapacity <= _items.Length)
                return;

            int capacity = Math.Max(requiredCapacity, Math.Max(4, _count * 2));
            var writable = new Entity[capacity];
            Items.CopyTo(writable);
            _items = writable;
            _published = false;
        }

        private void RefreshIndices(int start)
        {
            _diagnostics.RecordOrderedIndexWork(_count - start);
            for (int i = start; i < _count; i++)
                _indices[_items[i]] = i;
        }
    }
}
