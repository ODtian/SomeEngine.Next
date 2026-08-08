using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

public partial class World
{
    public Entity ExecuteBundleSpawn(
        ReadOnlySpan<int> componentIds,
        BundleWriteAction action) =>
        ExecuteBundleSpawn(componentIds, ReadOnlySpan<int>.Empty, action);

    public Entity ExecuteBundleSpawn(
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        BundleWriteAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        PublicComponentMutationGuard.Structural(componentIds, nameof(ExecuteBundleSpawn));
        PublicComponentMutationGuard.Structural(sparseComponentIds, nameof(ExecuteBundleSpawn));
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        using StructuralMutationScope mutation = BeginStructuralMutation();
        Entity entity = Bundles.ExecuteSpawn(componentIds, sparseComponentIds, action);
        mutation.Commit();
        return entity;
    }

    public Entity ExecuteBundleSpawn<TState>(
        ReadOnlySpan<int> componentIds,
        ref TState state,
        BundleWriteAction<TState> action) =>
        ExecuteBundleSpawn(
            componentIds,
            ReadOnlySpan<int>.Empty,
            ref state,
            action);

    public Entity ExecuteBundleSpawn<TState>(
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        ref TState state,
        BundleWriteAction<TState> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        PublicComponentMutationGuard.Structural(componentIds, nameof(ExecuteBundleSpawn));
        PublicComponentMutationGuard.Structural(sparseComponentIds, nameof(ExecuteBundleSpawn));
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        using StructuralMutationScope mutation = BeginStructuralMutation();
        Entity entity = Bundles.ExecuteSpawn(componentIds, sparseComponentIds, ref state, action);
        mutation.Commit();
        return entity;
    }

    public void ExecuteBundleAdd(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        BundleWriteAction action) =>
        ExecuteBundleAdd(entity, componentIds, ReadOnlySpan<int>.Empty, action);

    public void ExecuteBundleAdd(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        BundleWriteAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        PublicComponentMutationGuard.Structural(componentIds, nameof(ExecuteBundleAdd));
        PublicComponentMutationGuard.Structural(sparseComponentIds, nameof(ExecuteBundleAdd));
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        using StructuralMutationScope mutation = BeginStructuralMutation();
        Bundles.ExecuteAdd(entity, componentIds, sparseComponentIds, action);
        mutation.Commit();
    }

    public void ExecuteBundleAdd<TState>(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        ref TState state,
        BundleWriteAction<TState> action) =>
        ExecuteBundleAdd(
            entity,
            componentIds,
            ReadOnlySpan<int>.Empty,
            ref state,
            action);

    public void ExecuteBundleAdd<TState>(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        ref TState state,
        BundleWriteAction<TState> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        PublicComponentMutationGuard.Structural(componentIds, nameof(ExecuteBundleAdd));
        PublicComponentMutationGuard.Structural(sparseComponentIds, nameof(ExecuteBundleAdd));
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        using StructuralMutationScope mutation = BeginStructuralMutation();
        Bundles.ExecuteAdd(entity, componentIds, sparseComponentIds, ref state, action);
        mutation.Commit();
    }

    public void ExecuteBundleReplace(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        BundleWriteAction action) =>
        ExecuteBundleReplace(entity, componentIds, ReadOnlySpan<int>.Empty, action);

    public void ExecuteBundleReplace(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        BundleWriteAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        PublicComponentMutationGuard.Value(componentIds, nameof(ExecuteBundleReplace));
        PublicComponentMutationGuard.Value(sparseComponentIds, nameof(ExecuteBundleReplace));
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        using StructuralMutationScope mutation = BeginStructuralMutation();
        Bundles.ExecuteReplace(entity, componentIds, sparseComponentIds, action);
        mutation.Commit();
    }

    public void ExecuteBundleReplace<TState>(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        ref TState state,
        BundleWriteAction<TState> action) =>
        ExecuteBundleReplace(
            entity,
            componentIds,
            ReadOnlySpan<int>.Empty,
            ref state,
            action);

    public void ExecuteBundleReplace<TState>(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        ref TState state,
        BundleWriteAction<TState> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        PublicComponentMutationGuard.Value(componentIds, nameof(ExecuteBundleReplace));
        PublicComponentMutationGuard.Value(sparseComponentIds, nameof(ExecuteBundleReplace));
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        using StructuralMutationScope mutation = BeginStructuralMutation();
        Bundles.ExecuteReplace(entity, componentIds, sparseComponentIds, ref state, action);
        mutation.Commit();
    }

    public void ExecuteBundleSpawnBatch(
        ReadOnlySpan<int> componentIds,
        int count,
        BundleWriteAction action) =>
        ExecuteBundleSpawnBatch(componentIds, ReadOnlySpan<int>.Empty, count, action);

    public void ExecuteBundleSpawnBatch(
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        int count,
        BundleWriteAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        PublicComponentMutationGuard.Structural(componentIds, nameof(ExecuteBundleSpawnBatch));
        PublicComponentMutationGuard.Structural(sparseComponentIds, nameof(ExecuteBundleSpawnBatch));
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        using StructuralMutationScope mutation = BeginStructuralMutation();
        Bundles.ExecuteSpawnBatch(componentIds, sparseComponentIds, count, action);
        mutation.Commit();
    }

    public void ExecuteBundleSpawnBatch<TState>(
        ReadOnlySpan<int> componentIds,
        int count,
        ref TState state,
        BundleWriteAction<TState> action) =>
        ExecuteBundleSpawnBatch(
            componentIds,
            ReadOnlySpan<int>.Empty,
            count,
            ref state,
            action);

    public void ExecuteBundleSpawnBatch<TState>(
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        int count,
        ref TState state,
        BundleWriteAction<TState> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        PublicComponentMutationGuard.Structural(componentIds, nameof(ExecuteBundleSpawnBatch));
        PublicComponentMutationGuard.Structural(sparseComponentIds, nameof(ExecuteBundleSpawnBatch));
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        using StructuralMutationScope mutation = BeginStructuralMutation();
        Bundles.ExecuteSpawnBatch(componentIds, sparseComponentIds, count, ref state, action);
        mutation.Commit();
    }

    public void ReserveBundle(ReadOnlySpan<int> componentIds, int entityCapacity)
    {
        PublicComponentMutationGuard.Structural(componentIds, nameof(ReserveBundle));
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        Bundles.Reserve(componentIds, entityCapacity);
    }

    internal void ExecuteBundleLoad<TState>(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        ref TState state,
        BundleWriteAction<TState> action)
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        using StructuralMutationScope mutation = BeginStructuralMutation();
        Bundles.ExecuteLoad(entity, componentIds, sparseComponentIds, ref state, action);
        mutation.Commit();
    }
}
