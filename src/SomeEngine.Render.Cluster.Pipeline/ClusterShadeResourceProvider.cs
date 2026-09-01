using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Binds one feature-owned resource required by a final linked material stage entry. Providers are
/// resolved while the retained graph is reconciled, so frame recording invokes a fixed delegate
/// and performs no feature-name lookup or shader reflection.
/// </summary>
public delegate void ClusterStageResourceBinder(
    ref RenderGraphFrame graph,
    ref PassDefinition pass);

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
