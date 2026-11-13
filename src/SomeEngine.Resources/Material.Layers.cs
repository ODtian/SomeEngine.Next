using SomeEngine.Resources.Layers;

namespace SomeEngine.Resources;

public abstract partial class Material
{
    private MaterialLayer? _layer;

    /// <summary>The layer pass and composition operation used when this material contributes a layer.</summary>
    public MaterialLayer? Layer
    {
        get => _layer;
        set
        {
            if (_layer == value) return;
            _layer = value;
            if (!_changingProperties) Revision++;
        }
    }
}
