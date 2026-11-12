using System.Buffers;
using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Hierarchy;

public interface IHierarchyDomain;
public readonly struct DefaultHierarchyDomain : IHierarchyDomain;

public readonly record struct Parent<TDomain>(Entity Value) : IAddedComponent, IRemovedComponent,
    IControlledComponent, IComponentInitialization<Parent<TDomain>>,
    IEntityReferenceValue<Parent<TDomain>>, IEntityReferenceOperations<Parent<TDomain>> where TDomain : struct, IHierarchyDomain
{
    static void IEntityReferenceValue<Parent<TDomain>>.Remap(ref Parent<TDomain> value, in EntityMap entities)
        => value = new(entities.Map(value.Value));
    void IEntityReferenceOperations<Parent<TDomain>>.Remap(ref Parent<TDomain> value, in EntityMap entities)
        => value = new(entities.Map(value.Value));
    void IComponentInitialization<Parent<TDomain>>.ValidateCreate(World world, in Parent<TDomain> value)
    {
        if (value.Value != Entity.Null) HierarchyData<TDomain>.RequireActive(world, value.Value);
    }
    void IComponentInitialization<Parent<TDomain>>.Initialize(World world, Entity entity)
    {
        if (world.Read<Parent<TDomain>>(entity).Value == Entity.Null) world.ClearComputed<Parent<TDomain>>(entity);
        else Hierarchy<TDomain>.Data(world).InitializeChild(entity);
    }

}

public readonly struct Children<TDomain> : IAddedComponent, IRemovedComponent, IComputedComponent,
    IComponentInitialization<Children<TDomain>>, IComponentOperations<Children<TDomain>>,
    IEntityReferenceValue<Children<TDomain>>, IEntityReferenceOperations<Children<TDomain>> where TDomain : struct, IHierarchyDomain
{
    internal readonly Entity[]? Items;
    internal readonly int CountFlags;
    public int Count => CountFlags & int.MaxValue;
    public bool IsOrdered => CountFlags < 0;
    public ReadOnlySpan<Entity> Entities => Items.AsSpan(0, Count);
    static void IEntityReferenceValue<Children<TDomain>>.Remap(ref Children<TDomain> value, in EntityMap entities)
    {
        for (int i = 0; i < value.Count; i++) value.Items![i] = entities.Map(value.Items[i]);
    }
    void IEntityReferenceOperations<Children<TDomain>>.Remap(ref Children<TDomain> value, in EntityMap entities)
    {
        for (int index = 0; index < value.Count; index++) value.Items![index] = entities.Map(value.Items[index]);
    }
    void IComponentInitialization<Children<TDomain>>.ValidateCreate(World world, in Children<TDomain> value) { }
    void IComponentInitialization<Children<TDomain>>.Initialize(World world, Entity entity) => world.ClearComputed<Children<TDomain>>(entity);
    internal Children(Entity[]? items, int count, bool ordered) => (Items, CountFlags) = (items, count | (ordered ? int.MinValue : 0));

    Children<TDomain> IComponentOperations<Children<TDomain>>.Clone(World world, in Children<TDomain> value) => Copy(value);
    void IComponentOperations<Children<TDomain>>.Release(World world, ref Children<TDomain> value) => Release(ref value);

    internal static Children<TDomain> Copy(in Children<TDomain> value)
    {
        if (value.Count == 0) return new(null, 0, value.IsOrdered);
        Entity[] items = Rent(value.Count);
        value.Entities.CopyTo(items);
        return new(items, value.Count, value.IsOrdered);
    }

    internal static void Release(ref Children<TDomain> value)
    {
        if (value.Items is { } items) Return(items);
        value = default;
    }

    internal static Entity[] Rent(int count) => count <= 8
        ? new Entity[BitOperations.RoundUpToPowerOf2((uint)Math.Max(count, 1))]
        : ArrayPool<Entity>.Shared.Rent(count);

    internal static void Return(Entity[] items)
    {
        if (items.Length >= 16) ArrayPool<Entity>.Shared.Return(items);
    }
}

public readonly ref struct ChildrenRead
{
    private readonly World _world;
    public ReadOnlySpan<Entity> Entities { get; }
    public int Count => Entities.Length;
    internal ChildrenRead(World world, ReadOnlySpan<Entity> entities) { _world = world; Entities = entities; }
    public ReadOnlySpan<Entity>.Enumerator GetEnumerator() => Entities.GetEnumerator();
    public void Dispose() => _world.EndValueRead();
}

public readonly record struct ParentEdit(Entity Child, Entity Parent, int InsertAt = -1);
