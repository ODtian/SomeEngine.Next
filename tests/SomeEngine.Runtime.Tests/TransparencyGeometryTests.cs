using System.Numerics;
using Game.Content;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Resources;

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
        var firstImages = new List<SceneLinearImage>();
        var movedImages = new List<SceneLinearImage>();
        foreach (uint capacity in new[] { 524_288u, 262_144u })
        {
            SceneLinearImage? first = null, moved = null;
            var measurements = new List<ClusterBlendMetrics>();
            UnlitMaterial[] materials = Enumerable.Range(0, 4).Select(_ => new UnlitMaterial()).ToArray();
            Entity[] models = [];
            var actions = new List<SceneAction>
            {
                new(0, edit =>
                {
                    models = edit.MeshEntities.Where(entity => edit.GetMaterial(entity) is UnlitMaterial).ToArray();
                    Material source = edit.GetMaterial(models[0]);
                    for (int group = 0; group < materials.Length; group++)
                    {
                        materials[group].Update(source);
                        materials[group].BaseColorTint = new(.2f + group * .2f, .8f - group * .15f, 1, .35f);
                        materials[group].State = source.State with { Surface = SurfaceMode.Translucent, TwoSided = true };
                    }
                    for (int index = 0; index < models.Length; index++) edit.ReplaceMaterial(models[index], materials[index / 4]);
                }),
            };
            for (int frame = 0; frame < 4; frame++)
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
            actions.Add(new(0, edit => edit.CaptureLinearImage(image => first = image)));
            actions.Add(new(3, edit => edit.CaptureLinearImage(image => moved = image)));
            try
            {
                await SceneImages.RunAsync(backend, new SceneScript(content.ShadowedPhongParts, 4, actions)
                {
                    BlendTriangleCapacity = capacity,
                    WaitForFrames = false,
                    BlendFrameCompleted = measurements.Add,
                }, "--shading-path", "deferred", "--measure-blending");
            }
            finally { foreach (UnlitMaterial material in materials) material.Dispose(); }
            Assert.Equal(4, measurements.Count);
            Assert.All(measurements, frame => Assert.Equal(0, frame.CoverageReceiverMs));
            // The grayscale checker and neutral background retain this part's tint ratios.
            int sample = (480 * first!.Frame.Width + 800) * 4;
            float blue = MathF.Max(.1f, (float)first.Pixels.Span[sample + 2]);
            SceneFrame tintFrame = first.Frame with { Width = 1, Height = 1 };
            SceneImages.AssertLinearEqual(new(tintFrame, new Half[] { (Half).2f, (Half).8f, (Half)1, (Half)1 }),
                new(tintFrame, new Half[] { (Half)((float)first.Pixels.Span[sample] / blue),
                    (Half)((float)first.Pixels.Span[sample + 1] / blue),
                    (Half)((float)first.Pixels.Span[sample + 2] / blue), (Half)1 }), .003f);
            SceneImages.AssertLinearNotEqual(first!, moved!);
            firstImages.Add(first!); movedImages.Add(moved!);
        }
        SceneImages.AssertLinearEqual(firstImages[0], firstImages[1], .001f);
        SceneImages.AssertLinearEqual(movedImages[0], movedImages[1], .001f);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task TransparentDeformedPhongPartsAtWindowResolutionMatchOpaqueSurfaces(string backend)
    {
        SceneLinearImage? opaque = null, transparent = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.DeformedPhongParts, 16,
        [
            new(0, edit =>
            {
                Material material = edit.GetMaterial(edit.MeshEntities[0]);
                material.State = material.State with { TwoSided = true };
            }),
            new(7, edit => edit.CaptureLinearImage(image => opaque = image)),
            new(8, edit =>
            {
                Material material = edit.GetMaterial(edit.MeshEntities[0]);
                material.State = material.State with { Surface = SurfaceMode.Translucent };
            }),
            new(15, edit => edit.CaptureLinearImage(image => transparent = image)),
        ]) { WaitForFrames = false },
            "--hardware-raster", "--shading-path", "forward", "--no-taa", "--no-vsm");
        Assert.Equal((1280, 720), (transparent!.Frame.Width, transparent.Frame.Height));
        SceneImages.AssertLinearEqual(opaque!, transparent, .003f);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task TransparentCurvedGeometryMatchesAcrossRasterSourcesAndBufferBatches(string backend)
    {
        var images = new List<SceneLinearImage>();
        foreach (var (edgePixels, triangles, references, options) in new[]
        {
            (8f, 4096u, 4096u, new[] { "--hardware-raster" }),
            (256f, 4096u, 4096u, Array.Empty<string>()),
            (256f, 1u, 1u, Array.Empty<string>()),
        })
        {
            await SceneImages.RunAsync(backend, new SceneScript(content.DeformedPhong, 8,
            [
                new(0, edit =>
                {
                    foreach (var entity in edit.MeshEntities)
                    {
                        var material = (UnlitMaterial)edit.GetMaterial(entity);
                        material.State = material.State with { Surface = SurfaceMode.Translucent };
                        material.BaseColorTint = material.BaseColorTint with { W = .6f };
                    }
                }),
                new(7, edit => edit.CaptureLinearImage(images.Add)),
            ]) { RasterEdgePixels = edgePixels, BlendTriangleCapacity = triangles, BlendReferenceCapacity = references, WaitForFrames = false },
                ["--shading-path", "forward", "--no-taa", "--no-vsm", .. options]);
        }
        foreach (SceneLinearImage image in images.Skip(1))
            SceneImages.AssertLinearEqual(images[0], image, .001f);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task MovedTransparentCurvedSurfaceMatchesItsTransformFromStartup(string backend)
    {
        SceneLinearImage? before = null, changed = null, expected = null;
        TransformQvvs transform = new(new Vector3(.6f, .2f, 5.5f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .35f));
        await SceneImages.RunAsync(backend, new SceneScript(content.DeformedPhong, 16,
        [
            new(0, edit =>
            {
                foreach (var entity in edit.MeshEntities)
                {
                    var material = (UnlitMaterial)edit.GetMaterial(entity);
                    material.State = material.State with { Surface = SurfaceMode.Translucent };
                    material.BaseColorTint = material.BaseColorTint with { W = .6f };
                }
            }),
            new(7, edit => edit.CaptureLinearImage(image => before = image)),
            new(8, edit => edit.SetEntityTransform(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere"), transform)),
            new(15, edit => edit.CaptureLinearImage(image => changed = image)),
        ]) { RasterEdgePixels = 8, WaitForFrames = false }, "--hardware-raster", "--shading-path", "forward", "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.DeformedPhong, 8,
        [
            new(0, edit =>
            {
                foreach (var entity in edit.MeshEntities)
                {
                    var material = (UnlitMaterial)edit.GetMaterial(entity);
                    material.State = material.State with { Surface = SurfaceMode.Translucent };
                    material.BaseColorTint = material.BaseColorTint with { W = .6f };
                }
                edit.SetEntityTransform(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere"), transform);
            }),
            new(7, edit => edit.CaptureLinearImage(image => expected = image)),
        ]) { RasterEdgePixels = 8, WaitForFrames = false }, "--hardware-raster", "--shading-path", "forward", "--no-taa", "--no-vsm");
        SceneImages.AssertLinearNotEqual(before!, changed!);
        SceneImages.AssertLinearEqual(expected!, changed!, .001f);
    }
}
