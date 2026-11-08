namespace SomeEngine.Assets.Importers;

[AssetImporterSet(NeutralId, "glTF shared data", typeof(GltfMeshImporter), typeof(GltfTextureImporter), typeof(GltfVertexStreamsImporter), typeof(MorphTargetSetImporter), Neutral = true)]
[AssetImporterSet(ClusterId, "Cluster mesh", typeof(ClusterGeometryImporter), typeof(ClusterVertexAttributesImporter),
    typeof(ClusterSkinInfluencesImporter), typeof(ClusterSkinBoundsImporter), typeof(ClusterMorphDeltasImporter))]
[AssetImporterSet(DistanceFieldId, "Mesh distance fields", typeof(MeshDistanceFieldImporter))]
public static class GltfImporterSets
{
    public const string NeutralId = "dd503c85-0b44-4e55-9aeb-806cebdc27c6";
    public const string ClusterId = "04bf0353-5bf3-4824-a87a-3868d3b5a441";
    public const string DistanceFieldId = "e253ed1e-84f2-4901-98c3-f9721a962b18";
}
