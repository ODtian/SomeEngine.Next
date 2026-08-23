using System.Collections.Immutable;

namespace SomeEngine.RenderGraph.Diagnostics;

public static class RenderGraphSnapshotQueries
{
    public static ImmutableArray<RenderGraphSnapshot.Pass> PassesUsingBuffer(
        RenderGraphSnapshot snapshot,
        int bufferOrdinal)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if ((uint)bufferOrdinal >= (uint)snapshot.Buffers.Length)
            throw new ArgumentOutOfRangeException(nameof(bufferOrdinal));
        return PassesUsingResource(snapshot, GraphAccessTargetKind.Buffer, bufferOrdinal);
    }

    public static ImmutableArray<RenderGraphSnapshot.Pass> PassesUsingTexture(
        RenderGraphSnapshot snapshot,
        int textureOrdinal)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if ((uint)textureOrdinal >= (uint)snapshot.Textures.Length)
            throw new ArgumentOutOfRangeException(nameof(textureOrdinal));
        return PassesUsingResource(snapshot, GraphAccessTargetKind.Texture, textureOrdinal);
    }

    public static ImmutableArray<RenderGraphSnapshot.Barrier> BarriersForPass(
        RenderGraphSnapshot snapshot,
        int passOrdinal)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if ((uint)passOrdinal >= (uint)snapshot.Passes.Length)
            throw new ArgumentOutOfRangeException(nameof(passOrdinal));
        return snapshot.Barriers.Where(row => row.Pass == passOrdinal).ToImmutableArray();
    }

    private static ImmutableArray<RenderGraphSnapshot.Pass> PassesUsingResource(
        RenderGraphSnapshot snapshot,
        GraphAccessTargetKind kind,
        int resourceOrdinal)
    {
        var passOrdinals = new HashSet<int>();
        foreach (RenderGraphSnapshot.Access access in snapshot.Accesses)
        {
            if (access.TargetKind == kind && access.TargetOrdinal == resourceOrdinal)
                passOrdinals.Add(access.Pass);
        }
        return snapshot.Passes.Where(pass => passOrdinals.Contains(pass.Ordinal)).ToImmutableArray();
    }
}
