using SomeEngine.Assets.Schema;
using SomeEngine.Assets;
using SomeEngine.Serialization.Containers;

namespace DefaultGame.Content;

/// <summary>Editable project defaults. These declarations are the source of the built material assets.</summary>
internal static class SampleMaterials
{
    internal static async Task BuildAsync(AssetProject project, CancellationToken cancellationToken)
    {
        Guid standardShader = project.Resolve("assets/Shaders/standard.shader")
            ?? throw new InvalidDataException("The Standard shader has not been built.");
        Guid unlitShader = project.Resolve("assets/Shaders/unlit.shader")
            ?? throw new InvalidDataException("The Unlit shader has not been built.");
        using var standard = new StandardMaterialAsset(SampleAssetIds.StandardMaterial)
        {
            Name = "Standard",
        };
        _ = new StandardMaterialParameters(standard.GetBlock<MaterialBlock>())
        {
            AlbedoMap = new(SampleAssetIds.WhiteTexture),
            NormalMap = new(SampleAssetIds.NormalTexture),
            ARMMap = new(SampleAssetIds.WhiteTexture),
        };
        standard.GetBlock<MaterialBlock>().Shader = new(standardShader);
        AssetEntry standardEntry = await standard.SaveAsync(Path.Combine(project.RootPath, "assets/Materials/Standard.material.asset"), cancellationToken).ConfigureAwait(false);
        project.RegisterAsset(standardEntry);
        using var unlit = new UnlitMaterialAsset(SampleAssetIds.UnlitMaterial)
        {
            Name = "Unlit",
        };
        _ = new UnlitMaterialParameters(unlit.GetBlock<MaterialBlock>())
        {
            AlbedoMap = new(SampleAssetIds.WhiteTexture),
        };
        unlit.GetBlock<MaterialBlock>().Shader = new(unlitShader);
        AssetEntry unlitEntry = await unlit.SaveAsync(Path.Combine(project.RootPath, "assets/Materials/TestUnlit_1.material.asset"), cancellationToken).ConfigureAwait(false);
        project.RegisterAsset(unlitEntry);
    }
}
