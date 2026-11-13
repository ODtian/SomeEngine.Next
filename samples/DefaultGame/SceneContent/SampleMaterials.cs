using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;

namespace DefaultGame.Content;

/// <summary>Builds the sample's material composition entries against shared engine shader inputs.</summary>
public static class SampleMaterials
{
    public static async Task BuildAsync(AssetProject project, string engineRoot, CancellationToken cancellationToken)
    {
        string[] sources = ["assets/Shaders/standard.shader", "assets/Shaders/unlit.shader"];
        await project.BuildAllAsync(sources, 2, cancellationToken).ConfigureAwait(false);
        var standard = new AssetId<ShaderAsset>(project.Resolve(sources[0])!.Value);
        var unlit = new AssetId<ShaderAsset>(project.Resolve(sources[1])!.Value);
        await MaterialTemplates.SaveAsync(project, standard, unlit, cancellationToken).ConfigureAwait(false);
    }
}
