using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class ShadowGeometryRenderingTests
{
    [Fact]
    public async Task MainCameraGeometryProjectionPreservesOffCameraCasterShadows()
    {
        var (positions, triangles, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(5);
        using MeshAsset asset = ClusterBuilder.ProcessRaw(positions, attributes, triangles,
            [new MeshMaterialSlot(Guid.Empty)], "Shadow LOD sphere");
        ClusterMeshBlock geometry = asset.GetBlock<ClusterMeshBlock>();
        var caster = await geometry.CreateAsync(asset.GetBlock<MeshBlock>(), asset.Name);
        await geometry.LoadPagesAsync(caster, Enumerable.Range(0, caster.PageCount));
        var receiver = await RasterTestGeometry.CreatePlaneAsync();
        var options = new ClusterPipelineOptions { MaxCandidates = 65536, ForceHardwareRaster = true,
            EnableFrameMetricsReadback = true };
        var shadows = new VirtualShadowMapSettings { VirtualResolution = 4096, AtlasSize = 2048,
            WorldUnitsPerCentimeter = 1,
            MaxShadowLights = 1, DirectionalClipmapLevels = 16, DirectionalFirstClipmapLevel = -4,
            DirectionalFirstCoarseClipmapLevel = 8, DirectionalLastCoarseClipmapLevel = 11, DynamicResolutionEnabled = false };

        string engine = FrameStageScene.FindRoot();
        using var visibilitySource = new ShaderSourceBlock { SourceNames = ["shadow_reference_visibility.slang"] };
        var shaderDescription = TestShaderLoading.CreateSessionDescription();
        shaderDescription.SearchPaths = [Path.Combine(engine, "tests/SomeEngine.Render.Cluster.Tests/Shaders"),
            Path.Combine(engine, "templates/DefaultProject/assets/Shaders"), Path.Combine(engine, "assets/Shaders")];
        var visibilityShader = await visibilitySource.CreateAsync(TestShaderLoading.Slang, shaderDescription,
            "Source material shadow visibility");
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100);
        Vector3 lightDirection = Vector3.Normalize(new(0, -1, -1));
        Assert.True(Matrix4x4.Invert(projection, out Matrix4x4 clipToWorld));
        Vector3[] normals = MemoryMarshal.Cast<float, Vector3>(attributes.Single(value => value.Name == "NORMAL").Data).ToArray();
        Vector4[] tangents = MemoryMarshal.Cast<float, Vector4>(attributes.Single(value => value.Name == "TANGENT").Data).ToArray();
        Vector2[] uv = MemoryMarshal.Cast<float, Vector2>(attributes.Single(value => value.Name == "TEXCOORD_0").Data).ToArray();
        RayShadowScene.Vertex[] vertices =
        [
            new(new(-1, -1, 0), Vector3.UnitZ, new(1, 0, 0, 1), new(0, 0)),
            new(new(1, -1, 0), Vector3.UnitZ, new(1, 0, 0, 1), new(1, 0)),
            new(new(1, 1, 0), Vector3.UnitZ, new(1, 0, 0, 1), new(1, 1)),
            new(new(-1, 1, 0), Vector3.UnitZ, new(1, 0, 0, 1), new(0, 1)),
            .. positions.Select((position, index) => new RayShadowScene.Vertex(position, normals[index], tangents[index], uv[index])),
        ];
        var rays = new RayShadowScene(vertices, [0, 1, 2, 0, 2, 3, .. triangles],
            [new(0, 0, 1, 1, 1, 0, 1, 1, -1, 1), new(4, 6, 1, 1, 1, 0, 1, 1, -1, 1)],
            [0xffff8080, 0xffffffff],
            [new([new(0, 4, 0, 6, true)]), new([new(4, (uint)positions.Length, 6, (uint)triangles.Length, true)])],
            [new(0, Matrix4x4.CreateScale(2, 2, .1f) * Matrix4x4.CreateTranslation(0, 0, -8), 0),
                new(0, Matrix4x4.CreateTranslation(20, 4, -4), 0),
                new(1, Matrix4x4.CreateTranslation(0, 4, -4), 1)]);
        RayShadowImage.SoftPixel[] reference = await RayShadowImage.TraceSoftAsync(engine, rays, new()
        {
            ClipToWorld = clipToWorld, CameraPosition = Vector3.Zero, Width = 64, Height = 64,
            LightDirection = lightDirection, LightKind = 0, NearPlane = .1f, FarPlane = 100,
            SourceAngleRadians = .5357f * MathF.PI / 180, SampleCount = RayShadowImage.SourceSampleCount,
        }, vulkan: false);
        using var evidence = new ShadowEvidence("main-camera-shadow-source");

        RasterImage expected;
        await using (var fine = await FrameStageScene.CreateAsync("D3D12", true, options, receiver, shadows))
        {
            fine.UseStandardShader(visibilityShader);
            fine.SetMeshTransform(0, new(new Vector3(0, 0, -8), Quaternion.Identity) { Stretch = new(2, 2, .1f) });
            fine.SetMeshTransform(1, new(new Vector3(20, 4, -4), Quaternion.Identity));
            var casterEntity = fine.AddGeometryMesh(caster, new(new Vector3(0, 4, -4), Quaternion.Identity));
            fine.UseDirectionalLight(lightDirection);
            fine.SetView(new(Matrix4x4.Identity, projection,
                64, 64, GeometryProjection: Matrix4x4.CreateOrthographic(.001f, .001f, .1f, 100)));
            expected = await fine.ReadNextRasterImageAsync();
            for (int frame = 1; frame < 6; frame++)
                expected = await fine.ReadNextRasterImageAsync();
            AssertSourceVisibility("fine", expected);
            fine.RemoveMaterialMesh(casterEntity);
            Assert.NotEqual(expected.Color, (await fine.ReadNextRasterImageAsync()).Color);
        }
        await using var coarse = await FrameStageScene.CreateAsync("D3D12", true, options, receiver, shadows);
        coarse.UseStandardShader(visibilityShader);
        coarse.SetMeshTransform(0, new(new Vector3(0, 0, -8), Quaternion.Identity) { Stretch = new(2, 2, .1f) });
        coarse.SetMeshTransform(1, new(new Vector3(20, 4, -4), Quaternion.Identity));
        coarse.AddGeometryMesh(caster, new(new Vector3(0, 4, -4), Quaternion.Identity));
        coarse.UseDirectionalLight(lightDirection);
        coarse.SetView(new(Matrix4x4.Identity, projection,
            64, 64, GeometryProjection: Matrix4x4.CreateOrthographic(10000, 10000, .1f, 100)));
        RasterImage actual = await coarse.ReadNextRasterImageAsync();
        for (int frame = 1; frame < 6; frame++)
            actual = await coarse.ReadNextRasterImageAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { SourceTriangles = triangles.Length / 3,
            SourceVertices = positions.Length, SourcePages = caster.PageCount,
            Expected = expected.Metrics, Actual = actual.Metrics, Feedback = coarse.FeedbackStatistics }));
        Assert.True(actual.Metrics.Work.Complete);
        AssertSourceVisibility("coarse", actual);
        Assert.Equal(expected.Depth, actual.Depth);
        coarse.SetView(new(Matrix4x4.Identity, projection,
            64, 64, GeometryProjection: Matrix4x4.CreateOrthographic(.001f, .001f, .1f, 100)));
        RasterImage cached = await coarse.ReadNextRasterImageAsync();
        AssertSourceVisibility("cached", cached);
        Assert.Equal(expected.Depth, cached.Depth);
        evidence.Complete();

        void AssertSourceVisibility(string name, RasterImage image)
        {
            string directory = Path.Combine(evidence.Directory, name);
            Directory.CreateDirectory(directory);
            Half[] rgba = MemoryMarshal.Cast<byte, Half>(image.Color).ToArray();
            Vector3[] visibility = Enumerable.Range(0, 64 * 64).Select(pixel => new Vector3(
                (float)rgba[pixel * 4], (float)rgba[pixel * 4 + 1], (float)rgba[pixel * 4 + 2])).ToArray();
            RayShadowSoftComparison comparison = RayShadowSoftComparison.Write(directory, reference, 64, 64, visibility);
            Console.WriteLine(JsonSerializer.Serialize(new { Projection = name, SourceTriangles = triangles.Length / 3,
                ReferenceSamples = RayShadowImage.SourceSampleCount, Comparison = comparison }));
            Assert.True(comparison.ShadowedPixels > 0 && comparison.LitPixels > 0, $"Source receivers: {comparison}");
            Assert.True(comparison.RenderedReceiverCoverage >= .98f, $"Receiver coverage: {comparison}");
            Assert.Equal(0, comparison.SelfHitPixels);
            Assert.True(comparison.MeanVisibilityError <= .02f && comparison.MismatchFraction <= .02f,
                $"Independent source emitter visibility: {comparison}");
        }
    }
}
