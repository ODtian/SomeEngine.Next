using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Runtime;

namespace DefaultGame;

public readonly struct SampleSceneMaterials : ISceneMaterials
{
    public static readonly AssetId<MaterialAsset> ImportedTemplate = new(new Guid("5447a95f-6eca-4a89-9374-b004a4fe949d"));
    public const string ImportedTemplatePath = "assets/Materials/Imported.material.asset";

    public static async ValueTask<Material> CreateAsync(MaterialAsset asset, AssetLoader assets,
        CancellationToken cancellationToken = default)
    {
        AssetId<MaterialAsset> template = await asset.GetTemplateAsync(assets, cancellationToken);
        return template == ImportedTemplate
            ? await asset.CreateAsync<ImportedScenes.SceneMaterial>(assets, cancellationToken)
            : await EngineSceneMaterials.CreateAsync(asset, assets, cancellationToken);
    }
}
