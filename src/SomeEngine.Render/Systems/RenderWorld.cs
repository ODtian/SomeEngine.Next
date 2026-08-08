using SomeEngine.ECS;
using SomeEngine.ECS.Registry;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Systems;

/// <summary>
/// Independent render ECS. It contains extracted snapshots as well as render-only entities and
/// pipeline-owned components. Extraction and pipeline scheduling are deliberately owned by
/// separate system groups; this type is only their ECS storage.
/// </summary>
public sealed class RenderWorld : World
{
    public RenderWorld(int initialEntityCapacity = 256)
        : base(initialEntityCapacity)
    {
    }

    internal void ApplyExtraction(RenderExtractionSystems extraction)
    {
        ExecuteStructuralTransaction(
            extraction,
            static systems => systems.ApplyCandidate());
    }

    internal void ApplyExtractionChanges(RenderExtractionSystems extraction)
    {
        ExecuteStructuralTransaction(
            extraction,
            static systems => systems.ApplyChangesCandidate());
    }

    internal bool TryApplyExtractionChangesDirect(RenderExtractionSystems extraction) =>
        ExecuteValueMutation(
            extraction,
            static systems => systems.TryApplyChangesDirect());

    internal bool HasExtractionValueHooks =>
        HasValueReplaceHookCallbacks(ComponentMetadata<RenderMesh>.Id) ||
        HasValueReplaceHookCallbacks(ComponentMetadata<RenderDirectionalLight>.Id) ||
        HasValueReplaceHookCallbacks(ComponentMetadata<RenderPointLight>.Id) ||
        HasValueReplaceHookCallbacks(ComponentMetadata<RenderSpotLight>.Id) ||
        HasValueReplaceHookCallbacks(ComponentMetadata<RenderLightCookie>.Id);
}
