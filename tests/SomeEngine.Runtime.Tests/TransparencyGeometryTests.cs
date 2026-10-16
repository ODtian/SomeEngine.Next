using System.Numerics;
using Game.Content;
using SomeEngine.Core.Math;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Transparency content")]
public sealed class TransparencyGeometryTests(TransparencyContentFixture content)
{
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
