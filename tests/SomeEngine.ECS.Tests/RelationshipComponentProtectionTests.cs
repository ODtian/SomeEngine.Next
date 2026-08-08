using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public sealed class RelationshipComponentProtectionTests
{
    [Fact]
    public void MetadataAndQueryCapabilities_DistinguishOrdinarySourceAndTarget()
    {
        Assert.False(ComponentMetadata<RegularValue>.IsRelationshipSource);
        Assert.False(ComponentMetadata<RegularValue>.IsRelationshipTarget);
        Assert.True(ComponentMetadata<RegularValue>.AllowsPublicStructuralMutation);
        Assert.True(ComponentMetadata<RegularValue>.AllowsPublicValueMutation);

        Assert.True(ComponentMetadata<SourceValue>.IsRelationshipSource);
        Assert.False(ComponentMetadata<SourceValue>.IsRelationshipTarget);
        Assert.False(ComponentMetadata<SourceValue>.AllowsPublicStructuralMutation);
        Assert.False(ComponentMetadata<SourceValue>.AllowsPublicValueMutation);

        Assert.False(ComponentMetadata<TargetValue>.IsRelationshipSource);
        Assert.True(ComponentMetadata<TargetValue>.IsRelationshipTarget);
        Assert.False(ComponentMetadata<TargetValue>.AllowsPublicStructuralMutation);
        Assert.False(ComponentMetadata<TargetValue>.AllowsPublicValueMutation);

        var source = QueryableTypeInfo.For<SourceValue>().Capabilities;
        Assert.True(source.HasFlag(QueryableCapabilities.Match));
        Assert.True(source.HasFlag(QueryableCapabilities.DataRead));
        Assert.True(source.HasFlag(QueryableCapabilities.DataWrite));
        Assert.True(source.HasFlag(QueryableCapabilities.ChangeFilter));

        var target = QueryableTypeInfo.For<TargetValue>().Capabilities;
        Assert.True(target.HasFlag(QueryableCapabilities.Match));
        Assert.True(target.HasFlag(QueryableCapabilities.DataRead));
        Assert.False(target.HasFlag(QueryableCapabilities.DataWrite));
        Assert.True(target.HasFlag(QueryableCapabilities.ChangeFilter));

        _ = new QueryDefinitionBuilder().ReadWrite<SourceValue>().Build();
        _ = new QueryDefinitionBuilder().Read<TargetValue>().Changed<TargetValue>().Build();
        Assert.Throws<InvalidOperationException>(
            () => new QueryDefinitionBuilder().Write<TargetValue>());
    }

    [Fact]
    public void RelationshipRoles_AreMutuallyExclusiveAndRequireTableStorage()
    {
        var both = Assert.Throws<TypeInitializationException>(
            () => _ = ComponentMetadata<BothRoles>.Id);
        Assert.IsType<InvalidOperationException>(both.InnerException);

        var sparse = Assert.Throws<TypeInitializationException>(
            () => _ = ComponentMetadata<SparseSource>.Id);
        Assert.IsType<InvalidOperationException>(sparse.InnerException);

        var enableable = Assert.Throws<TypeInitializationException>(
            () => _ = ComponentMetadata<EnableableSource>.Id);
        Assert.IsType<InvalidOperationException>(enableable.InnerException);

        var cleanup = Assert.Throws<TypeInitializationException>(
            () => _ = ComponentMetadata<CleanupTarget>.Id);
        Assert.IsType<InvalidOperationException>(cleanup.InnerException);
    }

    [Fact]
    public void WorldGenericMutation_RejectsSourceAndTarget_ButAllowsRead()
    {
        var world = new World();
        var sourceEntity = world.CreateEntity();
        var targetEntity = world.CreateEntity();
        world.AddRelationshipComponent(sourceEntity, new SourceValue { Value = 10 });
        world.AddRelationshipComponent(targetEntity, new TargetValue { Value = 20 });

        Assert.Equal(10, world.Read<SourceValue>(sourceEntity).Value);
        Assert.Equal(20, world.Read<TargetValue>(targetEntity).Value);

        AssertRelationshipMutationRejected(
            () => world.Add(world.CreateEntity(), new SourceValue()));
        AssertRelationshipMutationRejected(
            () => world.Add(world.CreateEntity(), new TargetValue()));
        AssertRelationshipMutationRejected(
            () => world.Replace(sourceEntity, new SourceValue { Value = 11 }));
        AssertRelationshipMutationRejected(
            () => world.Replace(targetEntity, new TargetValue { Value = 21 }));
        AssertRelationshipMutationRejected(() => world.Remove<SourceValue>(sourceEntity));
        AssertRelationshipMutationRejected(() => world.Remove<TargetValue>(targetEntity));

        Assert.Equal(10, world.Read<SourceValue>(sourceEntity).Value);
        Assert.Equal(20, world.Read<TargetValue>(targetEntity).Value);
    }

    [Fact]
    public void CommandAndBundleEntryPoints_RejectRelationshipMutationBeforeRecordingOrSpawning()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddRelationshipComponent(entity, new SourceValue { Value = 1 });
        using var commands = new CommandBuffer(world);

        AssertRelationshipMutationRejected(
            () => commands.Add(entity, new SourceValue { Value = 2 }));
        AssertRelationshipMutationRejected(
            () => commands.Add(entity, new TargetValue { Value = 2 }));
        AssertRelationshipMutationRejected(
            () => commands.Replace(entity, new SourceValue { Value = 2 }));
        AssertRelationshipMutationRejected(() => commands.Remove<SourceValue>(entity));
        Assert.Equal(0, commands.CommandCount);

        AssertRelationshipMutationRejected(
            () => world.CreateEntity(new SourceValue { Value = 3 }));

        int[] sourceIds = [ComponentMetadata<SourceValue>.Id];
        int[] targetIds = [ComponentMetadata<TargetValue>.Id];
        AssertRelationshipMutationRejected(() =>
            world.ExecuteBundleSpawn(targetIds, static _ => { }));
        AssertRelationshipMutationRejected(() =>
            world.ExecuteBundleSpawnBatch(targetIds, 1, static _ => { }));
        AssertRelationshipMutationRejected(() =>
            world.ExecuteBundleAdd(entity, targetIds, static _ => { }));
        AssertRelationshipMutationRejected(() =>
            world.ExecuteBundleReplace(entity, sourceIds, static _ => { }));

        Assert.Equal(1, world.EntityCount);
        Assert.Equal(1, world.Read<SourceValue>(entity).Value);
    }

    [Fact]
    public void EntityCopy_RejectsRelationshipComponentsInSelectedTableSurface()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity(new RegularValue { Value = 4 });
        world.AddRelationshipComponent(source, new SourceValue { Value = 1 });

        AssertRelationshipMutationRejected(() => world.CloneEntity(source));
        AssertRelationshipMutationRejected(() => world.CopyEntity(source, target));

        var derivedTarget = world.CreateEntity();
        world.AddRelationshipComponent(derivedTarget, new TargetValue { Value = 5 });
        AssertRelationshipMutationRejected(() => world.CopyEntity(target, derivedTarget));

        Entity cloneWithoutTable = world.CloneEntity(source, EntityCopyOptions.Tags);
        Assert.True(world.IsAlive(cloneWithoutTable));
        Assert.False(world.Has<SourceValue>(cloneWithoutTable));
    }

    [Fact]
    public void OrdinaryComponentMutation_RemainsUnchanged()
    {
        var world = new World();
        var entity = world.CreateEntity(new RegularValue { Value = 1 });

        world.Replace(entity, new RegularValue { Value = 2 });
        Assert.Equal(2, world.Read<RegularValue>(entity).Value);
        world.Replace(entity, new RegularValue { Value = 3 });
        _ = new QueryDefinitionBuilder().ReadWrite<RegularValue>().Build();

        Assert.Equal(3, world.Read<RegularValue>(entity).Value);
        world.Remove<RegularValue>(entity);
        Assert.False(world.Has<RegularValue>(entity));
    }

    [Fact]
    public void PublicHooks_CannotReplaceRelationshipLifecycleMaintenance()
    {
        var world = new World();

        var source = Assert.Throws<InvalidOperationException>(() => world.Hooks<SourceValue>());
        var target = Assert.Throws<InvalidOperationException>(() => world.Hooks<TargetValue>());

        Assert.Contains("ECS-internal", source.Message);
        Assert.Contains("Added/Changed/Removed", source.Message);
        Assert.Contains("ECS-internal", target.Message);
    }

    private static void AssertRelationshipMutationRejected(Action action)
    {
        var error = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains("typed relationship API", error.Message);
    }

    private struct RegularValue : IComponent
    {
        public int Value;
    }

    private struct SourceValue : IRelationshipSource
    {
        public int Value;
    }

    private struct TargetValue : IRelationshipTarget
    {
        public int Value;
    }

    private struct BothRoles : IRelationshipSource, IRelationshipTarget;

    private struct SparseSource : IRelationshipSource, ISparseComponent;

    private struct EnableableSource : IRelationshipSource, IEnableableComponent;

    private struct CleanupTarget : IRelationshipTarget, ICleanupComponent;
}
