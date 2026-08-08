using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Tests;

public sealed class StructuralCandidateContextTests
{
    [Fact]
    public void StructuralCandidates_ForDistinctWorlds_NestAndRestoreOuterCandidate()
    {
        var outerWorld = new World();
        var innerWorld = new World();
        WorldStructureRoot outerPublished = outerWorld.PublishedStructureRoot;
        WorldStructureRoot innerPublished = innerWorld.PublishedStructureRoot;
        WorldStructureRoot outerCandidate;
        WorldStructureRoot innerCandidate;

        using (StructuralMutationScope outerMutation = outerWorld.BeginStructuralMutation())
        {
            outerCandidate = outerWorld.ActiveStructureRoot;
            Assert.Same(outerCandidate, outerWorld.ActiveStructureRoot);
            Assert.Same(innerPublished, innerWorld.ActiveStructureRoot);

            using (StructuralMutationScope innerMutation = innerWorld.BeginStructuralMutation())
            {
                innerCandidate = innerWorld.ActiveStructureRoot;
                Assert.Same(outerCandidate, outerWorld.ActiveStructureRoot);
                Assert.Same(innerCandidate, innerWorld.ActiveStructureRoot);
                innerMutation.Commit();
            }

            Assert.Same(outerCandidate, outerWorld.ActiveStructureRoot);
            Assert.Same(innerCandidate, innerWorld.PublishedStructureRoot);
            outerMutation.Commit();
        }

        Assert.Same(outerCandidate, outerWorld.PublishedStructureRoot);
        Assert.Same(innerCandidate, innerWorld.PublishedStructureRoot);
        Assert.NotSame(outerPublished, outerWorld.ActiveStructureRoot);
        Assert.NotSame(innerPublished, innerWorld.ActiveStructureRoot);
    }

    [Fact]
    public void StructuralCandidates_NestedFault_RestoresEachWorldContext()
    {
        var outerWorld = new World();
        var innerWorld = new World();
        WorldStructureRoot outerPublished = outerWorld.PublishedStructureRoot;
        WorldStructureRoot innerPublished = innerWorld.PublishedStructureRoot;
        WorldStructureRoot outerCandidate;

        using (StructuralMutationScope outerMutation = outerWorld.BeginStructuralMutation())
        {
            outerCandidate = outerWorld.ActiveStructureRoot;
            InvalidOperationException? fault = null;
            try
            {
                using StructuralMutationScope innerMutation =
                    innerWorld.BeginStructuralMutation();
                throw new InvalidOperationException("candidate callback fault");
            }
            catch (InvalidOperationException exception)
            {
                fault = exception;
            }

            Assert.NotNull(fault);
            Assert.Equal("candidate callback fault", fault.Message);
            Assert.Same(outerCandidate, outerWorld.ActiveStructureRoot);
            Assert.Same(innerPublished, innerWorld.ActiveStructureRoot);
        }

        Assert.Same(outerPublished, outerWorld.ActiveStructureRoot);
        Assert.Same(innerPublished, innerWorld.ActiveStructureRoot);
    }

    [Fact]
    public void StructuralTransaction_RejectsSameWorldOverlap_AndRecoversAfterRelease()
    {
        var world = new World();
        WorldStructureRoot published = world.PublishedStructureRoot;
        WorldStructureRoot firstCandidate = published.CloneDetached(world, world.HookStore);
        WorldStructureRoot secondCandidate = published.CloneDetached(world, world.HookStore);

        using (World.StructuralTransactionScope transaction =
               world.BeginStructuralTransaction())
        {
            InvalidOperationException transactionFault = Assert.Throws<InvalidOperationException>(
                () => world.BeginStructuralTransaction());
            Assert.Contains("already active for this World", transactionFault.Message);

            using (World.StructuralCandidateScope candidateScope =
                   world.EnterStructuralCandidate(firstCandidate))
            {
                InvalidOperationException candidateFault = Assert.Throws<InvalidOperationException>(
                    () => world.EnterStructuralCandidate(secondCandidate));
                Assert.Contains("already active for this World", candidateFault.Message);
                Assert.Same(firstCandidate, world.ActiveStructureRoot);
            }
        }

        using World.StructuralTransactionScope recovered = world.BeginStructuralTransaction();
        Assert.Same(published, world.ActiveStructureRoot);
    }

    [Fact]
    public void StructuralCandidate_CrossThreadReleaseIsRejectedWithoutLosingOwnerContext()
    {
        var world = new World();
        WorldStructureRoot candidate = world.PublishedStructureRoot.CloneDetached(
            world,
            world.HookStore);
        World.StructuralTransactionScope transaction = world.BeginStructuralTransaction();
        World.StructuralCandidateScope candidateScope =
            world.EnterStructuralCandidate(candidate);

        try
        {
            Exception? releaseFault = null;
            var otherThread = new Thread(
                () => releaseFault = Record.Exception(() => candidateScope.Dispose()));
            otherThread.Start();
            otherThread.Join();

            InvalidOperationException invalidRelease =
                Assert.IsType<InvalidOperationException>(releaseFault);
            Assert.Contains("owning thread", invalidRelease.Message);
            Assert.Same(candidate, world.ActiveStructureRoot);
        }
        finally
        {
            candidateScope.Dispose();
            transaction.Dispose();
        }

        Assert.Same(world.PublishedStructureRoot, world.ActiveStructureRoot);
    }

    [Fact]
    public void WorldStructureRoot_CreateAndCloneKeepCompleteOwnerSetAndBindingsInParity()
    {
        var world = new World();
        Entity entity = world.CreateEntity(new WiringValue { Value = 7 });
        WorldStructureRoot published = world.PublishedStructureRoot;
        WorldStructureRoot candidate = published.CloneDetached(world, world.HookStore);

        object[] publishedOwners = Owners(published);
        object[] candidateOwners = Owners(candidate);
        Assert.Equal(publishedOwners.Length, candidateOwners.Length);
        Assert.Equal(14, publishedOwners.Length);
        for (int index = 0; index < publishedOwners.Length; index++)
        {
            Assert.NotNull(publishedOwners[index]);
            Assert.NotNull(candidateOwners[index]);
            Assert.NotSame(publishedOwners[index], candidateOwners[index]);
        }

        using World.StructuralTransactionScope transaction = world.BeginStructuralTransaction();
        using World.StructuralCandidateScope candidateScope =
            world.EnterStructuralCandidate(candidate);
        world.Replace(entity, new WiringValue { Value = 11 });

        Assert.Equal(11, world.Read<WiringValue>(entity).Value);
        Assert.Equal(11, candidate.Components.Read<WiringValue>(entity).Value);
        Assert.Equal(7, published.Components.Read<WiringValue>(entity).Value);
        Assert.Same(candidate.Entities, world.Entities);
        Assert.Same(candidate.Tables, world.Tables);
        Assert.Same(candidate.Components, world.Components);
    }

    private static object[] Owners(WorldStructureRoot root) =>
    [
        root.Entities,
        root.Tables,
        root.Sparse,
        root.Indices,
        root.RelationGraph,
        root.Components,
        root.Queries,
        root.Buffers,
        root.Bundles,
        root.Copy,
        root.Shared,
        root.Clock,
        root.Iteration,
        root.Hierarchy,
    ];

    private struct WiringValue : IComponent
    {
        public int Value;
    }
}
