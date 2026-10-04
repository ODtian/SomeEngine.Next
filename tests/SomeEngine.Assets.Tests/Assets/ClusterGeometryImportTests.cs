using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using SharpGLTF.Schema2;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources.Geometry;
using SomeEngine.Tests;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ClusterGeometryImportTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(.0001f)]
    [InlineData(10000)]
    public async Task NewPositionsMatchTheRetainedClusterBlock(float scale)
    {
        VertexPrimitive primitive = Triangle();
        double[] values = primitive.Attributes["POSITION"].Values.ToArray();
        for (int index = 0; index < values.Length; index++) values[index] *= scale;
        primitive = WithAttribute(primitive, "POSITION", new(3, values.AsSpan()));
        await using ImportResult imported = await ImportAsync([primitive]);
        Vector3[] positions = Enumerable.Range(0, 3).Select(vertex => new Vector3((float)values[vertex * 3],
            (float)values[vertex * 3 + 1], (float)values[vertex * 3 + 2])).ToArray();
        using MeshAsset old = ClusterBuilder.ProcessRaw(positions, [], [0, 1, 2], ClusterBuilderTestMaterials.Slots(), "Old geometry");
        List<Vector3> oldPositions = await DecodeOldPositionsAsync(old.GetBlock<ClusterMeshBlock>());
        ClusterGeometry cluster = Assert.Single(imported.Geometry.Clusters);
        Assert.Equal(oldPositions.Count, cluster.Vertices.Length);
        for (int vertex = 0; vertex < cluster.Vertices.Length; vertex++)
        {
            Vector3 decoded = cluster.DecodePosition(vertex);
            Assert.Contains(oldPositions, position => Vector3.Distance(position, decoded) <= cluster.PositionStep * 2);
            Vector3 center = new(cluster.Center.X, cluster.Center.Y, cluster.Center.Z);
            Assert.True(Vector3.Distance(center, decoded) <= cluster.Radius);
        }
    }

    [Fact]
    public async Task AttributesPreserveEveryUvColorAndCustomComponentThroughSaveAndOpen()
    {
        VertexPrimitive triangle = Triangle();
        triangle = WithAttribute(triangle, "TANGENT", new(4, [1, 0, 0, -1, 1, 0, 0, 1, 1, 0, 0, -1]));
        foreach (int set in new[] { 0, 1, 7, 35 })
        {
            triangle = WithAttribute(triangle, $"TEXCOORD_{set}", new(2, [set, .2, .3, .4, .5, .6]));
            triangle = WithAttribute(triangle, $"COLOR_{set}", new(4, [.1, .2, .3, .4, .5, .6, .7, .8, .9, 1, .1, .2]));
        }
        triangle = WithAttribute(triangle, "_INTEGER", new(1, [uint.MaxValue, 16777217, 0]));
        triangle = WithAttribute(triangle, "_MATRIX", new(16, Enumerable.Range(0, 48).Select(value => (double)value).ToArray().AsSpan()));
        triangle = new(triangle.SourcePrimitive, triangle.Material, triangle.Indices.Span, [90, 10, 20], triangle.Attributes, triangle.MorphTargets);
        await using ImportResult imported = await ImportAsync([triangle]);
        AssertAttributes(imported.Geometry, imported.Attributes, [triangle]);
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "attributes.mesh.asset");
            await imported.Asset.SaveAsync(path);
            using MeshAsset reopened = await MeshAsset.OpenAsync(path);
            ClusterGeometryBlock geometry = await reopened.GetBlockAsync<ClusterGeometryBlock>();
            ClusterVertexAttributesBlock attributes = await reopened.GetBlockAsync<ClusterVertexAttributesBlock>();
            AssertAttributes(geometry, attributes, [triangle]);
            Assert.Equal(imported.Geometry.VertexIdentity, geometry.VertexIdentity);
            Assert.Equal(ClusterGeometryBlock.EncodingCompatibility, geometry.PositionEncoding);
            Assert.Equal(ClusterVertexAttributesBlock.EncodingCompatibility, attributes.Encoding);
            Assert.Equal(geometry.VertexIdentity, attributes.VertexIdentity);
            Assert.All(geometry.Clusters, cluster =>
                Assert.Equal(imported.Geometry.Clusters[0].DecodePosition(0), cluster.DecodePosition(0)));
            Assert.DoesNotContain(reopened.BlockDescriptions, description => description.BlockTypeId == VertexStreamsBlock.BlockTypeId);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task EachDeformKindHasSeparateClustersAndLodGroups()
    {
        VertexPrimitive[] primitives = Enumerable.Range(0, 4).Select(kind => Triangle(kind, (ClusterDeform)kind)).ToArray();
        await using ImportResult imported = await ImportAsync(primitives);
        Assert.Equal(4, imported.Geometry.Clusters.Count);
        Assert.Equal(4, imported.Geometry.Clusters.Select(cluster => cluster.Group).Distinct().Count());
        foreach (ClusterGeometry cluster in imported.Geometry.Clusters)
        {
            Assert.All(cluster.Vertices, vertex => Assert.Equal((int)cluster.Deform, imported.Geometry.Vertices[(int)vertex].Primitive));
            Assert.Equal(3, cluster.Vertices.Length);
        }
    }

    [Theory]
    [InlineData("NORMAL")]
    [InlineData("TANGENT")]
    [InlineData("TEXCOORD_7")]
    [InlineData("COLOR_2")]
    [InlineData("_CUSTOM")]
    [InlineData("JOINTS_5")]
    [InlineData("WEIGHTS_5")]
    [InlineData("morph")]
    public async Task EveryStreamParticipatesInBitwiseWelding(string changedStream)
    {
        ClusterDeform deform = changedStream == "morph" ? ClusterDeform.Morph :
            changedStream.StartsWith("JOINTS_") || changedStream.StartsWith("WEIGHTS_") ? ClusterDeform.Skin : ClusterDeform.Static;
        VertexPrimitive first = Triangle(0, deform);
        string name = changedStream == "morph" ? "_CUSTOM" : changedStream;
        int components = name switch { "NORMAL" => 3, "TANGENT" or "COLOR_2" or "JOINTS_5" or "WEIGHTS_5" => 4, "TEXCOORD_7" => 2, _ => 1 };
        double[] values = new double[3 * components];
        if (name == "NORMAL") values = first.Attributes[name].Values.ToArray();
        first = WithAttribute(first, name, new(components, values.AsSpan()));
        VertexPrimitive second = Triangle(1, deform);
        second = WithAttribute(second, name, new(components, values.AsSpan()));
        if (changedStream == "morph")
        {
            var targets = first.MorphTargets.Select(target => (IReadOnlyDictionary<string, VertexStream>)new Dictionary<string, VertexStream>(target)).ToArray();
            ((Dictionary<string, VertexStream>)targets[0])["_CUSTOM"] = new(1, [0, 0, 0]);
            first = new(0, -1, [0, 1, 2], [0, 1, 2], first.Attributes, targets);
            ((Dictionary<string, VertexStream>)targets[0])["_CUSTOM"] = new(1, [-0.0, 0, 0]);
            second = new(1, -1, [0, 1, 2], [0, 1, 2], second.Attributes, targets);
        }
        else
        {
            values[0] = BitConverter.Int64BitsToDouble(long.MinValue);
            second = WithAttribute(second, name, new(components, values.AsSpan()));
        }
        await using ImportResult imported = await ImportAsync([first, second]);
        Assert.Equal(4, imported.Geometry.Vertices.Count);
        Assert.Equal(6, imported.Geometry.Clusters.Where(cluster => cluster.Level == 0).Sum(cluster => cluster.Indices.Length));
    }

    [Fact]
    public async Task IdenticalRowsAcrossPrimitivesShareTheirVertexIdentity()
    {
        await using ImportResult imported = await ImportAsync([Triangle(0), Triangle(1)]);
        Assert.Equal(3, imported.Geometry.Vertices.Count);
        AssertAttributes(imported.Geometry, imported.Attributes, [Triangle(0), Triangle(1)]);
    }

    [Theory]
    [InlineData("material")]
    [InlineData("NORMAL")]
    [InlineData("TEXCOORD_3")]
    public async Task LodKeepsSeamVerticesAndTriangleMaterials(string seam)
    {
        VertexPrimitive[] primitives = [Grid(0, 0, 16, seam), Grid(1, 16, 32, seam)];
        await using ImportResult imported = await ImportAsync(primitives);
        ClusterGeometryBlock geometry = imported.Geometry;
        Assert.Contains(geometry.Clusters, cluster => cluster.Level > 0);
        uint[] seamVertices = geometry.Vertices.Select((row, vertex) => (row, vertex))
            .Where(value => primitives[value.row.Primitive].Attributes["POSITION"].Values.Span[value.row.Vertex * 3] == 16)
            .Select(value => (uint)value.vertex).ToArray();
        foreach (uint seamVertex in seamVertices)
            Assert.Contains(geometry.Clusters, cluster => cluster.Level > 0 && cluster.Vertices.Contains(seamVertex));
        foreach (ClusterGeometry cluster in geometry.Clusters)
        {
            for (int triangle = 0; triangle < cluster.Indices.Length / 3; triangle++)
            {
                int material = cluster.TriangleMaterials[triangle];
                for (int corner = 0; corner < 3; corner++)
                {
                    ClusterVertexSource row = geometry.Vertices[(int)cluster.Vertices[cluster.Indices[triangle * 3 + corner]]];
                    Assert.Equal(primitives[row.Primitive].Material, material);
                }
            }
        }
        AssertAttributes(geometry, imported.Attributes, primitives);
    }

    [Fact]
    public async Task MixedSchemasRetainAbsenceAndSourceComponentCounts()
    {
        VertexPrimitive first = WithAttribute(Triangle(0), "_DATA", new(1, [1, 2, 3]));
        VertexPrimitive second = WithAttribute(Triangle(1), "_DATA", new(3, [1, 2, 3, 4, 5, 6, 7, 8, 9]));
        VertexPrimitive third = Triangle(2);
        await using ImportResult imported = await ImportAsync([first, second, third]);
        AssertAttributes(imported.Geometry, imported.Attributes, [first, second, third]);
    }

    [Theory]
    [InlineData(ClusterDeform.Skin)]
    [InlineData(ClusterDeform.Morph)]
    [InlineData(ClusterDeform.Skin | ClusterDeform.Morph)]
    public async Task LodKeepsVerticesAlongChangingInfluencesAndMorphDeltas(ClusterDeform deform)
    {
        VertexPrimitive grid = Grid(0, 0, 32, "material");
        var attributes = new Dictionary<string, VertexStream>(grid.Attributes);
        int count = grid.SourceVertices.Length;
        double[] changes = new double[count];
        for (int vertex = 0; vertex < count; vertex++)
            changes[vertex] = attributes["POSITION"].Values.Span[vertex * 3] == 16 ? .5 : 0;
        if (deform.HasFlag(ClusterDeform.Skin))
        {
            attributes["JOINTS_8"] = new(1, new double[count].AsSpan());
            attributes["WEIGHTS_8"] = new(1, changes.AsSpan());
        }
        IReadOnlyDictionary<string, VertexStream>[] targets = deform.HasFlag(ClusterDeform.Morph)
            ? [new Dictionary<string, VertexStream> { ["_DELTA"] = new(1, changes.AsSpan()) }] : [];
        var primitive = new VertexPrimitive(0, 0, grid.Indices.Span, grid.SourceVertices.Span, attributes, targets);
        await using ImportResult imported = await ImportAsync([primitive]);
        Assert.Contains(imported.Geometry.Clusters, cluster => cluster.Level > 0);
        Assert.All(imported.Geometry.Clusters, cluster => Assert.Equal(deform, cluster.Deform));
        foreach (var vertex in imported.Geometry.Vertices.Select((row, vertex) => (row, vertex)).Where(value => changes[value.row.Vertex] != 0))
            Assert.Contains(imported.Geometry.Clusters, cluster => cluster.Level > 0 && cluster.Vertices.Contains((uint)vertex.vertex));
    }

    [Fact]
    public async Task PackageGroupsProduceNewBlocksAlongsideTheRetainedBlock()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "triangle.gltf");
            File.WriteAllText(path, """
                {"asset":{"version":"2.0"},
                "buffers":[{"uri":"data:application/octet-stream;base64,AAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAgD8AAAAA","byteLength":36}],
                "bufferViews":[{"buffer":0,"byteLength":36}],
                "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]}],
                "meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}]}
                """);
            ModelRoot model = await new GltfSourceImporter(static _ => { }, "cluster-tests").ReadAsync(path, CancellationToken.None);
            await using var context = CreateContext();
            var services = new ServiceCollection();
            Game.Importing.ProjectAssetCapabilities.AddAssetCapabilities(services);
            ServiceProvider provider = services.BuildServiceProvider();
            context.Hold(provider);
            foreach (AssetImporterSetCapability set in provider.GetServices<AssetImporterSetCapability>()
                .Where(set => set.Id == new Guid(GltfImporterSets.NeutralId) || set.Id == new Guid(GltfImporterSets.ClusterId)))
            {
                foreach (AssetImporterCapability capability in provider.GetServices<AssetImporterCapability>()
                    .Where(capability => set.Importers.Contains(capability.Id) && capability.SourceType == typeof(GltfMesh)))
                    context.EnableImporter<GltfMesh, MeshAsset>(capability.Bind<GltfMesh>(provider));
            }
            MeshAsset mesh = await context.GetAssetAsync<GltfMesh, MeshAsset>(model.LogicalMeshes[0]);
            Assert.Single(mesh.GetBlock<ClusterGeometryBlock>().Clusters);
            Assert.Single(mesh.GetBlock<ClusterVertexAttributesBlock>().Clusters);
            Assert.Equal(3, (await DecodeOldPositionsAsync(mesh.GetBlock<ClusterMeshBlock>())).Count);
            string saved = Path.Combine(directory, "triangle.mesh.asset");
            await mesh.SaveAsync(saved);
            using MeshAsset reopened = await MeshAsset.OpenAsync(saved);
            Assert.Single((await reopened.GetBlockAsync<ClusterGeometryBlock>()).Clusters);
            Assert.Single((await reopened.GetBlockAsync<ClusterVertexAttributesBlock>()).Clusters);
            Assert.Equal(3, (await DecodeOldPositionsAsync(await reopened.GetBlockAsync<ClusterMeshBlock>())).Count);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static VertexPrimitive Triangle(int source = 0, ClusterDeform deform = ClusterDeform.Static)
    {
        var attributes = new Dictionary<string, VertexStream>
        {
            ["POSITION"] = new(3, [0, 0, 0, 1.12345, 0, 0, 0, 1.6789, 0]),
            ["NORMAL"] = new(3, [0, 0, 1, 0, 0, 1, 0, 0, 1]),
        };
        if (deform.HasFlag(ClusterDeform.Skin))
        {
            attributes["JOINTS_5"] = new(4, new double[12].AsSpan());
            attributes["WEIGHTS_5"] = new(4, [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0]);
        }
        IReadOnlyDictionary<string, VertexStream>[] morphs = deform.HasFlag(ClusterDeform.Morph)
            ? [new Dictionary<string, VertexStream> { ["POSITION"] = new(3, new double[9].AsSpan()) }] : [];
        return new(source, -1, [0, 1, 2], [0, 1, 2], attributes, morphs);
    }

    private static VertexPrimitive WithAttribute(VertexPrimitive primitive, string name, VertexStream stream)
    {
        var attributes = new Dictionary<string, VertexStream>(primitive.Attributes) { [name] = stream };
        return new(primitive.SourcePrimitive, primitive.Material, primitive.Indices.Span, primitive.SourceVertices.Span, attributes, primitive.MorphTargets);
    }

    private static VertexPrimitive Grid(int primitive, int firstX, int lastX, string seam)
    {
        int width = lastX - firstX + 1, height = 17;
        List<double> positions = [], normals = [], uvs = [];
        List<uint> indices = [];
        for (int y = 0; y < height; y++)
        for (int x = firstX; x <= lastX; x++)
        {
            positions.AddRange([x, y, 0]);
            normals.AddRange(seam == "NORMAL" && primitive == 1 ? new double[] { 0, 1, 0 } : [0, 0, 1]);
            uvs.AddRange([x + (seam == "TEXCOORD_3" ? primitive : 0), y]);
        }
        for (int y = 0; y < height - 1; y++)
        for (int x = 0; x < width - 1; x++)
        {
            uint vertex = (uint)(y * width + x);
            indices.AddRange([vertex, vertex + 1, vertex + (uint)width, vertex + 1, vertex + (uint)width + 1, vertex + (uint)width]);
        }
        return new(primitive, seam == "material" ? primitive : -1, indices.ToArray().AsSpan(),
            Enumerable.Range(0, positions.Count / 3).Select(value => (uint)value).ToArray().AsSpan(),
            new Dictionary<string, VertexStream> { ["POSITION"] = new(3, positions.ToArray().AsSpan()), ["NORMAL"] = new(3, normals.ToArray().AsSpan()),
                ["TEXCOORD_3"] = new(2, uvs.ToArray().AsSpan()) }, []);
    }

    private static void AssertAttributes(ClusterGeometryBlock geometry, ClusterVertexAttributesBlock attributes, VertexPrimitive[] primitives)
    {
        Assert.Equal(geometry.VertexIdentity, attributes.VertexIdentity);
        Assert.Equal(geometry.Clusters.Count, attributes.Clusters.Count);
        for (int clusterIndex = 0; clusterIndex < geometry.Clusters.Count; clusterIndex++)
        {
            ClusterGeometry cluster = geometry.Clusters[clusterIndex];
            ClusterVertexAttributes data = attributes.Clusters[clusterIndex];
            for (int vertex = 0; vertex < cluster.Vertices.Length; vertex++)
            {
                ClusterVertexSource row = geometry.Vertices[(int)cluster.Vertices[vertex]];
                VertexPrimitive primitive = primitives.Single(value => value.SourcePrimitive == row.Primitive);
                Assert.Equal(primitive.SourceVertices.Span[row.Vertex], row.SourceVertex);
                foreach (var stream in primitive.Attributes.Where(pair => pair.Key != "POSITION" && !pair.Key.StartsWith("JOINTS_") && !pair.Key.StartsWith("WEIGHTS_")))
                {
                    ClusterVertexAttribute attribute = Assert.Single(data.Attributes, attribute => attribute.Name == stream.Key);
                    Assert.Equal(stream.Value.ComponentCount, attribute.VertexComponentCounts[vertex]);
                    for (int component = 0; component < stream.Value.ComponentCount; component++)
                        Assert.Equal(BitConverter.DoubleToInt64Bits(stream.Value.Values.Span[row.Vertex * stream.Value.ComponentCount + component]),
                            BitConverter.DoubleToInt64Bits(attribute.ReadComponent(vertex, component)));
                }
                foreach (ClusterVertexAttribute attribute in data.Attributes.Where(attribute => !primitive.Attributes.ContainsKey(attribute.Name)))
                    Assert.Equal(0, attribute.VertexComponentCounts[vertex]);
            }
        }
    }

    private static ImportContext CreateContext()
    {
        var context = new ImportContext();
        context.ConfigureAssets<GltfMesh, MeshAsset>(mesh => $"mesh/{mesh.LogicalIndex}", (_, id) => new MeshAsset(id),
            (_, id) => id + ".mesh.asset", static (asset, destination, token) => asset.SaveAsync(destination, token));
        return context;
    }

    private static async Task<ImportResult> ImportAsync(VertexPrimitive[] primitives)
    {
        ImportContext context = CreateContext();
        try
        {
            context.EnableImporter<GltfMesh, MeshAsset>(Guid.NewGuid(), [VertexStreamsBlock.BlockTypeId],
                (_, _, output, _) => { output.SetBlock(new VertexStreamsBlock((IReadOnlyList<VertexPrimitive>)primitives, [])); return ValueTask.CompletedTask; });
            var services = new ServiceCollection();
            Game.Importing.ProjectAssetCapabilities.AddAssetCapabilities(services);
            ServiceProvider provider = services.BuildServiceProvider();
            context.Hold(provider);
            foreach (AssetImporterCapability capability in provider.GetServices<AssetImporterCapability>()
                .Where(value => value.ImporterType == typeof(ClusterGeometryImporter) || value.ImporterType == typeof(ClusterVertexAttributesImporter)))
                context.EnableImporter<GltfMesh, MeshAsset>(capability.Bind<GltfMesh>(provider));
            MeshAsset asset = await context.GetAssetAsync<GltfMesh, MeshAsset>(ModelRoot.CreateModel().CreateMesh("Test mesh"));
            return new(context, asset);
        }
        catch { await context.DisposeAsync(); throw; }
    }

    private sealed class ImportResult(ImportContext context, MeshAsset asset) : IAsyncDisposable
    {
        public MeshAsset Asset => asset;
        public ClusterGeometryBlock Geometry => asset.GetBlock<ClusterGeometryBlock>();
        public ClusterVertexAttributesBlock Attributes => asset.GetBlock<ClusterVertexAttributesBlock>();
        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(TestProjectPaths.ProjectRoot(), ".cluster-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<List<Vector3>> DecodeOldPositionsAsync(ClusterMeshBlock block)
    {
        List<Vector3> positions = [];
        using var source = await block.OpenPayloadSourceAsync();
        for (int page = 0; page < block.PageDigests.Count; page++)
        {
            ReadOnlyMemory<byte> memory = await source.ReadPageAsync(page);
            ReadOnlySpan<byte> data = memory.Span;
            MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(data);
            ReadOnlySpan<PackedCluster> clusters = MemoryMarshal.Cast<byte, PackedCluster>(data.Slice((int)header.ClustersOffset, (int)header.ClusterCount * PackedCluster.SizeInBytes));
            foreach (PackedCluster cluster in clusters)
            {
                ReadOnlySpan<byte> blob = data.Slice((int)(header.VertexBlobsOffset + cluster.VertexBlobOffset), (int)cluster.VertexBlobSize);
                uint widths = BinaryPrimitives.ReadUInt32LittleEndian(blob[12..]);
                int stride = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[16..]);
                int start = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[20..]);
                Vector3 origin = new(BinaryPrimitives.ReadSingleLittleEndian(blob[24..]), BinaryPrimitives.ReadSingleLittleEndian(blob[28..]), BinaryPrimitives.ReadSingleLittleEndian(blob[32..]));
                float step = BinaryPrimitives.ReadSingleLittleEndian(blob[36..]);
                for (int vertex = 0; vertex < (cluster.PackedCounts & 255); vertex++)
                {
                    int bit = start + vertex * stride;
                    positions.Add(origin + new Vector3(ReadBits(blob, ref bit, (int)(widths & 31)),
                        ReadBits(blob, ref bit, (int)(widths >> 5 & 31)), ReadBits(blob, ref bit, (int)(widths >> 10 & 31))) * step);
                }
            }
        }
        return positions;
    }

    private static uint ReadBits(ReadOnlySpan<byte> data, ref int bit, int count)
    {
        uint value = 0;
        for (int component = 0; component < count; component++, bit++)
            value |= (uint)(data[bit / 8] >> (bit % 8) & 1) << component;
        return value;
    }
}
