using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MeshOptimizer;
using SharpGLTF.Schema2;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class GltfVertexStreamsTests
{
    [Fact]
    public async Task FlatNormalsKeepHardEdgesAndEverySourceStream()
    {
        using var file = new GltfFile();
        int position = file.AddFloats("VEC3", [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1], position: true);
        int uv = file.AddFloats("VEC2", [0, 0, 1, 0, 0, 1, .25f, .75f]);
        int color = file.AddFloats("VEC3", [.1f, .2f, .3f, .2f, .3f, .4f, .3f, .4f, .5f, .4f, .5f, .6f]);
        int joints = file.AddUnsignedShorts("VEC4", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
        int weights = file.AddFloats("VEC4", [.1f, .2f, .3f, .4f, .4f, .3f, .2f, .1f, .25f, .25f, .25f, .25f, 0, 0, 0, 1]);
        int custom = file.AddFloats("SCALAR", [10, 20, 30, 40]);
        int delta = file.AddFloats("VEC3", [0, 0, 1, 0, 0, 2, 0, 0, 3, 0, 0, 4], position: true);
        int colorDelta = file.AddFloats("VEC3", [.1f, 0, 0, .2f, 0, 0, .3f, 0, 0, .4f, 0, 0]);
        int topology = file.AddUnsignedShorts("SCALAR", [0, 1, 2, 0, 3, 1]);
        JsonObject primitive = file.CreatePrimitive(new()
        {
            ["POSITION"] = position,
            ["TEXCOORD_1"] = uv,
            ["TEXCOORD_7"] = uv,
            ["COLOR_0"] = color,
            ["COLOR_5"] = color,
            ["JOINTS_1"] = joints,
            ["WEIGHTS_1"] = weights,
            ["JOINTS_3"] = joints,
            ["WEIGHTS_3"] = weights,
            ["_TEMPERATURE"] = custom
        }, indices: topology);
        primitive["targets"] = new JsonArray(new JsonObject { ["POSITION"] = delta, ["COLOR_0"] = colorDelta },
            new JsonObject { ["POSITION"] = delta, ["_TEMPERATURE"] = custom });
        // Source weights are preserved, including a source that only supplies influence set one.
        await using ImportResult result = await ImportAsync(file.WriteFile(primitive));
        VertexPrimitive triangle = Assert.Single(result.Block.Primitives);
        Assert.Equal(new uint[] { 0, 1, 2, 0, 3, 1 }, triangle.SourceVertices.ToArray());
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, triangle.Indices.ToArray());
        Assert.Equal(new double[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 1, 0, 0, 1, 0 }, triangle.Attributes["NORMAL"].Values.ToArray());
        foreach (string name in new[] { "POSITION", "TEXCOORD_1", "TEXCOORD_7", "JOINTS_1", "WEIGHTS_1", "JOINTS_3", "WEIGHTS_3", "_TEMPERATURE" })
            AssertMapped(file, primitive, triangle, name);
        double[] colors = triangle.Attributes["COLOR_0"].Values.ToArray();
        Assert.Equal(4, triangle.Attributes["COLOR_0"].ComponentCount);
        for (int corner = 0; corner < 6; corner++)
        {
            int source = (int)triangle.SourceVertices.Span[corner];
            Assert.Equal((double)(.1f + source * .1f), colors[corner * 4], precision: 6);
            Assert.Equal(1, colors[corner * 4 + 3]);
            Assert.Equal(source + 1, triangle.MorphTargets[0]["POSITION"].Values.Span[corner * 3 + 2]);
            Assert.Equal(0, triangle.MorphTargets[0]["COLOR_0"].Values.Span[corner * 4 + 3]);
            Assert.Equal((source + 1) * 10, triangle.MorphTargets[1]["_TEMPERATURE"].Values.Span[corner]);
        }
        Assert.Equal(colors, triangle.Attributes["COLOR_5"].Values.ToArray());
        Assert.Empty(result.Block.Diagnostics);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    public async Task TriangleModesKeepSourceWindingAndGenerateSequentialIndices(int mode, bool indexed)
    {
        using var file = new GltfFile();
        int count = mode == 4 ? 3 : 4;
        int position = file.AddFloats("VEC3", new float[count * 3], position: true);
        int normal = file.AddFloats("VEC3", Enumerable.Range(0, count).SelectMany(_ => new float[] { 0, 0, 1 }).ToArray());
        JsonObject primitive = file.CreatePrimitive(new() { ["POSITION"] = position, ["NORMAL"] = normal }, mode,
            indexed ? file.AddUnsignedShorts("SCALAR", Enumerable.Range(0, count).Select(value => (ushort)value).ToArray()) : null);
        await using ImportResult result = await ImportAsync(file.WriteFile(primitive));
        uint[] expected = mode switch { 4 => [0, 1, 2], 5 => [0, 1, 2, 2, 1, 3], _ => [0, 1, 2, 0, 2, 3] };
        Assert.Equal(expected, Assert.Single(result.Block.Primitives).Indices.ToArray());
        Assert.Equal(count, result.Block.Primitives[0].Attributes["POSITION"].Count);
    }

    [Fact]
    public async Task ExistingNormalsAndTangentsKeepTheirValuesAndCoordinates()
    {
        using var file = new GltfFile();
        JsonObject primitive = file.CreateTriangle();
        int tangents = file.AddFloats("VEC4", [0, 1, 0, -1, 0, 1, 0, -1, 0, 1, 0, -1]);
        primitive["attributes"]!["TANGENT"] = tangents;
        primitive["material"] = 0;
        file.SetNormalMaterial(1);
        await using ImportResult result = await ImportAsync(file.WriteFile(primitive));
        VertexPrimitive triangle = Assert.Single(result.Block.Primitives);
        AssertMapped(file, primitive, triangle, "POSITION");
        AssertMapped(file, primitive, triangle, "NORMAL");
        AssertMapped(file, primitive, triangle, "TANGENT");
        Assert.Equal(0, triangle.Material);
        Assert.Empty(result.Block.Diagnostics);
    }

    [Fact]
    public async Task PointsAndLinesProduceDiagnosticsAndLeaveTriangleImportUsable()
    {
        using var file = new GltfFile();
        JsonObject triangle = file.CreateTriangle();
        JsonObject[] primitives = Enumerable.Range(0, 4).Select(mode =>
        {
            JsonObject primitive = (JsonObject)triangle.DeepClone(); primitive["mode"] = mode;
            primitive["indices"] = file.AddUnsignedShorts("SCALAR", mode == 1 ? [0, 1] : [0, 1, 2]);
            return primitive;
        }).Append(triangle).ToArray();
        await using ImportResult result = await ImportAsync(file.WriteFile(primitives));
        Assert.Equal(4, Assert.Single(result.Block.Primitives).SourcePrimitive);
        Assert.Equal(new[] { 0, 1, 2, 3 }, result.Block.Diagnostics.Select(value => value.Primitive));
        Assert.All(result.Block.Diagnostics, diagnostic => Assert.Equal(VertexImportDiagnosticKind.UnsupportedTopology, diagnostic.Kind));
        string saved = Path.Combine(file.Directory, "result.mesh.asset");
        await result.Asset.SaveAsync(saved);
        using MeshAsset reopened = await MeshAsset.OpenAsync(saved);
        Assert.Equal(1, reopened.GetBlock<MeshBlock>().Bounds!.Radius);
        Assert.Null(await reopened.TryGetBlockAsync<VertexStreamsBlock>());
    }

    [Fact]
    public async Task QuantizedAndSparseAccessorsExpandWithoutLosingIntegerPrecision()
    {
        using var file = new GltfFile();
        file.Document["extensionsUsed"] = new JsonArray("KHR_mesh_quantization");
        file.Document["extensionsRequired"] = new JsonArray("KHR_mesh_quantization");
        byte[] positions = new byte[24];
        BinaryPrimitives.WriteInt16LittleEndian(positions.AsSpan(8), 32767);
        BinaryPrimitives.WriteInt16LittleEndian(positions.AsSpan(18), -32768);
        int position = file.AddAccessor("VEC3", 5122, 3, file.AddView(positions, stride: 8), normalized: true);
        file.Accessors[position]!["min"] = new JsonArray(0, -32768, 0);
        file.Accessors[position]!["max"] = new JsonArray(32767, 0, 0);
        int normal = file.AddAccessor("VEC3", 5120, 3, file.AddView(new byte[] { 0, 0, 127, 0, 0, 0, 127, 0, 0, 0, 127, 0 }, stride: 4), normalized: true);
        int uv = file.AddAccessor("VEC2", 5126, 3, null);
        file.SetSparseValues(uv, [2], file.EncodeFloats([.25f, .75f]));
        int color = file.AddAccessor("VEC3", 5121, 3, file.AddView(new byte[] { 64, 128, 255, 0, 255, 0, 128, 0, 0, 255, 64, 0 }, stride: 4), normalized: true);
        int ids = file.AddAccessor("SCALAR", 5125, 3, file.AddView(file.EncodeUnsignedIntegers([16777217, uint.MaxValue, 7])));
        file.SetSparseValues(ids, [2], file.EncodeUnsignedIntegers([4294967294]));
        JsonObject primitive = file.CreatePrimitive(new() { ["POSITION"] = position, ["NORMAL"] = normal, ["TEXCOORD_1"] = uv, ["COLOR_0"] = color, ["_IDENTIFIER"] = ids });
        await using ImportResult result = await ImportAsync(file.WriteFile(primitive));
        VertexPrimitive triangle = Assert.Single(result.Block.Primitives);
        Assert.Equal(new double[] { 0, 0, 0, 1, 0, 0, 0, -1, 0 }, triangle.Attributes["POSITION"].Values.ToArray());
        Assert.Equal(new double[] { 0, 0, 0, 0, .25, .75 }, triangle.Attributes["TEXCOORD_1"].Values.ToArray());
        Assert.Equal(new double[] { 16777217, 4294967295, 4294967294 }, triangle.Attributes["_IDENTIFIER"].Values.ToArray());
        Assert.Equal((double)(64f / 255f), triangle.Attributes["COLOR_0"].Values.Span[0]);
        Assert.Equal((double)(128f / 255f), triangle.Attributes["COLOR_0"].Values.Span[1]);
        Assert.Equal(1, triangle.Attributes["COLOR_0"].Values.Span[3]);
        Assert.Empty(result.Block.Diagnostics);
    }

    [Fact]
    public async Task SparseTriangleIndicesAndMatrixAttributesKeepTheirSourceValues()
    {
        using var file = new GltfFile();
        JsonObject primitive = file.CreateTriangle();
        int indices = file.AddAccessor("SCALAR", 5123, 3, null);
        file.SetSparseValues(indices, [1, 2], new byte[] { 1, 0, 2, 0 });
        primitive["indices"] = indices;
        byte[] matrices = new byte[36];
        for (int vertex = 0; vertex < 3; vertex++)
            for (int component = 0; component < 9; component++) matrices[vertex * 12 + (component / 3) * 4 + component % 3] = (byte)(vertex * 9 + component);
        primitive["attributes"]!["_BASIS"] = file.AddAccessor("MAT3", 5121, 3, file.AddView(matrices));
        await using ImportResult result = await ImportAsync(file.WriteFile(primitive));
        VertexPrimitive triangle = Assert.Single(result.Block.Primitives);
        Assert.Equal(new uint[] { 0, 1, 2 }, triangle.Indices.ToArray());
        Assert.Equal(Enumerable.Range(0, 27).Select(value => (double)value), triangle.Attributes["_BASIS"].Values.ToArray());
    }

    [Theory]
    [InlineData("mirrored-uv")]
    [InlineData("smooth-skew")]
    [InlineData("degenerate-uv")]
    public async Task TangentsMatchMikkReferenceUsingTheNormalTextureCoordinateSet(string name)
    {
        JsonNode references = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Content", "Gltf", "mikk-reference.json")))!;
        JsonNode example = references["cases"]!.AsArray().Single(value => value!["name"]!.GetValue<string>() == name)!;
        using var file = new GltfFile();
        float[] ReadValues(string key) => example[key]!.AsArray().Select(value => value!.GetValue<float>()).ToArray();
        int position = file.AddFloats("VEC3", ReadValues("positions"), position: true);
        int normal = file.AddFloats("VEC3", ReadValues("normals"));
        int uv0 = file.AddFloats("VEC2", new float[ReadValues("uv").Length]);
        int uv1 = file.AddFloats("VEC2", ReadValues("uv"));
        ushort[] indices = example["indices"]!.AsArray().Select(value => value!.GetValue<ushort>()).ToArray();
        JsonObject primitive = file.CreatePrimitive(new() { ["POSITION"] = position, ["NORMAL"] = normal, ["TEXCOORD_0"] = uv0, ["TEXCOORD_1"] = uv1 }, indices: file.AddUnsignedShorts("SCALAR", indices));
        primitive["material"] = 0;
        int delta = file.AddFloats("VEC3", ReadValues("positions"), position: true);
        primitive["targets"] = new JsonArray(new JsonObject { ["POSITION"] = delta });
        file.SetNormalMaterial(1);
        await using ImportResult result = await ImportAsync(file.WriteFile(primitive));
        VertexPrimitive triangle = Assert.Single(result.Block.Primitives);
        double[] tangents = triangle.Attributes["TANGENT"].Values.ToArray();
        float[] expected = ReadValues("tangents");
        Assert.Equal(expected.Length, tangents.Length);
        for (int component = 0; component < tangents.Length; component++)
            Assert.InRange(Math.Abs(expected[component] - tangents[component]), 0, 1e-6);
        Assert.Equal(indices.Select(value => (uint)value), triangle.SourceVertices.ToArray());
        AssertMapped(file, primitive, triangle, "NORMAL");
        AssertMapped(file, primitive, triangle, "TEXCOORD_1");
        Assert.Equal(triangle.Attributes["POSITION"].Values.ToArray(), triangle.MorphTargets[0]["POSITION"].Values.ToArray());
        Assert.Empty(result.Block.Diagnostics);
    }

    [Fact]
    public async Task FlatNormalsReplaceTheTangentBasisAndRetainSourceTangents()
    {
        using var file = new GltfFile();
        JsonObject primitive = file.CreateTriangle();
        primitive["attributes"]!.AsObject().Remove("NORMAL");
        primitive["attributes"]!["TANGENT"] = file.AddFloats("VEC4", [0, 1, 0, -1, 0, 1, 0, -1, 0, 1, 0, -1]);
        primitive["attributes"]!["TEXCOORD_0"] = file.AddFloats("VEC2", [0, 0, 1, 0, 0, 1]);
        primitive["material"] = 0;
        file.SetNormalMaterial(0);
        await using ImportResult result = await ImportAsync(file.WriteFile(primitive));
        VertexPrimitive triangle = Assert.Single(result.Block.Primitives);
        Assert.Equal(new double[] { 0, 1, 0, -1, 0, 1, 0, -1, 0, 1, 0, -1 }, triangle.SourceTangents!.Values.ToArray());
        Assert.Equal(new double[] { 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1 }, triangle.Attributes["TANGENT"].Values.ToArray());
    }

    [Fact]
    public async Task MissingNormalTextureCoordinatesProduceAnActionableDiagnostic()
    {
        using var file = new GltfFile();
        JsonObject primitive = file.CreateTriangle(); primitive["material"] = 0; file.SetNormalMaterial(2);
        await using ImportResult result = await ImportAsync(file.WriteFile(primitive));
        Assert.Single(result.Block.Primitives);
        VertexImportDiagnostic diagnostic = Assert.Single(result.Block.Diagnostics);
        Assert.Equal(VertexImportDiagnosticKind.MissingTextureCoordinates, diagnostic.Kind);
    }

    [Theory]
    [InlineData(false, "ATTRIBUTES")]
    [InlineData(true, "ATTRIBUTES")]
    [InlineData(false, "TRIANGLES")]
    [InlineData(true, "INDICES")]
    [InlineData(false, "EXPONENTIAL")]
    [InlineData(true, "KHR")]
    public async Task MeshoptBuffersDecodeThroughTheSourceLoadingApi(bool binary, string mode)
    {
        using var file = new GltfFile();
        JsonObject primitive = file.CreateTriangle();
        if (mode is "ATTRIBUTES" or "EXPONENTIAL" or "KHR")
        {
            byte[] bytes = file.EncodeFloats([0, 0, 0, 1, 0, 0, 0, 1, 0]);
            if (mode == "EXPONENTIAL") bytes = EncodeExponentialPositions();
            int view = file.AddCompressedView(Compress(bytes, 3, 12, "ATTRIBUTES"), 3, 12, "ATTRIBUTES",
                mode == "EXPONENTIAL" ? "EXPONENTIAL" : "NONE", mode == "KHR" ? "KHR_meshopt_compression" : "EXT_meshopt_compression");
            primitive["attributes"]!["POSITION"] = file.AddAccessor("VEC3", 5126, 3, view);
            file.Accessors[file.Accessors.Count - 1]!["min"] = new JsonArray(0, 0, 0);
            file.Accessors[file.Accessors.Count - 1]!["max"] = new JsonArray(1, 1, 0);
        }
        else
        {
            int view = file.AddCompressedView(Compress(file.EncodeUnsignedIntegers([0, 1, 2]), 3, 4, mode), 3, 4, mode);
            primitive["indices"] = file.AddAccessor("SCALAR", 5125, 3, view);
        }
        await using ImportResult result = await ImportAsync(file.WriteFile([primitive], binary));
        VertexPrimitive triangle = Assert.Single(result.Block.Primitives);
        Assert.Equal(new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, triangle.Attributes["POSITION"].Values.ToArray());
        Assert.Equal(new uint[] { 0, 1, 2 }, triangle.Indices.ToArray());
        Assert.Empty(result.Block.Diagnostics);
    }

    [Theory]
    [InlineData("Draco")]
    [InlineData("meshopt-mode")]
    [InlineData("meshopt-filter")]
    [InlineData("meshopt-corrupt")]
    public async Task UnsupportedOrCorruptCompressionSkipsOnlyItsPrimitive(string failure)
    {
        using var file = new GltfFile();
        JsonObject triangle = file.CreateTriangle(), compressed = (JsonObject)triangle.DeepClone();
        if (failure == "Draco")
        {
            compressed["extensions"] = new JsonObject
            {
                ["KHR_draco_mesh_compression"] = new JsonObject
                { ["bufferView"] = file.AddView([1, 2, 3, 4]), ["attributes"] = new JsonObject { ["POSITION"] = 0 } }
            };
            file.Document["extensionsUsed"] = new JsonArray("KHR_draco_mesh_compression");
            file.Document["extensionsRequired"] = new JsonArray("KHR_draco_mesh_compression");
        }
        else
        {
            int view = file.AddCompressedView(failure == "meshopt-corrupt" ? [1, 2, 3, 4] : Compress(new byte[36], 3, 12, "ATTRIBUTES"),
                3, 12, failure == "meshopt-mode" ? "OTHER" : "ATTRIBUTES", failure == "meshopt-filter" ? "OTHER" : "NONE");
            compressed["attributes"]!["POSITION"] = file.AddAccessor("VEC3", 5126, 3, view);
        }
        await using ImportResult result = await ImportAsync(file.WriteFile(compressed, triangle));
        Assert.Equal(1, Assert.Single(result.Block.Primitives).SourcePrimitive);
        VertexImportDiagnostic diagnostic = Assert.Single(result.Block.Diagnostics);
        Assert.Equal(0, diagnostic.Primitive);
        Assert.Equal(failure == "meshopt-corrupt" ? VertexImportDiagnosticKind.InvalidPrimitive : VertexImportDiagnosticKind.UnsupportedCompression, diagnostic.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MorphTargetsReceiveFlatNormalsAndTangentsForTheirUpdatedVertices(bool sourceTangents)
    {
        using var file = new GltfFile();
        int positions = file.AddFloats("VEC3", [0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0], position: true);
        int uv = file.AddFloats("VEC2", [0, 0, 1, 0, 0, 1, 1, 1]);
        int delta = file.AddFloats("VEC3", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1], position: true);
        JsonObject primitive = file.CreatePrimitive(new() { ["POSITION"] = positions, ["TEXCOORD_0"] = uv },
            indices: file.AddUnsignedShorts("SCALAR", [0, 1, 2, 2, 1, 3]));
        JsonObject target = new() { ["POSITION"] = delta };
        if (sourceTangents)
        {
            primitive["attributes"]!["TANGENT"] = file.AddFloats("VEC4", Enumerable.Range(0, 4).SelectMany(_ => new float[] { 0, 1, 0, -1 }).ToArray());
            target["TANGENT"] = file.AddFloats("VEC3", Enumerable.Range(0, 4).SelectMany(_ => new float[] { .1f, .2f, .3f }).ToArray());
        }
        primitive["targets"] = new JsonArray(target);
        primitive["material"] = 0;
        file.SetNormalMaterial(0);
        await using ImportResult result = await ImportAsync(file.WriteFile(primitive));
        VertexPrimitive triangle = Assert.Single(result.Block.Primitives);
        VertexStream normals = triangle.MorphTargets[0]["NORMAL"], tangents = triangle.MorphTargets[0]["TANGENT"];
        float normal = 1 / MathF.Sqrt(3), tangent = 1 / MathF.Sqrt(2);
        for (int corner = 0; corner < 6; corner++)
        {
            double[] expectedNormal = corner < 3 ? [0, 0, 0] : [-normal, -normal, normal - 1];
            double[] expectedTangent = corner < 3 ? [0, 0, 0] : [tangent - 1, 0, tangent];
            for (int component = 0; component < 3; component++)
            {
                Assert.InRange(Math.Abs(expectedNormal[component] - normals.Values.Span[corner * 3 + component]), 0, 1e-6);
                Assert.InRange(Math.Abs(expectedTangent[component] - tangents.Values.Span[corner * 3 + component]), 0, 1e-6);
            }
            Assert.Equal(corner == 5 ? 1 : 0, triangle.MorphTargets[0]["POSITION"].Values.Span[corner * 3 + 2]);
            if (sourceTangents)
            {
                Assert.Equal(-1, triangle.SourceTangents!.Values.Span[corner * 4 + 3]);
                Assert.Equal((double).1f, triangle.SourceMorphTangents[0]!.Values.Span[corner * 3]);
                Assert.Equal((double).2f, triangle.SourceMorphTangents[0]!.Values.Span[corner * 3 + 1]);
                Assert.Equal((double).3f, triangle.SourceMorphTangents[0]!.Values.Span[corner * 3 + 2]);
            }
        }
        Assert.Empty(result.Block.Diagnostics);
    }

    [Fact]
    public void ReplacementImportersCanCreateVertexStreamsFromCallerData()
    {
        double[] values = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        uint[] indices = [0, 1, 2];
        uint[] mapping = [0, 1, 2];
        var attributes = new Dictionary<string, VertexStream> { ["POSITION"] = new(3, values.AsSpan()) };
        var primitive = new VertexPrimitive(0, -1, indices.AsSpan(), mapping.AsSpan(), attributes, []);
        var primitives = new List<VertexPrimitive> { primitive };
        using var block = new VertexStreamsBlock(primitives, []);
        values[0] = 9;
        indices[0] = 2;
        mapping[0] = 2;
        attributes.Clear();
        primitives.Clear();
        VertexPrimitive result = Assert.Single(block.Primitives);
        Assert.Equal(new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, result.Attributes["POSITION"].Values.ToArray());
        Assert.Equal(new uint[] { 0, 1, 2 }, result.Indices.ToArray());
        Assert.Equal(new uint[] { 0, 1, 2 }, result.SourceVertices.ToArray());
    }

    private static void AssertMapped(GltfFile file, JsonObject source, VertexPrimitive result, string name)
    {
        int accessor = source["attributes"]![name]!.GetValue<int>();
        double[] original = file.AccessorValues[accessor];
        VertexStream stream = result.Attributes[name];
        for (int vertex = 0; vertex < stream.Count; vertex++)
            for (int component = 0; component < stream.ComponentCount; component++)
                Assert.Equal(original[(int)result.SourceVertices.Span[vertex] * stream.ComponentCount + component], stream.Values.Span[vertex * stream.ComponentCount + component]);
    }

    [Fact]
    public async Task BoundsFromPrimitivesSurviveReopenAndGeometryConsumption()
    {
        using var file = new GltfFile();
        float[] CreatePositions(Vector3 origin)
        {
            List<float> values = [];
            for (int triangle = 0; triangle < 2048; triangle++)
            {
                Vector3 position = origin + new Vector3(triangle % 32 * 2, triangle / 32 * 2, 0);
                values.AddRange([position.X, position.Y, position.Z,
                    position.X + 1, position.Y, position.Z, position.X, position.Y + 1, position.Z]);
            }
            return values.ToArray();
        }
        JsonObject first = file.CreatePrimitive(new()
        { ["POSITION"] = file.AddFloats("VEC3", CreatePositions(new(-10, -20, 0)), position: true) });
        JsonObject second = file.CreatePrimitive(new()
        { ["POSITION"] = file.AddFloats("VEC3", CreatePositions(new(20, 0, 5)), position: true) });
        await using ImportResult result = await ImportAsync(file.WriteFile(first, second), renderable: true);
        string path = Path.Combine(file.Directory, "bounds.mesh.asset");
        await result.Asset.SaveAsync(path);
        using MeshAsset reopened = await MeshAsset.OpenAsync(path);
        MeshBlock shape = reopened.GetBlock<MeshBlock>();
        Assert.Equal(36.5f, shape.Bounds!.Center.X);
        Assert.Equal(53.5f, shape.Bounds.Center.Y);
        Assert.Equal(2.5f, shape.Bounds.Center.Z);
        Assert.Equal(73.5f, shape.Bounds.Radius);
        Assert.Equal([0, 1], shape.RegionSources.Select(region => region.PrimitiveIndex));
        Assert.Equal(["region_0", "region_1"], shape.Regions.Select(region => region.Name));
        ClusterGeometryBlock geometry = await reopened.GetBlockAsync<ClusterGeometryBlock>();
        ClusterVertexAttributesBlock attributes = await reopened.GetBlockAsync<ClusterVertexAttributesBlock>();
        var mesh = await geometry.CreateAsync(shape, attributes, "bounds grid");
        Assert.Equal(shape.Bounds, mesh.Bounds);
        Assert.Equal(shape.Regions.ToArray(), mesh.Regions.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompressedPositionsSetSemanticAndRenderedBounds(bool binary)
    {
        using var file = new GltfFile();
        JsonObject primitive = file.CreateTriangle();
        byte[] positions = file.EncodeFloats([10,0,0, 11,0,0, 10,1,0]);
        int view = file.AddCompressedView(Compress(positions, 3, 12, "ATTRIBUTES"), 3, 12, "ATTRIBUTES");
        int accessor = file.AddAccessor("VEC3", 5126, 3, view);
        file.Accessors[accessor]!["min"] = new JsonArray(10,0,0);
        file.Accessors[accessor]!["max"] = new JsonArray(11,1,0);
        primitive["attributes"]!["POSITION"] = accessor;
        await using ImportResult result = await ImportAsync(file.WriteFile([primitive], binary), true);
        MeshBlock shape = result.Asset.GetBlock<MeshBlock>();
        Assert.Equal(10.5f, shape.Bounds!.Center.X);
        Assert.Equal(.5f, shape.Bounds.Center.Y);
        Assert.Equal(.5f, shape.Bounds.Radius);
        ClusterGeometryBlock geometry = result.Asset.GetBlock<ClusterGeometryBlock>();
        var rendered = await geometry.CreateAsync(shape, result.Asset.GetBlock<ClusterVertexAttributesBlock>(), "compressed bounds");
        Assert.Equal(shape.Bounds, rendered.Bounds);
        Assert.Contains(geometry.Clusters.SelectMany(cluster => Enumerable.Range(0, cluster.Vertices.Length).Select(cluster.DecodePosition)),
            position => Vector3.Distance(position, new(10,0,0)) < .001f);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task SkippedPointAndLineObjectsLeaveDrawableSceneNodes(int mode, bool mixed)
    {
        using var file = new GltfFile();
        JsonObject skipped = file.CreateTriangle();
        skipped["mode"] = mode;
        JsonObject[] primitives = mixed ? [skipped, file.CreateTriangle()] : [skipped];
        file.Document["nodes"] = new JsonArray(new JsonObject { ["mesh"] = 0 });
        file.Document["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) });
        file.Document["scene"] = 0;
        string path = file.WriteFile(primitives);
        await using ImportResult result = await ImportAsync(path, true);
        Assert.Contains(result.Block.Diagnostics, diagnostic => diagnostic.Kind == VertexImportDiagnosticKind.UnsupportedTopology);
        ClusterGeometryBlock? geometry = await result.Asset.TryGetBlockAsync<ClusterGeometryBlock>();
        Assert.Equal(mixed, geometry is not null);
        Assert.Equal(mixed, await result.Asset.TryGetBlockAsync<ClusterVertexAttributesBlock>() is not null);
        SomeEngine.Assets.WorldAsset sceneAsset = await result.Imports.GetAssetAsync<SharpGLTF.Schema2.Scene, SomeEngine.Assets.WorldAsset>(result.Model.LogicalScenes[0]);
        using var world = await sceneAsset.LoadAsync(SomeEngine.Render.SceneSerialization.CreateRegistry());
        var query = world.Query(new SomeEngine.ECS.Queries.QueryDefinitionBuilder().Read<SomeEngine.Render.Components.MeshSource>());
        int count = 0;
        world.ExecuteQuery(query, rows => { foreach (var row in rows.Rows) count++; });
        world.ReleaseQuery(query);
        Assert.Equal(mixed ? 1 : 0, count);
    }

    private sealed class MeshSceneImporter : GltfSceneImporter
    {
        public override ValueTask ImportMaterialAsync(SharpGLTF.Schema2.Material source, ImportContext context,
            ImportOutput output, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task DrawableSceneMeshKeepsItsReferenceWithOptionalBounds()
    {
        using var file = new GltfFile();
        file.Document["nodes"] = new JsonArray(new JsonObject { ["mesh"] = 0 });
        file.Document["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) });
        file.Document["scene"] = 0;
        await using ImportResult result = await ImportAsync(file.WriteFile(file.CreateTriangle()), true, true);
        Assert.Null(result.Asset.GetBlock<MeshBlock>().Bounds);
        var mesh = await result.Asset.GetBlock<ClusterGeometryBlock>().CreateAsync(result.Asset.GetBlock<MeshBlock>(),
            result.Asset.GetBlock<ClusterVertexAttributesBlock>(), "optional bounds");
        Assert.Equal(1, mesh.PageCount);
        string meshPath = Path.Combine(file.Directory, "optional.mesh.asset");
        await result.Asset.SaveAsync(meshPath);
        using MeshAsset reopenedMesh = await MeshAsset.OpenAsync(meshPath);
        Assert.Null(reopenedMesh.GetBlock<MeshBlock>().Bounds);
        var reopenedGeometry = await reopenedMesh.GetBlock<ClusterGeometryBlock>().CreateAsync(reopenedMesh.GetBlock<MeshBlock>(),
            await reopenedMesh.GetBlockAsync<ClusterVertexAttributesBlock>(), "reopened optional bounds");
        Assert.Equal(1, reopenedGeometry.PageCount);
        WorldAsset scene = await result.Imports.GetAssetAsync<SharpGLTF.Schema2.Scene, WorldAsset>(result.Model.LogicalScenes[0]);
        string scenePath = Path.Combine(file.Directory, "optional.scene.asset");
        await scene.SaveAsync(scenePath);
        using WorldAsset reopened = await WorldAsset.OpenAsync(scenePath);
        foreach (WorldAsset asset in new[] { scene, reopened })
        {
            using var world = await asset.LoadAsync(SomeEngine.Render.SceneSerialization.CreateRegistry());
            var query = world.Query(new SomeEngine.ECS.Queries.QueryDefinitionBuilder().Read<SomeEngine.Render.Components.MeshSource>());
            var references = new List<AssetId<MeshAsset>>();
            world.ExecuteQuery(query, rows =>
            {
                foreach (var row in rows.Rows) references.Add(world.Read<SomeEngine.Render.Components.MeshSource>(row.Entity).Mesh);
            });
            world.ReleaseQuery(query);
            Assert.Equal(result.Asset.Reference, Assert.Single(references));
        }
    }

    private static async Task<ImportResult> ImportAsync(string path, bool renderable = false, bool optionalBounds = false)
    {
        ModelRoot model = await new GltfSourceImporter(new ServiceCollection(), "vertex-tests").ReadAsync(path, CancellationToken.None);
        var context = new ImportContext();
        try
        {
            if (renderable)
            {
                context.EnableImporter<SharpGLTF.Schema2.Scene, SomeEngine.Assets.WorldAsset>(Guid.NewGuid(), [SomeEngine.Assets.WorldBlock.BlockTypeId],
                    (scene, imports, output, token) => new MeshSceneImporter().ImportAsync(scene, new(), imports, output, token));

                context.EnableImporter<SharpGLTF.Schema2.Mesh, MeshAsset>(Guid.NewGuid(), [MeshBlock.BlockTypeId],
                    (source, imports, output, token) =>
                    {
                        if (!optionalBounds) return new GltfMeshImporter().ImportAsync(source, new(), imports, output, token);
                        output.SetBlock(new MeshBlock { Regions = [new() { Name = "surface" }] });
                        return ValueTask.CompletedTask;
                    });
                context.EnableImporter<SharpGLTF.Schema2.Mesh, MeshAsset>(Guid.NewGuid(), [ClusterGeometryBlock.BlockTypeId],
                    (source, imports, output, token) => new ClusterGeometryImporter().ImportAsync(source, new(), imports, output, token));
                context.EnableImporter<SharpGLTF.Schema2.Mesh, MeshAsset>(Guid.NewGuid(), [ClusterVertexAttributesBlock.BlockTypeId],
                    (source, imports, output, token) => new ClusterVertexAttributesImporter().ImportAsync(source, new(), imports, output, token));
            }
            else
            {
                context.EnableImporter<SharpGLTF.Schema2.Mesh, MeshAsset>(Guid.NewGuid(), [MeshBlock.BlockTypeId],
                    async (_, imports, output, token) =>
                    {
                        VertexStreamsBlock streams = await imports.GetBlockAsync<VertexStreamsBlock>(output.Target, token);
                        output.SetBlock(new MeshBlock { Bounds = new() { Radius = streams.Primitives.Count } });
                    });
            }
            var services = new ServiceCollection();
            EngineAssetCapabilities.AddAssetCapabilities(services);
            ServiceProvider provider = services.BuildServiceProvider();
            context.Hold(provider);
            AssetImporterCapability capability = provider.GetServices<AssetImporterCapability>()
                .Single(value => value.ImporterType == typeof(GltfVertexStreamsImporter));
            context.EnableImporter<SharpGLTF.Schema2.Mesh, MeshAsset>(capability.Bind<SharpGLTF.Schema2.Mesh>(provider));
            foreach (SharpGLTF.Schema2.Mesh mesh in model.LogicalMeshes)
            {
                string key = $"mesh/{mesh.LogicalIndex}";
                Guid id = context.GetAssetId(key);
                context.AddAsset(mesh, new MeshAsset(id), key, id + ".mesh.asset");
            }
            if (renderable)
            {
                foreach (SharpGLTF.Schema2.Scene scene in model.LogicalScenes)
                {
                    string key = $"scene/{scene.LogicalIndex}";
                    Guid id = context.GetAssetId(key);
                    context.AddAsset(scene, new SomeEngine.Assets.WorldAsset(id), key, id + ".scene.asset");
                }
            }
            MeshAsset asset = await context.GetAssetAsync<SharpGLTF.Schema2.Mesh, MeshAsset>(model.LogicalMeshes[0]);
            return new(context, asset, model);
        }
        catch { await context.DisposeAsync(); throw; }
    }

    private sealed class ImportResult(ImportContext context, MeshAsset asset, ModelRoot model) : IAsyncDisposable
    {
        public ImportContext Imports => context;
        public ModelRoot Model => model;
        public MeshAsset Asset { get; } = asset;
        public VertexStreamsBlock Block => Asset.GetBlock<VertexStreamsBlock>();
        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private static unsafe byte[] Compress(byte[] data, int count, int stride, string mode)
    {
        byte[] compressed = new byte[1024];
        nuint length;
        fixed (byte* source = data)
        fixed (byte* destination = compressed)
            length = mode switch
            {
                "ATTRIBUTES" => Meshopt.EncodeVertexBuffer(destination, (nuint)compressed.Length, source, (nuint)count, (nuint)stride),
                "TRIANGLES" => Meshopt.EncodeIndexBuffer(destination, (nuint)compressed.Length, (uint*)source, (nuint)count),
                _ => Meshopt.EncodeIndexSequence(destination, (nuint)compressed.Length, (uint*)source, (nuint)count),
            };
        return compressed.AsSpan(0, (int)length).ToArray();
    }

    private static unsafe byte[] EncodeExponentialPositions()
    {
        byte[] bytes = new byte[36];
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        fixed (byte* destination = bytes)
        fixed (float* source = positions)
            Meshopt.EncodeFilterExp(destination, 3, 12, 24, source, EncodeExpMode.EncodeExpSharedComponent);
        return bytes;
    }

    private sealed class GltfFile : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "gltf-streams-" + Guid.NewGuid().ToString("N"));
        public JsonObject Document { get; } = new() { ["asset"] = new JsonObject { ["version"] = "2.0" } };
        public JsonArray Accessors { get; } = [];
        private readonly JsonArray _views = [];
        private readonly MemoryStream _bytes = new();
        public Dictionary<int, double[]> AccessorValues { get; } = [];
        private int _fallbackBytes;
        public GltfFile() => System.IO.Directory.CreateDirectory(Directory);
        public int AddView(byte[] bytes, int? stride = null)
        {
            while (_bytes.Length % 4 != 0) _bytes.WriteByte(0);
            var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = _bytes.Length, ["byteLength"] = bytes.Length };
            if (stride.HasValue) view["byteStride"] = stride.Value;
            _views.Add(view);
            _bytes.Write(bytes);
            return _views.Count - 1;
        }
        public int AddAccessor(string shape, int encoding, int count, int? view, bool normalized = false)
        {
            var accessor = new JsonObject { ["type"] = shape, ["componentType"] = encoding, ["count"] = count };
            if (view.HasValue) accessor["bufferView"] = view.Value;
            if (normalized) accessor["normalized"] = true;
            Accessors.Add(accessor);
            return Accessors.Count - 1;
        }
        public int AddFloats(string shape, float[] values, bool position = false)
        {
            int components = shape switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, _ => 4 };
            int index = AddAccessor(shape, 5126, values.Length / components, AddView(EncodeFloats(values)));
            AccessorValues.Add(index, values.Select(value => (double)value).ToArray());
            if (position)
            {
                float[] low = new float[components], high = new float[components];
                for (int component = 0; component < components; component++)
                {
                    float[] channel = values.Where((_, index) => index % components == component).ToArray();
                    low[component] = channel.Min();
                    high[component] = channel.Max();
                }
                Accessors[index]!["min"] = new JsonArray(low.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                Accessors[index]!["max"] = new JsonArray(high.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            }
            return index;
        }
        public int AddUnsignedShorts(string shape, ushort[] values)
        {
            byte[] bytes = new byte[values.Length * 2];
            for (int index = 0; index < values.Length; index++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * 2), values[index]);
            int result = AddAccessor(shape, 5123, values.Length / (shape == "SCALAR" ? 1 : 4), AddView(bytes));
            AccessorValues.Add(result, values.Select(value => (double)value).ToArray());
            return result;
        }
        public byte[] EncodeFloats(float[] values)
        {
            byte[] bytes = new byte[values.Length * 4];
            for (int index = 0; index < values.Length; index++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * 4), values[index]);
            return bytes;
        }
        public byte[] EncodeUnsignedIntegers(uint[] values)
        {
            byte[] bytes = new byte[values.Length * 4];
            for (int index = 0; index < values.Length; index++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), values[index]);
            return bytes;
        }
        public void SetSparseValues(int accessor, byte[] indices, byte[] values)
            => Accessors[accessor]!["sparse"] = new JsonObject
            {
                ["count"] = indices.Length,
                ["indices"] = new JsonObject { ["bufferView"] = AddView(indices), ["componentType"] = 5121 },
                ["values"] = new JsonObject { ["bufferView"] = AddView(values) }
            };
        public JsonObject CreatePrimitive(Dictionary<string, int> attributes, int mode = 4, int? indices = null)
        {
            var values = attributes.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value)));
            var primitive = new JsonObject { ["attributes"] = new JsonObject(values), ["mode"] = mode };
            if (indices.HasValue) primitive["indices"] = indices.Value;
            return primitive;
        }
        public JsonObject CreateTriangle() => CreatePrimitive(new()
        {
            ["POSITION"] = AddFloats("VEC3", [0, 0, 0, 1, 0, 0, 0, 1, 0], position: true),
            ["NORMAL"] = AddFloats("VEC3", [0, 0, 1, 0, 0, 1, 0, 0, 1])
        });
        public void SetNormalMaterial(int coordinates)
        {
            Document["images"] = new JsonArray(new JsonObject { ["uri"] = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a4x8AAAAASUVORK5CYII=" });
            Document["textures"] = new JsonArray(new JsonObject { ["source"] = 0 });
            Document["materials"] = new JsonArray(new JsonObject { ["normalTexture"] = new JsonObject { ["index"] = 0, ["texCoord"] = coordinates } });
        }
        public int AddCompressedView(byte[] bytes, int count, int stride, string mode, string filter = "NONE", string extension = "EXT_meshopt_compression")
        {
            int input = AddView(bytes);
            JsonObject compressed = new()
            {
                ["buffer"] = 0,
                ["byteOffset"] = _views[input]!["byteOffset"]!.DeepClone(),
                ["byteLength"] = bytes.Length,
                ["count"] = count,
                ["byteStride"] = stride,
                ["mode"] = mode,
                ["filter"] = filter
            };
            int length = count * stride;
            var view = new JsonObject
            {
                ["buffer"] = 1,
                ["byteOffset"] = _fallbackBytes,
                ["byteLength"] = length,
                ["extensions"] = new JsonObject { [extension] = compressed }
            };
            if (mode == "ATTRIBUTES") view["byteStride"] = stride;
            _fallbackBytes += length;
            _views.Add(view);
            Document["extensionsUsed"] = new JsonArray(extension);
            Document["extensionsRequired"] = new JsonArray(extension);
            return _views.Count - 1;
        }
        public string WriteFile(params JsonObject[] primitives) => WriteFile(primitives, false);
        public string WriteFile(JsonObject[] primitives, bool binary)
        {
            while (_bytes.Length % 4 != 0) _bytes.WriteByte(0);
            byte[] data = _bytes.ToArray();
            Document["accessors"] = Accessors;
            Document["bufferViews"] = _views;
            Document["meshes"] = new JsonArray(new JsonObject { ["primitives"] = new JsonArray(primitives.Cast<JsonNode?>().ToArray()) });
            JsonObject buffer = new() { ["byteLength"] = data.Length };
            if (!binary) buffer["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(data);
            var buffers = new JsonArray(buffer);
            if (_fallbackBytes > 0) buffers.Add(new JsonObject { ["byteLength"] = _fallbackBytes });
            Document["buffers"] = buffers;
            string path = Path.Combine(Directory, binary ? "mesh.glb" : "mesh.gltf");
            if (!binary) File.WriteAllText(path, Document.ToJsonString());
            else
            {
                byte[] json = Encoding.UTF8.GetBytes(Document.ToJsonString());
                int padded = (json.Length + 3) & ~3;
                using var writer = new BinaryWriter(File.Create(path));
                writer.Write(0x46546c67u);
                writer.Write(2u);
                writer.Write(28 + padded + data.Length);
                writer.Write(padded);
                writer.Write(0x4e4f534au);
                writer.Write(json);
                for (int index = json.Length; index < padded; index++) writer.Write((byte)' ');
                writer.Write(data.Length);
                writer.Write(0x004e4942u);
                writer.Write(data);
            }
            return path;
        }
        public void Dispose()
        {
            _bytes.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
