using SomeEngine.Assets.Schema;
using GltfMaterial = SharpGLTF.Schema2.Material;
using GltfTexture = SharpGLTF.Schema2.Texture;

namespace SomeEngine.Assets.Importers;

/// <summary>Maps glTF material values to the project's shader-defined material data.</summary>
public interface IGltfMaterialConversion
{
    IEnumerable<GltfTexture> Textures(GltfMaterial material);
    void Apply(MaterialAsset asset, GltfMaterial material, Func<GltfTexture?, AssetId<TextureAsset>> resolveTexture);
}
