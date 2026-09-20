using System.Numerics;
using Game.Assets;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization.Containers;

namespace Game.Build;

/// <summary>Editable project defaults. These declarations are the source of the built material assets.</summary>
internal static class DefaultMaterials
{
    internal static async Task BuildAsync(AssetProject project, CancellationToken cancellationToken)
    {
        Guid standardShader = project.Resolve("assets/Shaders/standard.shader")
            ?? throw new InvalidDataException("The Standard shader has not been built.");
        Guid unlitShader = project.Resolve("assets/Shaders/unlit.shader")
            ?? throw new InvalidDataException("The Unlit shader has not been built.");
        using var standard = new StandardMaterialAsset(PageArena.Shared, DefaultContentIds.StandardMaterial)
        {
            Name = "Standard",
        };
        _ = new StandardMaterialParameters(standard.GetBlock<MaterialBlock>())
        {
            AlbedoMap = new(DefaultContentIds.WhiteTexture),
            NormalMap = new(DefaultContentIds.NormalTexture),
            ARMMap = new(DefaultContentIds.ArmTexture),
            BaseColorTint = Vector4.One,
            MetallicFactor = 0,
            Roughness = 0.5f,
            EmissiveFactor = Vector3.Zero,
        };
        standard.GetBlock<MaterialBlock>().Shader = new(standardShader);
        AssetEntry standardEntry = await standard.SaveAsync(Path.Combine(project.RootPath, "assets/Materials/Standard.material.asset"), cancellationToken).ConfigureAwait(false);
        project.RegisterAsset(standardEntry);
        using var unlit = new UnlitMaterialAsset(PageArena.Shared, DefaultContentIds.UnlitMaterial)
        {
            Name = "Unlit",
        };
        _ = new UnlitMaterialParameters(unlit.GetBlock<MaterialBlock>())
        {
            AlbedoMap = new(DefaultContentIds.WhiteTexture),
            BaseColorTint = Vector4.One,
        };
        unlit.GetBlock<MaterialBlock>().Shader = new(unlitShader);
        AssetEntry unlitEntry = await unlit.SaveAsync(Path.Combine(project.RootPath, "assets/Materials/TestUnlit_1.material.asset"), cancellationToken).ConfigureAwait(false);
        project.RegisterAsset(unlitEntry);
    }
}
