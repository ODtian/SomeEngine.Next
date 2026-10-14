using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SharpGLTF.Schema2;
using GltfMesh = SharpGLTF.Schema2.Mesh;
using GltfMaterial = SharpGLTF.Schema2.Material;
using GltfTexture = SharpGLTF.Schema2.Texture;

namespace SomeEngine.Assets.Importers;

public abstract class GltfAssetSource<TSource, TAsset>(GltfImporterSettings settings, string sourcePath, string kind)
    : AssetSource<TSource, TAsset> where TSource : ExtraProperties where TAsset : SomeEngine.Assets.Asset
{
    protected abstract int GetIndex(TSource source);

    public override string GetElementKey(TSource source)
    {
        string? persistentId = source.Extras?[settings.PersistentIdProperty]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(persistentId)) return $"{kind}:id:{persistentId}";
        int index = GetIndex(source);
        if (settings.RequirePersistentElementIds)
            throw new InvalidDataException($"glTF {kind} {index} lacks extras.{settings.PersistentIdProperty}.");
        return $"{kind}:index:{index}";
    }

    public override string GetOutputPath(TSource source, Guid id)
        => Path.Combine(Path.GetDirectoryName(sourcePath)!, $"{Path.GetFileNameWithoutExtension(sourcePath)}.{id:N}.{kind}.asset");
}

public sealed class GltfMeshSource(GltfImporterSettings settings, string path) : GltfAssetSource<GltfMesh, MeshAsset>(settings, path, "mesh")
{
    protected override int GetIndex(GltfMesh source) => source.LogicalIndex;
    public override MeshAsset CreateAsset(GltfMesh source, Guid id) => new(id) { Name = source.Name ?? $"Mesh_{source.LogicalIndex}" };
}

public sealed class GltfSceneSource(GltfImporterSettings settings, string path) : GltfAssetSource<Scene, WorldAsset>(settings, path, "scene")
{
    protected override int GetIndex(Scene source) => source.LogicalIndex;
    public override WorldAsset CreateAsset(Scene source, Guid id) => new(id) { Name = source.Name ?? $"Scene_{source.LogicalIndex}" };
}

public sealed class GltfMaterialSource(GltfImporterSettings settings, string path) : GltfAssetSource<GltfMaterial, MaterialAsset>(settings, path, "material")
{
    protected override int GetIndex(GltfMaterial source) => source.LogicalIndex;
    public override MaterialAsset CreateAsset(GltfMaterial source, Guid id) => new(id) { Name = source.Name ?? $"Material_{source.LogicalIndex}" };
}

public sealed class GltfTextureSource(GltfImporterSettings settings, string path) : GltfAssetSource<GltfTexture, TextureAsset>(settings, path, "texture")
{
    protected override int GetIndex(GltfTexture source) => source.LogicalIndex;
    public override TextureAsset CreateAsset(GltfTexture source, Guid id) => new(id) { Name = source.Name ?? source.PrimaryImage?.Name ?? $"Texture_{source.LogicalIndex}" };
}
