using SomeEngine.ECS.Systems;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Features.Lighting;

/// <summary>Applies delivered light-list requirements before the graph frame begins.</summary>
internal sealed class ClusteredLightBinningPrepareSystem(ClusteredLightBinning binning)
    : ISystem<RenderPrepareSystemContext>
{
    public void OnUpdate(ref RenderPrepareSystemContext context) => binning.Grow();
}
