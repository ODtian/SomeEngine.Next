using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Binds one feature-owned resource required by a final linked material stage entry. Providers are
/// resolved and connected while the graph is reconciled. Each frame only supplies resource values.
/// </summary>
public delegate void ClusterStageResourceBinder(
    ref RenderGraphEdit graph,
    ref PassDefinition pass);

/// <summary>
/// Gives a feature adapter the source view templates from which it emits flat raster
/// invocations. The adapter owns its conversion pass and output storage.
/// </summary>
public delegate void ClusterInvocationSourceBinder(
    ref RenderGraphFrame graph,
    GraphBufferSlice sourceViews);

/// <summary>
/// Resolves feature resources reflected from final linked material stages. Implementations own the
/// referenced resources and passes; Cluster owns only the generic binding boundary.
/// </summary>
public interface IClusterStageResourceProvider
{
    bool TryResolve(
        string name,
        ResourceBindingType type,
        out ClusterStageResourceBinder binder);
}
