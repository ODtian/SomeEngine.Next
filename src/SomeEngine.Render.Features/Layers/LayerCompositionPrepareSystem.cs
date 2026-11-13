using SomeEngine.ECS.Systems;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Features.Layers;

internal sealed class LayerCompositionPrepareSystem(LayerComposition composition)
    : ISystem<RenderPrepareSystemContext>
{
    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        composition.PrepareViews();
        composition.GrowStorage();
    }
}
