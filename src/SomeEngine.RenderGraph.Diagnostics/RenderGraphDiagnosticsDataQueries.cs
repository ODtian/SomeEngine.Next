using System.Collections.Immutable;

namespace SomeEngine.RenderGraph.Diagnostics;

public static class RenderGraphDiagnosticsDataQueries
{
    public static ImmutableArray<RenderGraphDiagnosticsData.Pass> PassesUsingBuffer(
        RenderGraphDiagnosticsData data,
        int bufferOrdinal)
    {
        ArgumentNullException.ThrowIfNull(data);
        if ((uint)bufferOrdinal >= (uint)data.Buffers.Length)
            throw new ArgumentOutOfRangeException(nameof(bufferOrdinal));
        return PassesUsingResource(data, GraphAccessTargetKind.Buffer, bufferOrdinal);
    }

    public static ImmutableArray<RenderGraphDiagnosticsData.Pass> PassesUsingTexture(
        RenderGraphDiagnosticsData data,
        int textureOrdinal)
    {
        ArgumentNullException.ThrowIfNull(data);
        if ((uint)textureOrdinal >= (uint)data.Textures.Length)
            throw new ArgumentOutOfRangeException(nameof(textureOrdinal));
        return PassesUsingResource(data, GraphAccessTargetKind.Texture, textureOrdinal);
    }

    public static ImmutableArray<RenderGraphDiagnosticsData.Barrier> BarriersForPass(
        RenderGraphDiagnosticsData data,
        int passOrdinal)
    {
        ArgumentNullException.ThrowIfNull(data);
        if ((uint)passOrdinal >= (uint)data.Passes.Length)
            throw new ArgumentOutOfRangeException(nameof(passOrdinal));
        return data.Barriers.Where(row => row.Pass == passOrdinal).ToImmutableArray();
    }

    private static ImmutableArray<RenderGraphDiagnosticsData.Pass> PassesUsingResource(
        RenderGraphDiagnosticsData data,
        GraphAccessTargetKind kind,
        int resourceOrdinal)
    {
        var passOrdinals = new HashSet<int>();
        foreach (RenderGraphDiagnosticsData.Access access in data.Accesses)
        {
            if (access.TargetKind == kind && access.TargetOrdinal == resourceOrdinal)
                passOrdinals.Add(access.Pass);
        }
        return data.Passes.Where(pass => passOrdinals.Contains(pass.Ordinal)).ToImmutableArray();
    }
}
