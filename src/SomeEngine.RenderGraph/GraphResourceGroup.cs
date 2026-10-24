using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph;

/// <summary>Combines independent stage-resource contributors behind one feature-neutral boundary.</summary>
public sealed class GraphResourceGroup : IGraphResourceProvider
{
    private readonly List<IGraphResourceProvider> _providers = [];

    public void Add(IGraphResourceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (_providers.Contains(provider))
            return;
        _providers.Add(provider);
    }

    public bool Remove(IGraphResourceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return _providers.Remove(provider);
    }

    public bool TryResolve(
        string name,
        ResourceBindingType type,
        out GraphResourceBinder binder)
    {
        binder = null!;
        for (int index = 0; index < _providers.Count; index++)
        {
            if (!_providers[index].TryResolve(name, type, out GraphResourceBinder candidate))
                continue;
            if (binder is not null)
            {
                throw new InvalidDataException(
                    $"Multiple graph resource providers resolve '{name}' as {type}.");
            }
            binder = candidate;
        }
        return binder is not null;
    }
}
