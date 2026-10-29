using SomeEngine.Assets;
using SomeEngine.Assets.Schema;

namespace DefaultGame.Content;

public static class MeshDistanceFields
{
    public static async Task BuildAsync(AssetProject project, CancellationToken cancellationToken)
    {
        var entries = project.List(MeshAsset.FileType).ToArray();
        int completed = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(project.RootPath, entry.Path);
            using MeshAsset mesh = await MeshAsset.OpenAsync(path, cancellationToken);
            ClusterGeometryBlock? geometry = await mesh.TryGetBlockAsync<ClusterGeometryBlock>(cancellationToken);
            if (geometry is null) continue;
            ClusterVertexAttributesBlock attributes = await mesh.GetBlockAsync<ClusterVertexAttributesBlock>(cancellationToken);
            using MeshDistanceFieldBlock field = MeshDistanceFieldBlock.Build(geometry, attributes, 4, 32, cancellationToken);
            mesh.SetBlock(field);
            project.RegisterAsset(await mesh.SaveAsync(path, cancellationToken));
            Console.WriteLine($"Mesh fields {++completed}: {mesh.Name}");
        }
    }
}
