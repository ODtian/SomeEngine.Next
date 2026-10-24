using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Model-space bounds produced by a deformation implementation.</summary>
/// <remarks>Each bound contains two float4 values, minimum and maximum. RenderDeformBounds locates each instance's nodes and clusters.</remarks>
public interface IClusterBoundsSource
{
    GraphBufferInput CurrentBounds { get; }
    GraphBufferInput PreviousBounds { get; }
    GraphPassId CompletePass { get; }
    ulong Revision { get; }
}
