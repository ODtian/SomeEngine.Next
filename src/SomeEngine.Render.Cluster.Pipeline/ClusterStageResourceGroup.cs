using SomeEngine.Graphics;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Combines independent stage-resource contributors behind one feature-neutral boundary.</summary>
public sealed class ClusterStageResourceGroup : IClusterStageResourceProvider
{
    private readonly List<IClusterStageResourceProvider> _providers = [];

    public void Add(IClusterStageResourceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (_providers.Contains(provider))
            return;
        _providers.Add(provider);
    }

    public bool Remove(IClusterStageResourceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return _providers.Remove(provider);
    }

    public bool TryResolve(
        string name,
        ResourceBindingType type,
        out ClusterStageResourceBinder binder)
    {
        binder = null!;
        for (int index = 0; index < _providers.Count; index++)
        {
            if (!_providers[index].TryResolve(name, type, out ClusterStageResourceBinder candidate))
                continue;
            if (binder is not null)
            {
                throw new InvalidDataException(
                    $"Multiple Cluster stage-resource contributors resolve '{name}' as {type}.");
            }
            binder = candidate;
        }
        return binder is not null;
    }
}
