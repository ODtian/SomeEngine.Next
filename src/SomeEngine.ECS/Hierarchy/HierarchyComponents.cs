using System.Collections;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Owners;

namespace SomeEngine.ECS.Hierarchy;

/// <summary>
/// Statically identifies an independent Parent/Children forest.
/// </summary>
public interface IHierarchyDomain;

/// <summary>
/// Domain used by the non-generic hierarchy convenience facade.
/// </summary>
public readonly struct DefaultHierarchyDomain : IHierarchyDomain;

/// <summary>
/// Canonical at-most-one-parent relationship for one hierarchy domain.
/// </summary>
/// <typeparam name="TDomain">The statically typed forest.</typeparam>
public struct Parent<TDomain> : IRelationshipSource, IHierarchyComponentRegistration
    where TDomain : IHierarchyDomain
{
    public Parent(Entity value)
    {
        Value = value;
    }

    public Entity Value;

    bool IHierarchyComponentRegistration.IsSource => true;

    IHierarchyDomainStore IHierarchyComponentRegistration.GetOrCreate(Owners.Hierarchy owner) =>
        owner.Domain<TDomain>();
}

/// <summary>
/// Opaque, read-only inverse token maintained from <see cref="Parent{TDomain}"/>.
/// The child list itself deliberately does not live in the component value.
/// </summary>
public readonly struct Children<TDomain> : IRelationshipTarget, IHierarchyComponentRegistration
    where TDomain : IHierarchyDomain
{
    bool IHierarchyComponentRegistration.IsSource => false;

    IHierarchyDomainStore IHierarchyComponentRegistration.GetOrCreate(Owners.Hierarchy owner) =>
        owner.Domain<TDomain>();
}

/// <summary>
/// Storage and enumeration policy local to one parent's direct children.
/// </summary>
public enum ChildOrderPolicy : byte
{
    Unordered,
    Ordered,
}

/// <summary>
/// Safe immutable snapshot of one applied Children generation.
/// </summary>
/// <remarks>
/// Publication replaces an immutable array rather than mutating it. Keeping this value alive pins
/// that array generation through read-only memory, so later hierarchy publication
/// cannot invalidate <see cref="Span"/> and repeated reads of an unchanged generation allocate
/// nothing.
/// </remarks>
public readonly struct HierarchyChildrenSnapshot<TDomain> : IReadOnlyList<Entity>
    where TDomain : IHierarchyDomain
{
    private readonly ReadOnlyMemory<Entity> _items;

    internal HierarchyChildrenSnapshot(ReadOnlyMemory<Entity> items, ulong generation)
    {
        _items = items;
        Generation = generation;
    }

    public int Count => _items.Length;

    public Entity this[int index] => _items.Span[index];

    public ReadOnlySpan<Entity> Span => _items.Span;

    public ulong Generation { get; }

    public Entity[] ToArray()
    {
        if (_items.IsEmpty)
            return Array.Empty<Entity>();

        var copy = new Entity[_items.Length];
        _items.Span.CopyTo(copy);
        return copy;
    }

    public Enumerator GetEnumerator() => new(_items);

    IEnumerator<Entity> IEnumerable<Entity>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<Entity>
    {
        private readonly ReadOnlyMemory<Entity> _items;
        private int _index;

        internal Enumerator(ReadOnlyMemory<Entity> items)
        {
            _items = items;
            _index = -1;
        }

        public Entity Current => _items.Span[_index];

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            int next = _index + 1;
            if ((uint)next >= (uint)_items.Length)
                return false;

            _index = next;
            return true;
        }

        public void Reset() => _index = -1;

        public void Dispose()
        {
        }
    }
}
