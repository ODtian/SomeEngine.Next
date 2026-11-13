using SomeEngine.Graphics;
using EngineTexture = SomeEngine.Resources.Texture;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>The material data consumed by one graph update.</summary>
public readonly record struct ClusterMaterialChange
{
    internal ClusterMaterialChange(ClusterMaterialAssignmentSet assignments)
    {
        Assignments = assignments;
    }

    public IReadOnlyList<uint> ChangedRasterBins { get; init; } = [];
    public IReadOnlyList<uint> ChangedShadeBins { get; init; } = [];
    public ReadOnlyMemory<BufferRange> ChangedScalarRanges { get; init; }
    public IReadOnlyList<EngineTexture> ChangedTextures { get; init; } = [];
    public bool ShadingPathChanged { get; init; }
    internal ClusterMaterialAssignmentSet Assignments { get; init; }
    internal ClusterRasterStageSet RasterStages { get; init; } = null!;
    internal ClusterShadeStageSet ShadeStages { get; init; } = null!;
    internal ClusterMaterialScalars Scalars { get; init; } = null!;
}
