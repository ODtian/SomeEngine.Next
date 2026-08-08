namespace SomeEngine.RenderGraph.Diagnostics;

using System.Collections.Immutable;

public static class RenderGraphSnapshotQuery
{
    public static ImmutableArray<RenderGraphSnapshot.Pass> SnapshotPassesUsingResource(
        RenderGraphSnapshot snapshot,
        int resourceOrdinal)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        HashSet<int> ordinals = [];
        foreach (RenderGraphSnapshot.Access access in snapshot.Accesses)
            if (access.ResourceOrdinal == resourceOrdinal) ordinals.Add(access.PassOrdinal);
        ImmutableArray<RenderGraphSnapshot.Pass>.Builder result =
            ImmutableArray.CreateBuilder<RenderGraphSnapshot.Pass>(ordinals.Count);
        foreach (RenderGraphSnapshot.Pass pass in snapshot.Passes)
            if (ordinals.Contains(pass.Ordinal)) result.Add(pass);
        return result.MoveToImmutable();
    }

    public static ImmutableArray<RenderGraphSnapshot.Barrier> SnapshotBarriersForResource(
        RenderGraphSnapshot snapshot,
        int resourceOrdinal)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ImmutableArray<RenderGraphSnapshot.Barrier>.Builder result =
            ImmutableArray.CreateBuilder<RenderGraphSnapshot.Barrier>();
        foreach (RenderGraphSnapshot.Barrier barrier in snapshot.Barriers)
            if (barrier.ResourceOrdinal == resourceOrdinal ||
                barrier.AliasingBeforeResourceOrdinal == resourceOrdinal)
                result.Add(barrier);
        return result.ToImmutable();
    }
}
