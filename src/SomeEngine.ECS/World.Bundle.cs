using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS;

public partial class World
{
    public BundleWriter CreateSpawnWriter(Span<int> componentIds)
    {
        return Bundles.CreateSpawnWriter(componentIds);
    }

    public BundleWriter CreateSpawnWriter(
        Span<int> componentIds,
        ReadOnlySpan<SharedValueSlot> sharedValues)
    {
        return Bundles.CreateSpawnWriter(componentIds, sharedValues);
    }

    public BundleWriter CreateAddWriter(Entity entity, Span<int> componentIds)
    {
        return CreateAddWriter(
            entity,
            componentIds,
            ReadOnlySpan<SharedValueSlot>.Empty,
            ReadOnlySpan<int>.Empty);
    }

    public BundleWriter CreateAddWriter(
        Entity entity,
        Span<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds)
    {
        return CreateAddWriter(
            entity,
            componentIds,
            ReadOnlySpan<SharedValueSlot>.Empty,
            sparseComponentIds);
    }

    public BundleWriter CreateAddWriter(
        Entity entity,
        Span<int> componentIds,
        ReadOnlySpan<SharedValueSlot> sharedValues,
        ReadOnlySpan<int> sparseComponentIds)
    {
        return Bundles.CreateAddWriter(entity, componentIds, sharedValues, sparseComponentIds);
    }

    public BundleWriter CreateReplaceWriter(Entity entity, Span<int> componentIds)
    {
        return CreateReplaceWriter(
            entity,
            componentIds,
            ReadOnlySpan<SharedValueSlot>.Empty,
            ReadOnlySpan<int>.Empty);
    }

    public BundleWriter CreateReplaceWriter(
        Entity entity,
        Span<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds)
    {
        return CreateReplaceWriter(
            entity,
            componentIds,
            ReadOnlySpan<SharedValueSlot>.Empty,
            sparseComponentIds);
    }

    public BundleWriter CreateReplaceWriter(
        Entity entity,
        Span<int> componentIds,
        ReadOnlySpan<SharedValueSlot> sharedValues,
        ReadOnlySpan<int> sparseComponentIds)
    {
        return Bundles.CreateReplaceWriter(entity, componentIds, sharedValues, sparseComponentIds);
    }

    public SharedValueSlot SharedValue<T>(in SharedComponentValue<T> value)
        where T : struct, ISharedComponent
    {
        return Bundles.SharedValue(in value);
    }

    public void ReserveBundle(ReadOnlySpan<int> componentIds, int entityCapacity)
    {
        Bundles.Reserve(componentIds, entityCapacity);
    }

    internal BundleWriter CreateLoadWriter(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<SharedValueSlot> sharedValues)
    {
        return Bundles.CreateLoadWriter(entity, componentIds, sharedValues);
    }
}

