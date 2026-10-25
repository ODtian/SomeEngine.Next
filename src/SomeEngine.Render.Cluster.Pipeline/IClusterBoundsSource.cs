using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Model-space bounds produced by a deformation implementation.</summary>
/// <remarks>Each result contains three float4 values: minimum XYZ and maximum parent LOD error,
/// maximum XYZ and minimum own LOD error, and LOD center/radius. Draw-cluster records instead store
/// own LOD error in minimum.w. RenderDeformBounds locates four slice records per physical node,
/// followed by draw clusters. Geometry-only instances use a zero NodeCount.</remarks>
public interface IClusterBoundsSource
{
    GraphBufferInput CurrentBounds { get; }
    GraphBufferInput PreviousBounds { get; }
    GraphPassId CompletePass { get; }
    ulong Revision { get; }
}
