using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Sparse;

namespace SomeEngine.ECS.Tests;

public sealed class SideOwnerCopyOnWriteTests
{
    [Fact]
    public void CandidateRollback_SharesUntouchedSideOwners_AndPreservesPublishedImage()
    {
        Fixture fixture = CreateFixture();
        World world = fixture.World;
        WorldStructureRoot published = world.PublishedStructureRoot;
        WorldStructureRoot candidate;

        using (StructuralMutationScope mutation = world.BeginStructuralMutation())
        {
            candidate = world.ActiveStructureRoot;
            AssertSharedBackings(published, candidate);

            Mutate(candidate, fixture.Entity);
            AssertDetachedBackings(published, candidate);
            AssertImage(candidate, fixture.Entity, expectedValue: 2);
        }

        Assert.Same(published, world.PublishedStructureRoot);
        AssertImage(published, fixture.Entity, expectedValue: 1);
        Assert.Equal(0, published.Sparse.Set<CowSparse>().DetachCount);
        Assert.Equal(
            0,
            published.Indices.StoreDetachCount(ComponentMetadata<CowIndexed>.Id));
        Assert.Equal(
            0,
            published.Shared.StoreDetachCount<CowShared>(ComponentMetadata<CowShared>.Id));
    }

    [Fact]
    public void CandidateCommit_PublishesDetachedSideOwners_WithoutMutatingPriorRoot()
    {
        Fixture fixture = CreateFixture();
        World world = fixture.World;
        WorldStructureRoot published = world.PublishedStructureRoot;
        WorldStructureRoot candidate;

        using (StructuralMutationScope mutation = world.BeginStructuralMutation())
        {
            candidate = world.ActiveStructureRoot;
            AssertSharedBackings(published, candidate);

            Mutate(candidate, fixture.Entity);
            AssertDetachedBackings(published, candidate);
            mutation.Commit();
        }

        Assert.Same(candidate, world.PublishedStructureRoot);
        AssertImage(candidate, fixture.Entity, expectedValue: 2);

        // A retained reader of the prior publication continues to observe its exact generation.
        AssertImage(published, fixture.Entity, expectedValue: 1);
    }

    [Fact]
    public void SparseClone_ReadsRemainShared_AndWritableRefOrSpanDetachesBeforeExposure()
    {
        Entity entity = new(7, 3);
        var source = new SparseSet<CowSparse>();
        source.Add(entity, new CowSparse(10));

        SparseSet<CowSparse> refCandidate = source.CloneDetached();
        object sharedBacking = source.BackingIdentity;
        Assert.Same(sharedBacking, refCandidate.BackingIdentity);
        Assert.Equal(10, refCandidate.Read(entity).Value);
        Assert.Equal(10, Assert.Single(refCandidate.DenseData.ToArray()).Value);
        Assert.Same(sharedBacking, refCandidate.BackingIdentity);
        Assert.Equal(0, refCandidate.DetachCount);

        ref CowSparse candidateValue = ref refCandidate.Get(entity);
        Assert.NotSame(sharedBacking, refCandidate.BackingIdentity);
        Assert.Equal(1, refCandidate.DetachCount);
        candidateValue = new CowSparse(20);
        Assert.Equal(10, source.Read(entity).Value);
        Assert.Equal(20, refCandidate.Read(entity).Value);

        SparseSet<CowSparse> spanCandidate = source.CloneDetached();
        object spanSharedBacking = source.BackingIdentity;
        Assert.Same(spanSharedBacking, spanCandidate.BackingIdentity);
        Span<CowSparse> writable = spanCandidate.BorrowDenseWrite();
        Assert.NotSame(spanSharedBacking, spanCandidate.BackingIdentity);
        Assert.Equal(1, spanCandidate.DetachCount);
        writable[0] = new CowSparse(30);
        Assert.Equal(10, source.Read(entity).Value);
        Assert.Equal(30, spanCandidate.Read(entity).Value);
    }

    private static Fixture CreateFixture()
    {
        var world = new World();
        Entity entity = world.CreateEntity(new CowIndexed(1));
        world.AddSparse(entity, new CowSparse(1));
        world.AddShared(entity, new CowShared(1));

        Assert.Equal(
            [entity],
            world.GetByIndex<CowIndexed, int>(1).ToArray());
        return new Fixture(world, entity);
    }

    private static void Mutate(WorldStructureRoot candidate, Entity entity)
    {
        candidate.Sparse.Replace(entity, new CowSparse(2));
        candidate.Components.Replace(entity, new CowIndexed(2));
        candidate.Shared.Replace(entity, new CowShared(2));
    }

    private static void AssertSharedBackings(
        WorldStructureRoot published,
        WorldStructureRoot candidate)
    {
        SparseSet<CowSparse> publishedSparse = published.Sparse.Set<CowSparse>();
        SparseSet<CowSparse> candidateSparse = candidate.Sparse.Set<CowSparse>();
        Assert.NotSame(publishedSparse, candidateSparse);
        Assert.Same(publishedSparse.BackingIdentity, candidateSparse.BackingIdentity);
        Assert.Equal(0, candidateSparse.DetachCount);

        int indexedId = ComponentMetadata<CowIndexed>.Id;
        Assert.NotNull(published.Indices.StoreBackingIdentity(indexedId));
        Assert.Same(
            published.Indices.StoreBackingIdentity(indexedId),
            candidate.Indices.StoreBackingIdentity(indexedId));
        Assert.Equal(0, candidate.Indices.StoreDetachCount(indexedId));

        int sharedId = ComponentMetadata<CowShared>.Id;
        Assert.False(
            published.Shared.SharesStoreObjectWith<CowShared>(candidate.Shared, sharedId));
        Assert.True(
            published.Shared.SharesStoreBackingWith<CowShared>(candidate.Shared, sharedId));
        Assert.Equal(0, candidate.Shared.StoreDetachCount<CowShared>(sharedId));

    }

    private static void AssertDetachedBackings(
        WorldStructureRoot published,
        WorldStructureRoot candidate)
    {
        SparseSet<CowSparse> publishedSparse = published.Sparse.Set<CowSparse>();
        SparseSet<CowSparse> candidateSparse = candidate.Sparse.Set<CowSparse>();
        Assert.NotSame(publishedSparse.BackingIdentity, candidateSparse.BackingIdentity);
        Assert.Equal(1, candidateSparse.DetachCount);

        int indexedId = ComponentMetadata<CowIndexed>.Id;
        Assert.NotSame(
            published.Indices.StoreBackingIdentity(indexedId),
            candidate.Indices.StoreBackingIdentity(indexedId));
        Assert.Equal(1, candidate.Indices.StoreDetachCount(indexedId));

        int sharedId = ComponentMetadata<CowShared>.Id;
        Assert.False(
            published.Shared.SharesStoreBackingWith<CowShared>(candidate.Shared, sharedId));
        Assert.Equal(1, candidate.Shared.StoreDetachCount<CowShared>(sharedId));

    }

    private static void AssertImage(
        WorldStructureRoot root,
        Entity entity,
        int expectedValue)
    {
        Assert.Equal(expectedValue, root.Sparse.Read<CowSparse>(entity).Value);
        Assert.Equal(expectedValue, root.Components.Read<CowIndexed>(entity).Key);
        Assert.Equal(expectedValue, root.Shared.Get<CowShared>(entity).Value);
        Assert.Equal(
            [entity],
            root.Indices.Get<CowIndexed, int>(expectedValue, root.Tables.All).ToArray());
        Assert.Empty(
            root.Indices.Get<CowIndexed, int>(expectedValue == 1 ? 2 : 1, root.Tables.All)
                .ToArray());
    }

    private readonly record struct Fixture(World World, Entity Entity);

    private readonly record struct CowIndexed(int Key) : IIndexedComponent<int>
    {
        public int GetKey() => Key;
    }

    private readonly record struct CowShared(int Value) : ISharedComponent;

    private readonly record struct CowSparse(int Value) : ISparseComponent;
}
