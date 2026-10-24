using Microsoft.Extensions.DependencyInjection;
using SharpGLTF.Schema2;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class ClusterDeformationImportBehaviorTests
{
    [Fact]
    public async Task AllInfluenceSetsFollowFinalClusterVerticesThroughSaveAndOpen()
    {
        var attributes = new Dictionary<string, VertexStream>
        {
            ["POSITION"] = new(3, [0,0,0, 1,0,0, 0,1,0, 1,1,0]),
            ["NORMAL"] = new(3, [0,0,1, 0,0,1, 0,0,1, 0,0,1]),
            ["JOINTS_0"] = new(4, [0,1,2,3, 4,5,6,7, 8,9,10,11, 12,13,14,15]),
            ["JOINTS_7"] = new(4, [16,17,18,19, 20,21,22,23, 24,25,26,27, 28,29,30,31]),
            ["WEIGHTS_0"] = new(4, [.5,.125,0,0, .25,.25,0,0, .125,.125,.125,.125, 0,0,.5,0]),
            ["WEIGHTS_7"] = new(4, [.125,.125,.125,0, .125,.125,.125,.125, .25,.25,0,0, .25,0,0,.25]),
        };
        var primitive = new VertexPrimitive(0, 0, [2,0,1, 1,3,2], [40,10,30,20], attributes, []);
        await using ImportResult imported = await ImportAsync(primitive);
        using var files = new MeshFiles();
        await imported.Asset.SaveAsync(files.Path);
        using MeshAsset reopened = await MeshAsset.OpenAsync(files.Path);

        ClusterGeometryBlock geometry = await reopened.GetBlockAsync<ClusterGeometryBlock>();
        ClusterSkinInfluencesBlock influences = await reopened.GetBlockAsync<ClusterSkinInfluencesBlock>();
        Assert.Equal(geometry.VertexIdentity, influences.VertexIdentity);
        Assert.Equal(geometry.Clusters.Count, influences.Clusters.Count);
        for (int index = 0; index < geometry.Clusters.Count; index++)
        {
            ClusterGeometry cluster = geometry.Clusters[index];
            ClusterVertexAttributes data = influences.Clusters[index];
            Assert.Equal(new[] { "JOINTS_0", "JOINTS_7", "WEIGHTS_0", "WEIGHTS_7" },
                data.Attributes.Select(value => value.Name).Order(StringComparer.Ordinal));
            for (int vertex = 0; vertex < cluster.Vertices.Length; vertex++)
            {
                uint source = geometry.Vertices[(int)cluster.Vertices[vertex]].SourceVertex;
                int row = source switch { 40 => 0, 10 => 1, 30 => 2, 20 => 3, _ => throw new InvalidOperationException() };
                foreach (ClusterVertexAttribute stream in data.Attributes)
                    for (int component = 0; component < 4; component++)
                        Assert.Equal(attributes[stream.Name].Values.Span[row * 4 + component], stream.ReadComponent(vertex, component));
            }
        }
    }

    [Fact]
    public async Task MorphTargetsAndSourceTangentsFollowFinalVerticesThroughSaveAndOpen()
    {
        var attributes = new Dictionary<string, VertexStream>
        {
            ["POSITION"] = new(3, [0,0,0, 1,0,0, 0,1,0, 1,1,0]),
            ["NORMAL"] = new(3, [0,0,1, 0,0,1, 0,0,1, 0,0,1]),
            ["TANGENT"] = new(4, [1,0,0,1, 1,0,0,1, 1,0,0,1, 1,0,0,1]),
        };
        IReadOnlyDictionary<string, VertexStream>[] targets =
        [
            new Dictionary<string, VertexStream>
            {
                ["POSITION"] = new(3, [0,0,1, 0,0,2, 0,0,3, 0,0,4]),
                ["NORMAL"] = new(3, [.25,0,0, .5,0,0, .75,0,0, 1,0,0]),
                ["TANGENT"] = new(3, [0,.25,0, 0,.5,0, 0,.75,0, 0,1,0]),
            },
            new Dictionary<string, VertexStream>
            {
                ["POSITION"] = new(3, [1,0,0, 2,0,0, 3,0,0, 4,0,0]),
                ["TEXCOORD_3"] = new(2, [.25,.5, .5,.75, .75,1, 1,.25]),
                ["_DETAIL"] = new(1, [10,20,30,40]),
            },
        ];
        VertexStream?[] sourceTangents =
            [new(3, [.75,.5,.25, .5,.25,0, .25,0,-.25, 0,-.25,-.5]), null];
        var primitive = new VertexPrimitive(0, 0, [2,0,1, 1,3,2], [40,10,30,20], attributes, targets,
            sourceMorphTangents: sourceTangents);
        await using ImportResult imported = await ImportAsync(primitive);
        using var files = new MeshFiles();
        await imported.Asset.SaveAsync(files.Path);
        using MeshAsset reopened = await MeshAsset.OpenAsync(files.Path);

        ClusterGeometryBlock geometry = await reopened.GetBlockAsync<ClusterGeometryBlock>();
        ClusterMorphDeltasBlock morphs = await reopened.GetBlockAsync<ClusterMorphDeltasBlock>();
        Assert.Equal(geometry.VertexIdentity, morphs.VertexIdentity);
        Assert.Equal(geometry.Clusters.Count, morphs.Clusters.Count);
        for (int index = 0; index < geometry.Clusters.Count; index++)
        {
            ClusterGeometry cluster = geometry.Clusters[index];
            ClusterMorphDeltas data = morphs.Clusters[index];
            Assert.Equal(2, data.Targets.Length);
            ClusterVertexAttribute originalTangents = Assert.IsType<ClusterVertexAttribute>(data.SourceTangents[0]);
            Assert.Null(data.SourceTangents[1]);
            for (int target = 0; target < targets.Length; target++)
                Assert.Equal(targets[target].Keys.Order(StringComparer.Ordinal),
                    data.Targets[target].Attributes.Select(value => value.Name).Order(StringComparer.Ordinal));
            for (int vertex = 0; vertex < cluster.Vertices.Length; vertex++)
            {
                uint source = geometry.Vertices[(int)cluster.Vertices[vertex]].SourceVertex;
                int row = source switch { 40 => 0, 10 => 1, 30 => 2, 20 => 3, _ => throw new InvalidOperationException() };
                for (int target = 0; target < targets.Length; target++)
                {
                    foreach (ClusterVertexAttribute stream in data.Targets[target].Attributes)
                        for (int component = 0; component < stream.ComponentCount; component++)
                            Assert.Equal(targets[target][stream.Name].Values.Span[row * stream.ComponentCount + component],
                                stream.ReadComponent(vertex, component));
                }
                for (int component = 0; component < 3; component++)
                    Assert.Equal(sourceTangents[0]!.Values.Span[row * 3 + component], originalTangents.ReadComponent(vertex, component));
            }
        }
    }

    private static async Task<ImportResult> ImportAsync(VertexPrimitive primitive)
    {
        var context = new ImportContext();
        context.EnableImporter<GltfMesh, MeshAsset>(Guid.NewGuid(), [VertexStreamsBlock.BlockTypeId],
            (_, _, output, _) => { output.SetBlock(new VertexStreamsBlock([primitive], [])); return ValueTask.CompletedTask; });
        var services = new ServiceCollection();
        PoseTestAssets.AddAssetCapabilities(services);
        ServiceProvider provider = services.BuildServiceProvider();
        context.Hold(provider);
        AssetImporterSetCapability cluster = provider.GetServices<AssetImporterSetCapability>()
            .Single(value => value.Id == new Guid(GltfImporterSets.ClusterId));
        foreach (AssetImporterCapability capability in provider.GetServices<AssetImporterCapability>()
            .Where(value => cluster.Importers.Contains(value.Id)))
            context.EnableImporter<GltfMesh, MeshAsset>(capability.Bind<GltfMesh>(provider));
        GltfMesh mesh = ModelRoot.CreateModel().CreateMesh("deforming mesh");
        string key = $"mesh/{mesh.LogicalIndex}";
        Guid id = context.GetAssetId(key);
        context.AddAsset(mesh, new MeshAsset(id), key, id + ".mesh.asset");
        MeshAsset asset = await context.GetAssetAsync<GltfMesh, MeshAsset>(mesh);
        return new(context, asset);
    }

    private sealed class ImportResult(ImportContext context, MeshAsset asset) : IAsyncDisposable
    {
        public MeshAsset Asset => asset;
        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private sealed class MeshFiles : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(AppContext.BaseDirectory, "cluster-deformation-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "character.mesh.asset");
        public MeshFiles() => Directory.CreateDirectory(_directory);
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
