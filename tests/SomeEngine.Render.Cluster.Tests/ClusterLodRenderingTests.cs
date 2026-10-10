using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using GeometryValueType = SomeEngine.Resources.Geometry.ValueType;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class ClusterLodRenderingTests
{
    [Fact]
    public async Task CoarseGeometrySelectionPreservesSourceMaterialBoundaries()
    {
        const int cells = 16;
        var positions = new List<Vector3>();
        var indices = new List<uint>();
        var materialIndices = new List<float>();
        for (int y = 0; y < cells; y++)
        for (int x = 0; x < cells; x++)
        {
            uint first = checked((uint)positions.Count);
            float left = -1 + x * (2f / cells);
            float bottom = -1 + y * (2f / cells);
            float right = -1 + (x + 1) * (2f / cells);
            float top = -1 + (y + 1) * (2f / cells);
            positions.AddRange([new(left, bottom, 0), new(right, bottom, 0),
                new(right, top, 0), new(left, top, 0)]);
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
            materialIndices.AddRange(Enumerable.Repeat((float)((x + y) & 1), 4));
        }
        using MeshAsset asset = ClusterBuilder.ProcessRaw(positions.ToArray(),
            [new RawAttribute("NORMAL", positions.SelectMany(_ => new[] { 0f, 0f, 1f }).ToArray(),
                3, GeometryValueType.Int8, 3, true),
             new RawAttribute("_MATERIAL_INDEX", materialIndices.ToArray(), 1, GeometryValueType.UInt8, 1, false)],
            indices.ToArray(), [new MeshMaterialSlot(Guid.Empty), new MeshMaterialSlot(Guid.Empty)],
            "Two-material tiled surface");
        ClusterMeshBlock geometry = asset.GetBlock<ClusterMeshBlock>();
        var mesh = await geometry.CreateAsync(asset.GetBlock<MeshBlock>(), asset.Name);
        await geometry.LoadPagesAsync(mesh, Enumerable.Range(0, mesh.PageCount));

        string root = FrameStageScene.FindRoot();
        using var source = new ShaderSourceBlock { SourceNames = ["emissive_material.slang"] };
        var description = TestShaderLoading.CreateSessionDescription();
        description.SearchPaths = [Path.Combine(root, "tests/SomeEngine.Render.Cluster.Tests/Shaders"),
            Path.Combine(root, "templates/DefaultProject/assets/Shaders"), Path.Combine(root, "assets/Shaders")];
        var shader = await source.CreateAsync(TestShaderLoading.Slang, description, "Material boundary colors");
        await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 65536, ForceHardwareRaster = true }, mesh);
        scene.SetGeometryVisible(false);
        var entity = scene.AddGeometryMesh(mesh, new TransformQvvs(new Vector3(0, 0, -4), Quaternion.Identity));
        Vector3 firstColor = new(3, 1, 2), secondColor = new(1, 2, 3);
        scene.UseEntityMaterials(entity, scene.CreateEmissiveMaterial(shader, firstColor),
            scene.CreateEmissiveMaterial(shader, secondColor));
        scene.SetView(new RenderView(Matrix4x4.Identity, Matrix4x4.CreateOrthographic(2, 2, .1f, 100), 64, 64,
            GeometryProjection: Matrix4x4.CreateOrthographic(10000, 10000, .1f, 100)));
        byte[] actual = await scene.ReadClusterColorAsync();

        Half[] expected = new Half[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            int cellX = x * cells / 64;
            int cellY = (63 - y) * cells / 64;
            Vector3 color = ((cellX + cellY) & 1) == 0 ? firstColor : secondColor;
            int pixel = (y * 64 + x) * 4;
            expected[pixel] = (Half)color.X;
            expected[pixel + 1] = (Half)color.Y;
            expected[pixel + 2] = (Half)color.Z;
            expected[pixel + 3] = (Half)1;
        }
        ImageArtifacts.AssertEqual(new(64, 64, Format.R16G16B16A16Float,
                MemoryMarshal.AsBytes(expected.AsSpan()).ToArray()),
            new(64, 64, Format.R16G16B16A16Float, actual));
    }
}
