using SomeEngine.ECS.Hierarchy;
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
    public void GenericRelationshipMutationRequiresTypedHierarchyApi()
    {
        using var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();
        Hierarchy<ProtectionDomain>.SetParent(world, child, parent);
        Assert.Equal(parent, world.Read<Parent<ProtectionDomain>>(child).Value);
        Assert.Equal(new[] { child }, Hierarchy<ProtectionDomain>.GetChildren(world, parent).ToArray());
        AssertRelationshipMutationRejected(() => world.Add(world.CreateEntity(), new Parent<ProtectionDomain>(parent)));
        AssertRelationshipMutationRejected(() => world.Add(world.CreateEntity(), default(Children<ProtectionDomain>)));
        AssertRelationshipMutationRejected(() => world.Replace(child, new Parent<ProtectionDomain>(Entity.Null)));
        AssertRelationshipMutationRejected(() => world.Replace(parent, default(Children<ProtectionDomain>)));
        AssertRelationshipMutationRejected(() => world.Remove<Parent<ProtectionDomain>>(child));
        AssertRelationshipMutationRejected(() => world.Remove<Children<ProtectionDomain>>(parent));
        Assert.Equal(parent, Hierarchy<ProtectionDomain>.GetParent(world, child));
        Assert.Equal(new[] { child }, Hierarchy<ProtectionDomain>.GetChildren(world, parent).ToArray());
    }

    [Fact]
    public void CommandAndBundleRelationshipMutationsFailBeforeRecording()
    {
        using var world = new World();
        var parent = world.CreateEntity();
        var entity = world.CreateEntity();
        Hierarchy<ProtectionDomain>.SetParent(world, entity, parent);
        using var commands = new CommandBuffer(world);
        AssertRelationshipMutationRejected(() => commands.Add(entity, new Parent<ProtectionDomain>(parent)));
        AssertRelationshipMutationRejected(() => commands.Add(entity, default(Children<ProtectionDomain>)));
        AssertRelationshipMutationRejected(() => commands.Replace(entity, new Parent<ProtectionDomain>(parent)));
        AssertRelationshipMutationRejected(() => commands.Remove<Parent<ProtectionDomain>>(entity));
        Assert.Equal(0, commands.CommandCount);
        AssertRelationshipMutationRejected(() => world.CreateEntity(new Parent<ProtectionDomain>(parent)));
        int[] sourceIds = [ComponentMetadata<Parent<ProtectionDomain>>.Id];
        int[] targetIds = [ComponentMetadata<Children<ProtectionDomain>>.Id];
        AssertRelationshipMutationRejected(() => world.ExecuteBundleSpawn(targetIds, _ => { }));
        AssertRelationshipMutationRejected(() => world.ExecuteBundleSpawnBatch(targetIds, 1, _ => { }));
        AssertRelationshipMutationRejected(() => world.ExecuteBundleAdd(entity, targetIds, _ => { }));
        AssertRelationshipMutationRejected(() => world.ExecuteBundleReplace(entity, sourceIds, _ => { }));
        Assert.Equal(2, world.EntityCount);
        Assert.Equal(parent, Hierarchy<ProtectionDomain>.GetParent(world, entity));
    }

    [Fact]
    public void EntityCopyRequiresExcludingRelationshipTableComponents()
    {
        using var world = new World();
        var parent = world.CreateEntity();
        var source = world.CreateEntity();
        var target = world.CreateEntity(new RegularValue { Value = 4 });
        Hierarchy<ProtectionDomain>.SetParent(world, source, parent);
        AssertRelationshipMutationRejected(() => world.CloneEntity(source));
        AssertRelationshipMutationRejected(() => world.CopyEntity(source, target));
        AssertRelationshipMutationRejected(() => world.CopyEntity(target, parent));
        var clone = world.CloneEntity(source, EntityCopyOptions.Tags);
        Assert.True(world.IsAlive(clone));
        Assert.False(world.Has<Parent<ProtectionDomain>>(clone));
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

    }

    private static void AssertRelationshipMutationRejected(Action action)
    {
        var error = Assert.Throws<InvalidOperationException>(action);

    }

    private readonly struct ProtectionDomain : IHierarchyDomain;

    private struct RegularValue : IComponent
    {
        public int Value;
    }

    private struct SourceValue : IRelationshipSource;

    private struct TargetValue : IRelationshipTarget;

    private struct BothRoles : IRelationshipSource, IRelationshipTarget;

    private struct SparseSource : IRelationshipSource, ISparseComponent;

    private struct EnableableSource : IRelationshipSource, IEnableableComponent;

    private struct CleanupTarget : IRelationshipTarget, ICleanupComponent;
}
