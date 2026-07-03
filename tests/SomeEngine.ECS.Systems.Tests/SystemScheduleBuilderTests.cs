using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Systems;
using Xunit;

namespace SomeEngine.ECS.Systems.Tests;

public class SystemScheduleBuilderTests
{
    [Fact]
    public void ManifestFromQuery_UsesAccessMetadataAndIgnoresPresenceOnlyTerms()
    {
        var spec = new QueryDefinitionBuilder()
            .All<SystemDirty>()
            .Read<SystemPosition>()
            .Write<SystemVelocity>()
            .Build();

        var manifest = SystemAccessManifest.FromQuery(spec);

        Assert.Equal(2, manifest.Entries.Count);
        Assert.Contains(
            new SystemAccessEntry(
                SystemAccessResource.Component(ComponentMetadata<SystemPosition>.Id),
                QueryAccess.Read),
            manifest.Entries);
        Assert.Contains(
            new SystemAccessEntry(
                SystemAccessResource.Component(ComponentMetadata<SystemVelocity>.Id),
                QueryAccess.Write),
            manifest.Entries);
        Assert.DoesNotContain(
            manifest.Entries,
            entry => entry.Resource == SystemAccessResource.Component(ComponentMetadata<SystemDirty>.Id));
    }

    [Fact]
    public void ConflictRules_AllowReadReadAndRejectReadWriteOrWriteWrite()
    {
        var read = SystemAccessManifest.CreateBuilder().Read<SystemPosition>().Build();
        var readAgain = SystemAccessManifest.CreateBuilder().Read<SystemPosition>().Build();
        var write = SystemAccessManifest.CreateBuilder().Write<SystemPosition>().Build();

        Assert.False(AccessConflicts.Conflicts(read, readAgain));

        Assert.True(AccessConflicts.TryGetConflict(read, write, out var readWriteConflict));
        Assert.Equal(AccessConflictKind.ResourceWrite, readWriteConflict.Kind);
        Assert.Equal(SystemAccessResource.Component(ComponentMetadata<SystemPosition>.Id), readWriteConflict.Resource);
        Assert.Equal(QueryAccess.Read, readWriteConflict.LeftAccess);
        Assert.Equal(QueryAccess.Write, readWriteConflict.RightAccess);

        Assert.True(AccessConflicts.Conflicts(write, write));
    }

    [Fact]
    public void Builder_FailsFastWhenTableHelperReceivesSideStoreType()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SystemAccessManifest.CreateBuilder().Read<ScheduleShared>());

        Assert.Contains("table access helpers require", ex.Message);
    }

    [Fact]
    public void SideStoreHelpers_FailFastWhenStoragePathDiffers()
    {
        var sharedEx = Assert.Throws<InvalidOperationException>(
            () => SystemAccessManifest.CreateBuilder().ReadShared<ScheduleSharedSparse>());
        var sparseEx = Assert.Throws<InvalidOperationException>(
            () => SystemAccessManifest.CreateBuilder().ReadSparse<ScheduleSparseRelation>());

        Assert.Contains("uses Sparse storage", sharedEx.Message);
        Assert.Contains("uses Relation storage", sparseEx.Message);
    }

    [Fact]
    public void RelationHelper_AcceptsExclusiveRelation()
    {
        var manifest = SystemAccessManifest.CreateBuilder()
            .ReadRelation<ScheduleExclusive>()
            .Build();

        Assert.Contains(
            new SystemAccessEntry(
                SystemAccessResource.Relation(ComponentMetadata<ScheduleExclusive>.Id),
                QueryAccess.Read),
            manifest.Entries);
    }

    [Fact]
    public void Resource_FailsFastForUnknownResourceKind()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new SystemAccessResource((AccessResourceKind)255, 0));

        Assert.Contains("Unknown system access resource kind", ex.Message);
    }

    [Fact]
    public void StagePlanner_GroupsAdjacentCompatibleSystemsAndSkipsDisabledSystems()
    {
        var manifests = new[]
        {
            SystemAccessManifest.CreateBuilder().Read<SystemPosition>().Build(),
            SystemAccessManifest.CreateBuilder().Read<SystemVelocity>().Build(),
            SystemAccessManifest.CreateBuilder().Write<SystemPosition>().Build(),
            SystemAccessManifest.CreateBuilder().Read<SystemDirty>().Build(),
        };

        var plan = SystemScheduleBuilder.Build(manifests, new[] { true, true, true, false });

        Assert.Equal(2, plan.Stages.Count);
        Assert.Equal(new[] { 0, 1 }, plan.Stages[0].SystemIndices);
        Assert.Equal(new[] { 2 }, plan.Stages[1].SystemIndices);
    }

    [Fact]
    public void StagePlanner_PreservesRegistrationOrderAcrossConflicts()
    {
        var manifests = new[]
        {
            SystemAccessManifest.CreateBuilder().Write<SystemPosition>().Build(),
            SystemAccessManifest.CreateBuilder().Read<SystemPosition>().Build(),
            SystemAccessManifest.CreateBuilder().Read<SystemVelocity>().Build(),
        };

        var plan = SystemScheduleBuilder.Build(manifests);

        Assert.Equal(2, plan.Stages.Count);
        Assert.Equal(new[] { 0 }, plan.Stages[0].SystemIndices);
        Assert.Equal(new[] { 1, 2 }, plan.Stages[1].SystemIndices);
    }

    [Fact]
    public void StagePlanner_IsolatesStructuralChangesIntoExclusiveBarrierStages()
    {
        var manifests = new[]
        {
            SystemAccessManifest.CreateBuilder().Read<SystemPosition>().Build(),
            SystemAccessManifest.CreateBuilder().StructuralChange().Build(),
            SystemAccessManifest.CreateBuilder().Read<SystemVelocity>().Build(),
        };

        var plan = SystemScheduleBuilder.Build(manifests);

        Assert.Equal(3, plan.Stages.Count);
        Assert.Equal(new[] { 0 }, plan.Stages[0].SystemIndices);
        Assert.Equal(new[] { 1 }, plan.Stages[1].SystemIndices);
        Assert.True(plan.Stages[1].RequiresBarrierAfter);
        Assert.Equal(new[] { 2 }, plan.Stages[2].SystemIndices);
    }

    [Fact]
    public void ConflictRules_ReportExclusiveSideStoreResource()
    {
        var sharedWrite = SystemAccessManifest.CreateBuilder().WriteShared<ScheduleShared>().Build();
        var read = SystemAccessManifest.CreateBuilder().Read<SystemPosition>().Build();

        Assert.True(AccessConflicts.TryGetConflict(sharedWrite, read, out var conflict));
        Assert.Equal(AccessConflictKind.ExclusiveStage, conflict.Kind);
        Assert.Equal(SystemAccessResource.Shared(ComponentMetadata<ScheduleShared>.Id), conflict.Resource);
    }

    [Fact]
    public void StagePlanner_AllowsCommandBufferRecordingButMarksBarrierAfterStage()
    {
        var manifests = new[]
        {
            SystemAccessManifest.CreateBuilder().Read<SystemPosition>().Build(),
            SystemAccessManifest.CreateBuilder().CommandBufferWrite().Build(),
            SystemAccessManifest.CreateBuilder().Read<SystemVelocity>().Build(),
        };

        var plan = SystemScheduleBuilder.Build(manifests);

        Assert.Single(plan.Stages);
        Assert.Equal(new[] { 0, 1, 2 }, plan.Stages[0].SystemIndices);
        Assert.True(plan.Stages[0].RequiresBarrierAfter);
        Assert.False(AccessConflicts.Conflicts(manifests[1], manifests[1]));
    }

    [Fact]
    public void SideStoreResourcesConflictIndependentlyFromTableComponents()
    {
        var sparseRead = SystemAccessManifest.CreateBuilder().ReadSparse<ScheduleSparse>().Build();
        var sparseWrite = SystemAccessManifest.CreateBuilder().WriteSparse<ScheduleSparse>().Build();
        var relationRead = SystemAccessManifest.CreateBuilder().ReadRelation<ScheduleRelation>().Build();
        var relationWrite = SystemAccessManifest.CreateBuilder().WriteRelation<ScheduleRelation>().Build();

        Assert.True(AccessConflicts.Conflicts(sparseRead, sparseWrite));
        Assert.True(AccessConflicts.Conflicts(relationRead, relationWrite));
        Assert.False(AccessConflicts.Conflicts(sparseRead, relationWrite));
    }

    [Fact]
    public void StagePlanner_FailsFastWhenEnabledMaskLengthDoesNotMatch()
    {
        var manifests = new[]
        {
            SystemAccessManifest.CreateBuilder().Read<SystemPosition>().Build(),
        };

        var ex = Assert.Throws<ArgumentException>(
            () => SystemScheduleBuilder.Build(manifests, Array.Empty<bool>()));

        Assert.Contains("Enabled mask length", ex.Message);
    }

    private struct ScheduleShared : SomeEngine.ECS.Components.ISharedComponent { }

    private struct ScheduleSparse : SomeEngine.ECS.Components.ISparseComponent { }

    private struct ScheduleRelation : SomeEngine.ECS.Components.IRelation { }

    private struct ScheduleSharedSparse : SomeEngine.ECS.Components.ISharedComponent, ISparseComponent { }

    private struct ScheduleSparseRelation : SomeEngine.ECS.Components.ISparseComponent, IRelation { }

    private struct ScheduleExclusive : SomeEngine.ECS.Components.IExclusiveRelation { }
}
