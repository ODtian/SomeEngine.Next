using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Runtime;

/// <summary>The game's concrete material creation choices for its scene content.</summary>
public interface ISceneMaterials
{
    static abstract ValueTask<Material> CreateAsync(MaterialAsset asset, AssetLoader assets,
        CancellationToken cancellationToken = default);
}

/// <summary>Creates the engine's Standard and Unlit materials from their business templates.</summary>
public readonly struct EngineSceneMaterials : ISceneMaterials
{
    public static async ValueTask<Material> CreateAsync(MaterialAsset asset, AssetLoader assets,
        CancellationToken cancellationToken = default)
    {
        AssetId<MaterialAsset> template = await asset.GetTemplateAsync(assets, cancellationToken).ConfigureAwait(false);
        return template == MaterialTemplates.Unlit
            ? await asset.CreateAsync<UnlitMaterial>(assets, cancellationToken).ConfigureAwait(false)
            : await asset.CreateAsync<StandardMaterial>(assets, cancellationToken).ConfigureAwait(false);
    }
}
