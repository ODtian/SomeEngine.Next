using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Resources;
using GeometryValueType = SomeEngine.Resources.Geometry.ValueType;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class SoftwareRasterLaneRenderingTests
{
    [Theory]
    [InlineData("D3D12", "MaterialRanges")]
    [InlineData("D3D12", "VertexBatches")]
    [InlineData("Vulkan", "MaterialRanges")]
    [InlineData("Vulkan", "VertexBatches")]
    public async Task ReassigningTriangleThreadsPreservesSoftwareRasterColorAndDepth(string backend, string work)
    {
        string root = FrameStageScene.FindRoot();
        string source = File.ReadAllText(Path.Combine(root, "templates/DefaultProject/assets/Shaders/unlit.slang"))
            .Replace("typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;",
                VisibilitySource + "\ntypedef TextureVisibility VisibilityEvaluate;");
        if (work == "VertexBatches")
            source = source.Replace("#define CLUSTER_SOFTWARE_MESHLET_RASTER 1", "");
        string entries = File.ReadAllText(Path.Combine(root, "assets/Shaders/cluster_material_entries.slang"));
        Assert.Contains("#include \"cluster_material_entries.slang\"", source);
        Assert.Contains("struct TextureVisibility", source);
        Assert.Contains("groupID, groupThreadIndex", entries);
        Assert.Equal(work == "MaterialRanges", source.Contains("#define CLUSTER_SOFTWARE_MESHLET_RASTER 1"));
        Shader ordered = CreateShader(backend, "OrderedTriangleThreads",
            source.Replace("#include \"cluster_material_entries.slang\"", entries));
        // This is a bijection of the same 32 triangle assignments. Vertex values read with
        // WaveReadLaneAt must follow wave lane IDs, regardless of each thread's assignment.
        Shader rotated = CreateShader(backend, "RotatedTriangleThreads",
            source.Replace("#include \"cluster_material_entries.slang\"",
                entries.Replace("groupID, groupThreadIndex", "groupID, ((groupThreadIndex + 1u) & 31u)")));
        Mesh mesh = await CreateGridAsync();
        var options = new ClusterPipelineOptions { EnableFrameMetricsReadback = true,
            ForceHardwareRaster = false, SoftwareRasterEdgeThreshold = 4096, MaxCandidates = 65536 };
        await using var scene = await FrameStageScene.CreateAsync(backend, false, options, mesh);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100);
        projection.M11 = projection.M22 = 1;
        scene.SetView(new RenderView(Matrix4x4.Identity, projection, 64, 64));
        scene.SetMeshTransform(0, new(new Vector3(0, 0, -4), Quaternion.Identity)
            { Stretch = new Vector3(2, 2, 1) });
        scene.SetMeshTransform(1, new(new Vector3(20, 0, -6), Quaternion.Identity));
        scene.UseMaterial(0, scene.CreateRasterMaterial(ordered, new Vector4(.75f, .5f, .25f, 1)));
        RasterImage expected = await scene.ReadNextRasterImageAsync();
        Console.WriteLine($"{work} ordered: {expected.Metrics}");
        Assert.True(expected.Metrics.SoftwareRasterBatches > 0);
        Assert.True(MemoryMarshal.Cast<byte, Half>(expected.Color).ToArray()
            .Where((channel, index) => index % 4 == 0 && (float)channel > .5f).Count() > 512);
        scene.UseMaterial(0, scene.CreateRasterMaterial(rotated, new Vector4(.75f, .5f, .25f, 1)));
        RasterImage actual = await scene.ReadNextRasterImageAsync();
        Console.WriteLine($"{work} rotated: {actual.Metrics}");
        Assert.True(actual.Metrics.SoftwareRasterBatches > 0);
        ImageArtifacts.AssertEqual(new(64, 64, Format.D32Float, expected.Depth),
            new(64, 64, Format.D32Float, actual.Depth));
        ImageArtifacts.AssertEqual(new(64, 64, Format.R16G16B16A16Float, expected.Color),
            new(64, 64, Format.R16G16B16A16Float, actual.Color));
    }

    private static async Task<Mesh> CreateGridAsync()
    {
        // Both halves of a 64-vertex cluster carry different positions and depths.
        const int side = 8;
        var positions = new Vector3[side * side];
        var normals = new float[positions.Length * 3];
        var uvs = new float[positions.Length * 2];
        var indices = new List<uint>();
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            int vertex = y * side + x;
            float u = x / (float)(side - 1), v = y / (float)(side - 1);
            positions[vertex] = new Vector3(2 * u - 1, 2 * v - 1,
                .2f * u + .1f * v + .08f * MathF.Sin(x + 2 * y));
            normals[vertex * 3 + 2] = 1;
            uvs[vertex * 2] = u;
            uvs[vertex * 2 + 1] = v;
            if (x + 1 < side && y + 1 < side)
            {
                uint first = (uint)vertex;
                indices.AddRange([first, first + 1, first + side + 1,
                    first, first + side + 1, first + side]);
            }
        }
        List<RawAttribute> attributes =
        [
            new("NORMAL", normals, 3, GeometryValueType.Int8, 3, true),
            new("TEXCOORD_0", uvs, 2, GeometryValueType.Float16, 2, false),
        ];
        using MeshAsset asset = ClusterBuilder.ProcessRaw(positions, attributes, indices.ToArray(),
            [new MeshMaterialSlot(Guid.Empty)], "Software raster lane grid");
        ClusterMeshBlock geometry = asset.GetBlock<ClusterMeshBlock>();
        Mesh mesh = await geometry.CreateAsync(asset.GetBlock<MeshBlock>(), asset.Name);
        await geometry.LoadPagesAsync(mesh, Enumerable.Range(0, mesh.PageCount));
        return mesh;
    }

    private static Shader CreateShader(string backend, string name, string source)
    {
        string root = FrameStageScene.FindRoot();
        SlangCompileTarget target = backend == "Vulkan" ? SlangCompileTarget.Spirv : SlangCompileTarget.Dxil;
        var description = TestShaderLoading.CreateSessionDescription(target);
        description.SearchPaths = [Path.Combine(root, "templates/DefaultProject/assets/Shaders"),
            Path.Combine(root, "assets/Shaders")];
        IGlobalSession slang = TestShaderLoading.Slang;
        Assert.True(slang.CreateSession(description, out ISession session).Succeeded);
        ISlangBlob text = Slang.CreateBlob(Encoding.UTF8.GetBytes(source));
        IModule? unit = session.LoadModuleFromSource(name, name + ".slang", text, out ISlangBlob? diagnostics);
        Assert.True(unit is not null, DiagnosticText(diagnostics));
        var entries = new List<IEntryPoint>();
        for (int index = 0; index < unit!.GetDefinedEntryPointCount(); index++)
        {
            Assert.True(unit.GetDefinedEntryPoint(index, out IEntryPoint entry).Succeeded);
            entries.Add(entry);
        }
        Assert.True(session.CreateCompositeComponentType([unit, .. entries], out IComponentType combined,
            out diagnostics).Succeeded, DiagnosticText(diagnostics));
        Assert.True(combined.Link(out IComponentType linked, out diagnostics).Succeeded, DiagnosticText(diagnostics));
        return Shader.CreateFromSlang(name, slang, session, [unit], entries, linked, description.Targets);
    }

    private static string DiagnosticText(ISlangBlob? text) => text is null ? "" : Encoding.UTF8.GetString(text.Buffer);

    private const string VisibilitySource = """
        [VisibilityDepth("masked")]
        struct TextureVisibility : IVisibilityEvaluate<StandardVertex>
        {
            typealias Varying = EmptyVisibilityVarying;
            Texture2D<float4> AlbedoMap;
            SamplerState MaterialSampler;
            Varying makeVarying(StandardVertex vertex, VisibilityVertexInput input) { return {}; }
            Varying readLane(Varying value, uint lane) { return {}; }
            Varying interpolateVertex(Varying first, Varying second, Varying third, float3 bary) { return {}; }
            Varying interpolatePixel(Varying first, Varying second, Varying third, VisibilityInterpolationInput input) { return {}; }
            VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
            {
                VisibilityPixelResult result;
                result.Visible = AlbedoMap.SampleLevel(MaterialSampler, float2(.5, .5), 0).a >= .5 ? 1u : 0u;
                result.Depth = input.Depth;
                return result;
            }
        }
        """;
}
