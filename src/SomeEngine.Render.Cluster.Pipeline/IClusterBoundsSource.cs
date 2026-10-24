using SomeEngine.RenderGraph;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Model-space bounds produced by a deformation implementation.</summary>
/// <remarks>Each result contains three float4 values: minimum XYZ and LOD error, maximum XYZ, and LOD center/radius.
/// RenderDeformBounds locates each instance's nodes and clusters.</remarks>
public interface IClusterBoundsSource
{
    GraphBufferInput CurrentBounds { get; }
    GraphBufferInput PreviousBounds { get; }
    GraphPassId CompletePass { get; }
    ulong Revision { get; }
    /// <summary>Reports current instance contents changed after the caller's accepted source revision.</summary>
    bool HasChangedSince(Entity entity, ulong revision);
}
