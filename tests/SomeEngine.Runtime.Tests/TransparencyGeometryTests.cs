using SomeEngine.Transparency.Measurements;
using System.Numerics;
using DefaultGame.Content;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Resources;
using SomeEngine.Resources.Layers;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Transparency content")]
public sealed class TransparencyGeometryTests(TransparencyContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task TranslucentPhongPartsKeepRenderingAcrossFrameUpdates(string backend)
    {
        await content.BuildShadowedPhongPartsAsync();
        SceneLinearImage? first = null, moved = null;
        var measurements = new List<BlendFrameMetrics>();
        UnlitMaterial[] materials = Enumerable.Range(0, 4).Select(_ => new UnlitMaterial()).ToArray();
        Entity[] models = [];
        var actions = new List<SceneAction>
        {
            new(0, edit =>
            {
                models = edit.MeshEntities.Where(entity => edit.GetMaterial(entity) is UnlitMaterial).ToArray();
                var source = (UnlitMaterial)edit.GetMaterial(models[0]);
                for (int group = 0; group < materials.Length; group++)
                {
                    materials[group].Update(source);
                    materials[group].BaseColorTint = new(.2f + group * .2f, .8f - group * .15f, 1, .35f);
                }
                for (int index = 0; index < models.Length; index++) edit.ReplaceMaterial(models[index], materials[index / 4]);
            }),
        };
        for (int frame = 0; frame < 16; frame++)
        {
            int current = frame;
            actions.Add(new(frame, edit =>
            {
                for (int index = 0; index < models.Length; index++)
                {
                    int group = index / 4, layer = index % 4;
                    Vector3 position = new((group % 2 - .5f) * .9f + MathF.Sin(current / 60f + index * .3f) * .15f,
                        (group / 2 - .5f) * .7f, 2.4f + layer * .2f);
                    edit.SetEntityTransform(models[index], new(position,
                        Quaternion.CreateFromAxisAngle(Vector3.UnitY, current / 120f + index * .2f), .35f));
                }
            }));
        }
        actions.Add(new(7, edit => edit.CaptureLinearImage(image => first = image)));
        actions.Add(new(15, edit => edit.CaptureLinearImage(image => moved = image)));
        await SceneImages.RunAsync(backend, new SceneScript(content.ShadowedPhongParts, 16, actions)
        {
            WaitForFrames = false,
            CreateFrameFeatures = rendering =>
            {
                LayerPassId pass = rendering.Layers!.AddPass(new("Phong parts"));
                foreach (Material material in materials) material.Layer = new(pass, 0, new());
                return [new BlendMeasurementFeature(rendering, measurements.Add)];
            },
        }, "--shading-path", "deferred", "--measure-blending");
        Assert.Equal(16, measurements.Count);
        Assert.True(measurements.Single(frame => frame.SimulationFrame == 7).Layers.Fragments > 0);
        Assert.True(measurements.Single(frame => frame.SimulationFrame == 15).Layers.Fragments > 0);
        int sample = (480 * first!.Frame.Width + 800) * 4;
        float blue = MathF.Max(.1f, (float)first.Pixels.Span[sample + 2]);
        SceneFrame tintFrame = first.Frame with { Width = 1, Height = 1 };
        SceneImages.AssertLinearEqual(new(tintFrame, new Half[] { (Half).2f, (Half).8f, (Half)1, (Half)1 }),
            new(tintFrame, new Half[] { (Half)((float)first.Pixels.Span[sample] / blue),
                (Half)((float)first.Pixels.Span[sample + 1] / blue),
                (Half)((float)first.Pixels.Span[sample + 2] / blue), (Half)1 }), .003f);
        SceneImages.AssertLinearNotEqual(first!, moved!);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task TransparentDeformedPhongPartsAtWindowResolutionMatchOpaqueSurfaces(string backend)
    {
        SceneLinearImage? opaque = null, transparent = null;
        var materials = new List<Material>();
        LayerPassId pass = default;
        await SceneImages.RunAsync(backend, new SceneScript(content.DeformedPhongParts, 16,
        [
            new(0, edit =>
            {
                foreach (Entity entity in edit.MeshEntities)
                {
                    Material material = edit.GetMaterial(entity);
                    materials.Add(material);
                }
            }),
            new(7, edit => edit.CaptureLinearImage(image => opaque = image)),
            new(8, _ => { foreach (Material material in materials) material.Layer = new(pass, 0, new()); }),
            new(15, edit => edit.CaptureLinearImage(image => transparent = image)),
        ])
        {
            WaitForFrames = false,
            CreateFrameFeatures = rendering => { pass = rendering.Layers!.AddPass(new("Opaque-valued Phong layers")); return []; },
        }, "--hardware-raster", "--shading-path", "forward", "--no-taa", "--no-vsm");
        Assert.Equal((1280, 720), (transparent!.Frame.Width, transparent.Frame.Height));
        SceneImages.AssertLinearEqual(opaque!, transparent, .003f);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task TransparentCurvedGeometryMatchesHardwareAndSoftwareRasterization(string backend)
    {
        var images = new List<SceneLinearImage>();
        var fragments = new List<uint>();
        foreach (uint hardwareWindowTiles in new[] { 1u, uint.MaxValue })
        {
            UnlitMaterial? green = null;
            var recordedFrame = new RenderingFrameIdentity(7);
            SomeEngine.Render.Features.Layers.LayerComposition? layers = null;
            await SceneImages.RunAsync(backend, new SceneScript(content.DeformedPhong, 8,
            [
                new(0, edit =>
                {
                    Entity greenEntity = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere");
                    green = (UnlitMaterial)edit.GetMaterial(greenEntity);
                    green.BaseColorTint = green.BaseColorTint with { W = .6f };
                }),
                new(7, edit => edit.CaptureLinearImage(images.Add)),
            ])
            {
                RasterEdgePixels = 8, VisibilityMode = ClusterVisibilityMode.HardwareOnly, WaitForFrames = false,
                CreateFrameFeatures = rendering =>
                {
                    layers = rendering.Layers!;
                    layers.HardwareWindowTiles = hardwareWindowTiles;
                    LayerPassId pass = layers.AddPass(new("Curved layers"));
                    green!.Layer = new(pass, 0, new());
                    return [recordedFrame];
                },
            }, "--no-mesh-shaders", "--hardware-raster", "--shading-path", "forward", "--no-taa", "--no-vsm");
            Assert.Equal(recordedFrame.FrameIdentity, layers!.Metrics.FrameIdentity);
            Assert.True(layers.Metrics.Fragments > 0u);
            fragments.Add(layers.Metrics.Fragments);
            SceneLinearImage image = images[^1];
            Assert.Equal((128, 128), (image.Frame.Width, image.Frame.Height));
            int firstGreen = image.Frame.Width, lastGreen = -1;
            for (int pixel = 0; pixel < image.Frame.Width * image.Frame.Height; pixel++)
                if ((float)image.Pixels.Span[pixel * 4 + 1] > (float)image.Pixels.Span[pixel * 4] + .01f)
                {
                    int x = pixel % image.Frame.Width;
                    firstGreen = Math.Min(firstGreen, x);
                    lastGreen = Math.Max(lastGreen, x);
                }
            Assert.True(lastGreen - firstGreen + 1 > layers.TileSize,
                $"The green layer covers {lastGreen - firstGreen + 1} pixels across; tile width is {layers.TileSize}.");
        }
        Assert.Equal(fragments[0], fragments[1]);
        SceneImages.AssertLinearEqual(images[0], images[1], .001f);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task MovedTransparentCurvedSurfaceMatchesItsTransformFromStartup(string backend)
    {
        SceneLinearImage? before = null, changed = null, expected = null;
        TransformQvvs transform = new(new Vector3(.6f, .2f, 5.5f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .35f));
        var movedMaterials = new List<Material>();
        await SceneImages.RunAsync(backend, new SceneScript(content.DeformedPhong, 16,
        [
            new(0, edit =>
            {
                foreach (Entity entity in edit.MeshEntities)
                {
                    var material = (UnlitMaterial)edit.GetMaterial(entity);
                    material.BaseColorTint = material.BaseColorTint with { W = .6f };
                    movedMaterials.Add(material);
                }
            }),
            new(7, edit => edit.CaptureLinearImage(image => before = image)),
            new(8, edit => edit.SetEntityTransform(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere"), transform)),
            new(15, edit => edit.CaptureLinearImage(image => changed = image)),
        ])
        {
            RasterEdgePixels = 8, WaitForFrames = false,
            CreateFrameFeatures = rendering =>
            {
                LayerPassId pass = rendering.Layers!.AddPass(new("Moving curved layers"));
                foreach (Material material in movedMaterials) material.Layer = new(pass, 0, new());
                return [];
            },
        }, "--hardware-raster", "--shading-path", "forward", "--no-taa", "--no-vsm");
        var referenceMaterials = new List<Material>();
        await SceneImages.RunAsync(backend, new SceneScript(content.DeformedPhong, 8,
        [
            new(0, edit =>
            {
                foreach (Entity entity in edit.MeshEntities)
                {
                    var material = (UnlitMaterial)edit.GetMaterial(entity);
                    material.BaseColorTint = material.BaseColorTint with { W = .6f };
                    referenceMaterials.Add(material);
                }
                edit.SetEntityTransform(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere"), transform);
            }),
            new(7, edit => edit.CaptureLinearImage(image => expected = image)),
        ])
        {
            RasterEdgePixels = 8, WaitForFrames = false,
            CreateFrameFeatures = rendering =>
            {
                LayerPassId pass = rendering.Layers!.AddPass(new("Curved layers from startup"));
                foreach (Material material in referenceMaterials) material.Layer = new(pass, 0, new());
                return [];
            },
        }, "--hardware-raster", "--shading-path", "forward", "--no-taa", "--no-vsm");
        SceneImages.AssertLinearNotEqual(before!, changed!);
        SceneImages.AssertLinearEqual(expected!, changed!, .001f);
    }

    private sealed class RenderingFrameIdentity(int simulationFrame) : IApplicationFrameFeature
    {
        internal ulong FrameIdentity { get; private set; }

        public void Reconcile() { }

        public void Record(ref SomeEngine.RenderGraph.RenderGraphFrame frame, int currentSimulationFrame)
        {
            if (currentSimulationFrame == simulationFrame) FrameIdentity = frame.FrameIdentity;
        }

        public void Dispose() { }
    }
}
