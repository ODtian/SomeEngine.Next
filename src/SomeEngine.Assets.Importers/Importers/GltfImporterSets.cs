namespace SomeEngine.Assets.Importers;

[AssetImporterSet(NeutralId, "glTF shared data", typeof(GltfMeshImporter), typeof(GltfTextureImporter), Neutral = true)]
[AssetImporterSet(ClusterId, "Cluster mesh", typeof(ClusterGltfMeshImporter))]
public static class GltfImporterSets
{
    public const string NeutralId = "dd503c85-0b44-4e55-9aeb-806cebdc27c6";
    public const string ClusterId = "04bf0353-5bf3-4824-a87a-3868d3b5a441";
}
