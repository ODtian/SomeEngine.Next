using SomeEngine.ECS.Systems;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Features.Decals;

/// <summary>Applies delivered decal-list requirements before the graph frame begins.</summary>
internal sealed class MaterialDecalBinningPrepareSystem(MaterialDecalBinning binning)
    : ISystem<RenderPrepareSystemContext>
{
    public void OnUpdate(ref RenderPrepareSystemContext context) => binning.GrowList();
}
