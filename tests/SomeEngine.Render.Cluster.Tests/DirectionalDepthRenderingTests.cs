using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Shadows;
using SomeEngine.Resources;
using SomeEngine.Runtime;
using GeometryValueType = SomeEngine.Resources.Geometry.ValueType;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class DirectionalDepthRenderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AVisiblePlaneDoesNotCastAShadowOntoItself(bool hardware)
    {
        string root = FrameStageScene.FindRoot();
        using var files = new ShadowEvidence("directional-plane-depth-" + hardware);
        try
        {
            var inputs = new ShadowReferenceInputs(128, 128,
                new SceneCamera { Position = Vector3.Zero, Target = new(0, 0, -8), Up = Vector3.UnitY,
                    VerticalFieldOfView = 1, NearPlane = .1f, FarPlane = 100 },
                [new(new(0, 0, -8), new(2, 2, 1), true)],
                world => world.CreateEntity(new DirectionalLight(Vector3.Normalize(new(.35f, -.5f, -1)),
                    Vector3.One, 3)));
            await WritePlanesAsync(root, files.Content, inputs);
            Shader shader = await LoadVisibilityShaderAsync(root, "shadow_reference_visibility.slang");
            SceneLinearImage? image = null;
            var script = new SceneScript(files.Content, 16,
            [
                new(0, edit => { foreach (var entity in edit.MeshEntities) edit.GetMaterial(entity).Shader = shader; }),
                new(15, edit => edit.CaptureLinearImage(value => image = value)),
            ])
            {
                RasterEdgePixels = 4096,
                ShadowSettings = new VirtualShadowMapSettings
                {
                    VirtualResolution = 16384, AtlasSize = 1024, MaxShadowLights = 1,
                    DirectionalFirstClipmapLevel = 12, DirectionalClipmapLevels = 1,
                    DirectionalResolutionLodBias = 0, DirectionalMovingResolutionLodBias = 0,
                    DirectionalMinCameraViewportWidth = 512, DirectionalDepthRangeScale = 1000,
                    DirectionalReceiverMask = false, MaxFilterRadiusTexels = 0,
                    DirectionalCoarsePageMode = VirtualShadowCoarsePageMode.Disabled,
                },
            };
            string[] raster = hardware ? ["--hardware-raster"] : [];
            ApplicationResult result = await Application.RunAsync(
                ["--hardware-adapter", "--graphics-backend", "d3d12", "--hidden-window", "--no-vsync",
                 "--no-debug-ui", "--no-taa", "--fixed-step", "0.016666667", .. raster], script);
            Assert.True(result.Succeeded, result.Error?.ToString());
            SceneLinearImage actual = Assert.IsType<SceneLinearImage>(image);
            await File.WriteAllTextAsync(Path.Combine(files.Directory, "frame.json"), JsonSerializer.Serialize(actual.Frame,
                new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
            SceneRenderingQuality quality = Assert.IsType<SceneRenderingQuality>(actual.Frame.Quality);
            VirtualShadowFrameQuality shadows = Assert.IsType<VirtualShadowFrameQuality>(quality.Shadows);
            Assert.True(shadows.RequestedPages > 0);
            Assert.Equal(0u, shadows.AllocationFailures);
            Assert.Equal(0u, quality.Residency.MissingPages);
            VirtualShadowViewQuality view = Assert.Single(shadows.Views);
            Assert.Equal(12, view.ClipmapLevel);
            VirtualShadowMipQuality mip = Assert.Single(view.Mips);
            Assert.True(mip.MappedPages > 0 && mip.CoveredReceiverCells > 0);
            Assert.Equal(0u, mip.UnfinishedPages);
            Console.WriteLine(JsonSerializer.Serialize(new { shadows.RequestedPages, mip.MappedPages,
                mip.CoveredReceiverCells, view.BaseResolutionLodBias, view.AppliedResolutionLodBias }));
            Half[] expected = actual.Pixels.ToArray();
            int receivers = 0, shadowed = 0;
            for (int pixel = 0; pixel < expected.Length / 4; pixel++)
            {
                int offset = pixel * 4;
                if (expected[offset + 1] <= (Half).9f || expected[offset + 2] <= (Half).9f) continue;
                receivers++;
                if (expected[offset] < (Half).99f) shadowed++;
                expected[offset] = (Half)1;
            }
            Console.WriteLine(JsonSerializer.Serialize(new { Hardware = hardware, Receivers = receivers,
                SelfShadowedPixels = shadowed }));
            Assert.True(receivers > 512);
            ImageArtifacts.AssertEqual(new(128, 128, Format.R16G16B16A16Float,
                    MemoryMarshal.AsBytes(expected.AsSpan()).ToArray()),
                new(128, 128, Format.R16G16B16A16Float, MemoryMarshal.AsBytes(actual.Pixels.Span).ToArray()));
            files.Complete();
        }
        finally { if (Directory.Exists(files.Content)) Directory.Delete(files.Content, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANearbyPlaneShadowsTheReceiverUntilItIsRemoved(bool hardware)
    {
        string root = FrameStageScene.FindRoot();
        using var files = new ShadowEvidence("directional-near-depth-" + hardware);
        try
        {
            var inputs = new ShadowReferenceInputs(128, 128,
                new SceneCamera { Position = Vector3.Zero, Target = new(0, 0, -8), Up = Vector3.UnitY,
                    VerticalFieldOfView = 1, NearPlane = .1f, FarPlane = 100 },
                [new(new(0, 0, -8), new(2, 2, 1), true)],
                world => world.CreateEntity(new DirectionalLight(Vector3.Normalize(new(.97f, 0, -.2425f)),
                    Vector3.One, 3)));
            await WritePlanesAsync(root, files.Content, inputs);
            Shader shader = await LoadVisibilityShaderAsync(root, "shadow_reference_visibility.slang");
            Shader depthOffset = await LoadVisibilityShaderAsync(root, "forward_depth_visibility.slang");
            SceneLinearImage? shadowed = null, movedAway = null, removed = null;
            Entity caster = default;
            var script = new SceneScript(files.Content, 48,
            [
                new(0, edit =>
                {
                    var receiver = Assert.Single(edit.MeshEntities);
                    edit.GetMaterial(receiver).Shader = shader;
                    caster = edit.AddEntity(receiver, new TransformQvvs(new(-.48f, 0, -7.88f), Quaternion.Identity)
                    { Stretch = new(.12f, .4f, 1) });
                }),
                new(15, edit => edit.CaptureLinearImage(value => shadowed = value)),
                new(16, edit => { foreach (var entity in edit.MeshEntities) edit.GetMaterial(entity).Shader = depthOffset; }),
                new(31, edit => edit.CaptureLinearImage(value => movedAway = value)),
                new(32, edit =>
                {
                    foreach (var entity in edit.MeshEntities) edit.GetMaterial(entity).Shader = shader;
                    edit.DeleteEntity(caster);
                }),
                new(47, edit => edit.CaptureLinearImage(value => removed = value)),
            ])
            {
                RasterEdgePixels = 4096,
                ShadowSettings = new VirtualShadowMapSettings
                {
                    VirtualResolution = 16384, AtlasSize = 1024, MaxShadowLights = 1,
                    DirectionalFirstClipmapLevel = 12, DirectionalClipmapLevels = 1,
                    DirectionalResolutionLodBias = 0, DirectionalMovingResolutionLodBias = 0,
                    DirectionalMinCameraViewportWidth = 512, DirectionalDepthRangeScale = 1000,
                    DirectionalReceiverMask = false, MaxFilterRadiusTexels = 0,
                    DirectionalCoarsePageMode = VirtualShadowCoarsePageMode.Disabled,
                },
            };
            string[] raster = hardware ? ["--hardware-raster"] : [];
            ApplicationResult result = await Application.RunAsync(
                ["--hardware-adapter", "--graphics-backend", "d3d12", "--hidden-window", "--no-vsync",
                 "--no-debug-ui", "--no-taa", "--fixed-step", "0.016666667", .. raster], script);
            Assert.True(result.Succeeded, result.Error?.ToString());
            SceneLinearImage actual = Assert.IsType<SceneLinearImage>(shadowed);
            SceneLinearImage expected = Assert.IsType<SceneLinearImage>(removed);
            await File.WriteAllTextAsync(Path.Combine(files.Directory, "frames.json"), JsonSerializer.Serialize(new
            { WithCaster = actual.Frame, ForwardDepthOffset = movedAway!.Frame, WithoutCaster = expected.Frame },
                new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
            SceneRenderingQuality quality = Assert.IsType<SceneRenderingQuality>(actual.Frame.Quality);
            VirtualShadowFrameQuality shadows = Assert.IsType<VirtualShadowFrameQuality>(quality.Shadows);
            Assert.True(shadows.RequestedPages > 0);
            Assert.Equal(0u, shadows.AllocationFailures);
            Assert.Equal(0u, quality.Residency.MissingPages);
            VirtualShadowViewQuality view = Assert.Single(shadows.Views);
            Assert.Equal(12, view.ClipmapLevel);
            VirtualShadowMipQuality mip = Assert.Single(view.Mips);
            Assert.True(mip.MappedPages > 0 && mip.CoveredReceiverCells > 0);
            Assert.Equal(0u, mip.UnfinishedPages);
            Console.WriteLine(JsonSerializer.Serialize(new { shadows.RequestedPages, mip.MappedPages,
                mip.CoveredReceiverCells, view.BaseResolutionLodBias, view.AppliedResolutionLodBias }));
            Assert.Equal(2, actual.Frame.Meshes.Length);
            Assert.Single(expected.Frame.Meshes);
            Assert.Equal(-8f, expected.Frame.Meshes[0].Transform.M43);
            Half[] before = actual.Pixels.ToArray(), after = expected.Pixels.ToArray();
            Half[] offsetImage = Assert.IsType<SceneLinearImage>(movedAway).Pixels.ToArray();
            int restoredPixels = 0, forwardOffsetPixels = 0;
            for (int pixel = 0; pixel < before.Length / 4; pixel++)
            {
                int offset = pixel * 4;
                if (before[offset + 1] > (Half).9f && after[offset + 1] > (Half).9f &&
                    before[offset] < (Half).5f && after[offset] > (Half).99f)
                {
                    restoredPixels++;
                    if (offsetImage[offset + 1] > (Half).9f && offsetImage[offset] > (Half).99f) forwardOffsetPixels++;
                }
            }
            Console.WriteLine(JsonSerializer.Serialize(new { Hardware = hardware,
                RestoredShadowedReceiverPixels = restoredPixels, ForwardDepthOffsetLitPixels = forwardOffsetPixels }));
            if (restoredPixels < 4 || forwardOffsetPixels != restoredPixels)
            {
                ImageArtifacts.SavePng(Path.Combine(files.Directory, "with-caster.png"),
                    new(128, 128, Format.R16G16B16A16Float, MemoryMarshal.AsBytes(before.AsSpan()).ToArray()));
                ImageArtifacts.SavePng(Path.Combine(files.Directory, "forward-depth-offset.png"),
                    new(128, 128, Format.R16G16B16A16Float, MemoryMarshal.AsBytes(offsetImage.AsSpan()).ToArray()));
                ImageArtifacts.SavePng(Path.Combine(files.Directory, "without-caster.png"),
                    new(128, 128, Format.R16G16B16A16Float, MemoryMarshal.AsBytes(after.AsSpan()).ToArray()));
            }
            Assert.True(restoredPixels >= 4);
            Assert.Equal(restoredPixels, forwardOffsetPixels);
            // Geometry-derived probes are inside constant lit/shadow regions,
            // away from texel/LOD boundaries. Normal selection and sampling remain enabled.
            foreach (Vector3 probe in new Vector3[] { new(-.03f, 0, -8), new(.03f, 0, -8) })
            {
                Assert.True(VisibilityAt(actual, probe) < .5f);
                Assert.True(VisibilityAt(expected, probe) > .99f);
                Assert.True(VisibilityAt(movedAway!, probe) > .99f);
            }
            foreach (Vector3 probe in new Vector3[] { new(-1, 0, -8), new(1, 0, -8) })
                Assert.True(VisibilityAt(actual, probe) > .99f);
            files.Complete();
        }
        finally { if (Directory.Exists(files.Content)) Directory.Delete(files.Content, true); }
    }

    private static float VisibilityAt(SceneLinearImage image, Vector3 position)
    {
        Vector4 clip = Vector4.Transform(new Vector4(position, 1), image.Frame.View * image.Frame.Projection);
        int x = (int)MathF.Floor((clip.X / clip.W * .5f + .5f) * image.Frame.Width);
        int y = (int)MathF.Floor((.5f - clip.Y / clip.W * .5f) * image.Frame.Height);
        Assert.InRange(x, 0, image.Frame.Width - 1);
        Assert.InRange(y, 0, image.Frame.Height - 1);
        int offset = (y * image.Frame.Width + x) * 4;
        Assert.True(image.Pixels.Span[offset + 1] > (Half).9f);
        return (float)image.Pixels.Span[offset] / (float)image.Pixels.Span[offset + 1];
    }

    private static async Task<Shader> LoadVisibilityShaderAsync(string root, string file)
    {
        using var source = new ShaderSourceBlock { SourceNames = [file] };
        var description = TestShaderLoading.CreateSessionDescription();
        description.SearchPaths = [Path.Combine(root, "tests/SomeEngine.Render.Cluster.Tests/Shaders"),
            Path.Combine(root, "templates/DefaultProject/assets/Shaders"), Path.Combine(root, "assets/Shaders")];
        return await source.CreateAsync(TestShaderLoading.Slang, description, "Directional depth visibility");
    }

    private static async Task WritePlanesAsync(string root, string content, ShadowReferenceInputs inputs)
    {
        await ShadowReferenceScene.BuildAsync(root, content, inputs);
        var project = new AssetProject(content, []);
        AssetEntry material = project.Manifest.List(MaterialAsset.FileType).Single(item => item.Name == "Standard");
        Vector3[] positions = [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)];
        List<RawAttribute> attributes =
        [
            new("NORMAL", new float[] { 0,0,1, 0,0,1, 0,0,1, 0,0,1 }, 3, GeometryValueType.Int8, 3, true),
            new("TANGENT", new float[] { 1,0,0,1, 1,0,0,1, 1,0,0,1, 1,0,0,1 }, 4, GeometryValueType.Int8, 4, true),
            new("TEXCOORD_0", new float[] { 0,0, 1,0, 1,1, 0,1 }, 2, GeometryValueType.Float16, 2, false),
        ];
        using MeshAsset plane = ClusterBuilder.ProcessRaw(positions, attributes, [0,1,2, 0,2,3],
            [new MeshMaterialSlot(material.AssetGuid)], "Depth plane");
        AssetEntry planeEntry = await plane.SaveAsync(Path.Combine(content, "assets", "DepthPlane.mesh.asset"));
        project.RegisterAsset(planeEntry);
        AssetEntry scene = project.Manifest.List(WorldAsset.FileType).Single(item => item.Name == "Default");
        using var world = new World();
        foreach (var placement in inputs.Meshes)
        {
            var entity = world.CreateEntity(new WorldTransform
            {
                Qvvs = new TransformQvvs(placement.Position, Quaternion.Identity) { Stretch = placement.Scale },
            });
            world.Add(entity, new MeshSource { Mesh = new(planeEntry.AssetGuid) });
        }
        world.CreateEntity(inputs.Camera);
        inputs.AddLights?.Invoke(world);
        using var asset = new WorldAsset(scene.AssetGuid) { Name = scene.Name };
        asset.WriteWorld(world, SceneSerialization.CreateRegistry());
        project.RegisterAsset(await asset.SaveAsync(Path.Combine(content, scene.Path)));
    }
}
