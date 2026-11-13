using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.Render;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Core.Math;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

// #128: WARP complete Cluster frames time out; image scenarios require hardware.
[Trait("Category", "Hardware")]
[Collection("Rendering content")]
public sealed class GeometryRenderingTests(RenderingContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task ProjectedDecalOrderMatchesTheSameUnlitSurface(string backend)
    {
        SceneImage? layered = null, reference = null;
        var red = new StandardMaterial { BaseColorTint = new(1, 0, 0, .5f) };
        var green = new StandardMaterial { BaseColorTint = new(0, 1, 0, .5f) };
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 8,
        [
            new(0, edit =>
            {
                foreach (Entity entity in edit.MeshEntities)
                    ((UnlitMaterial)edit.GetMaterial(entity)).BaseColorTint = new(0, 0, 1, 1);
                var material = (UnlitMaterial)edit.GetMaterial(edit.MeshEntities[0]);
                red.Shader = green.Shader = material.Shader;
                red.AlbedoMap = green.AlbedoMap = material.AlbedoMap;
                TransformQvvs volume = new(new Vector3(0, 0, 7), Quaternion.Identity) { Stretch = new(4, 4, 4) };
                edit.AddMaterialDecal(green, volume, 2);
                edit.AddMaterialDecal(red, volume, 1);
            }),
            new(7, edit => edit.CaptureImage(image => layered = image)),
        ]), "--no-mesh-shaders", "--hardware-raster", "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 8,
        [
            new(0, edit =>
            {
                foreach (Entity entity in edit.MeshEntities)
                    ((UnlitMaterial)edit.GetMaterial(entity)).BaseColorTint = new(.25f, .5f, .25f, 1);
            }),
            new(7, edit => edit.CaptureImage(image => reference = image)),
        ]), "--no-mesh-shaders", "--hardware-raster", "--no-taa", "--no-vsm");
        SceneImages.AssertEqualRegion(reference!, layered!, 20, 29, 6, 6);
        SceneImages.AssertEqualRegion(reference!, layered!, 39, 29, 6, 6);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task RasterBatchOverflowPreservesTheSameFrameImageForAllMaterials(string backend)
    {
        const uint batchCapacity = 1;
        var captured = new List<SceneLinearImage>();
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 8,
            Enumerable.Range(0, 8).Select(frame => new SceneAction(frame,
                edit => edit.CaptureLinearImage(captured.Add))))
        {
            RasterBatchCapacity = batchCapacity,
            RasterEdgePixels = 512,
        }, "--no-mesh-shaders", "--no-taa", "--no-vsm");
        SceneLinearImage[] overflow = captured.Where(image =>
            (image.Frame.Quality!.Geometry.Work.Overflow & RenderGeometryWorkFlags.RasterBatches) != 0).ToArray();
        Assert.NotEmpty(overflow);
        SceneLinearImage source = overflow[0];
        ClusterFrameMetrics metrics = source.Frame.Quality!.Geometry;
        Assert.Equal((ulong)source.Frame.SubmittedFrame + 1u, metrics.FrameIndex);
        Assert.Equal((uint?)batchCapacity, source.Frame.RasterBatchCapacity);
        Assert.True((metrics.Work.Overflow & RenderGeometryWorkFlags.RasterBatches) != 0);
        Assert.True(metrics.Work.Complete);
        Assert.True(metrics.Work.HardwareRasterBatches + metrics.Work.SoftwareRasterBatches > 0);
        SceneLinearImage? reference = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, source.Frame.Frame + 1,
            [new(source.Frame.Frame, edit => edit.CaptureLinearImage(image => reference = image))])
        {
            RasterEdgePixels = 512,
        }, "--no-mesh-shaders", "--no-taa", "--no-vsm");
        ClusterFrameMetrics referenceMetrics = reference!.Frame.Quality!.Geometry;
        Assert.Equal((ulong)reference.Frame.SubmittedFrame + 1u, referenceMetrics.FrameIndex);
        Assert.Equal(RenderGeometryWorkFlags.None, referenceMetrics.Work.Overflow & RenderGeometryWorkFlags.RasterBatches);
        Assert.True(referenceMetrics.Work.Complete);
        SceneImages.AssertLinearEqual(reference, source, .001f);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task NonAtomicVisibilityMatchesHardwareRasterForMultipleMaterials(string backend)
    {
        SceneImage? software = null, hardware = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 8,
        [new(7, edit => edit.CaptureImage(image => software = image))])
        {
            VisibilityMode = ClusterVisibilityMode.NonAtomic,
            RasterEdgePixels = 512,
        }, "--no-mesh-shaders", "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 8,
        [new(7, edit => edit.CaptureImage(image => hardware = image))])
        {
            VisibilityMode = ClusterVisibilityMode.HardwareOnly,
        }, "--no-mesh-shaders", "--no-taa", "--no-vsm");
        SceneImages.AssertEqual(hardware!, software!, 1);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task AddedGeometryMatchesStartingWithThatGeometry(string backend)
    {
        SceneImage? before = null, changed = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(7, edit => edit.CaptureImage(image => before = image)),
            new(8, edit => { edit.AddEntity(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"), new(new Vector3(0, 1.8f, 6), Quaternion.Identity)); }),
            new(11, edit => edit.CaptureImage(image => changed = image)),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(0, edit => { edit.AddEntity(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"), new(new Vector3(0, 1.8f, 6), Quaternion.Identity)); }),
            new(11, edit => edit.CaptureImage(image => expected = image)),
        ]), "--no-taa", "--no-vsm");
        Assert.NotEqual(before!.Pixels.ToArray(), changed!.Pixels.ToArray());
        SceneImages.AssertEqual(expected!, changed);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task DeletedGeometryMatchesStartingWithTheRemainingGeometry(string backend)
    {
        SceneImage? before = null, changed = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(7, edit => edit.CaptureImage(image => before = image)),
            new(8, edit => { edit.DeleteEntity(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere")); }),
            new(11, edit => edit.CaptureImage(image => changed = image)),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(0, edit => { edit.DeleteEntity(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere")); }),
            new(11, edit => edit.CaptureImage(image => expected = image)),
        ]), "--no-taa", "--no-vsm");
        Assert.NotEqual(before!.Pixels.ToArray(), changed!.Pixels.ToArray());
        SceneImages.AssertEqual(expected!, changed);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task StretchedGeometryCrossingTheViewEdgeMatchesStartingAtThatTransform(string backend)
    {
        SceneImage? before = null, changed = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(7, edit => edit.CaptureImage(image => before = image)),
            new(8, edit => { edit.SetEntityTransform(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"), new(new Vector3(3.7f, 0, 6), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .47f)) { Stretch = new(.75f, 1.25f, .5f) }); }),
            new(11, edit => edit.CaptureImage(image => changed = image)),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(0, edit => { edit.SetEntityTransform(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"), new(new Vector3(3.7f, 0, 6), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .47f)) { Stretch = new(.75f, 1.25f, .5f) }); }),
            new(11, edit => edit.CaptureImage(image => expected = image)),
        ]), "--no-taa", "--no-vsm");
        Assert.NotEqual(before!.Pixels.ToArray(), changed!.Pixels.ToArray());
        SceneImages.AssertColorRegion(changed!, 1, 30, 3, 4, 186, 0, 0);
        SceneImages.AssertEqual(expected!, changed);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task CanceledGeometryFrameLeavesTheFollowingImageEqualToTheCompletedSequence(string backend)
    {
        SceneImage? changed = null, expected = null;
        Entity extra = default;
        var canceled = new SceneScript(content.Spheres, 13,
        [
            new(8, edit =>
            {
                extra = edit.AddEntity(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"),
                    new(new Vector3(0, 0, 3), Quaternion.Identity, 2));
                edit.CancelFrame();
            }),
            new(9, edit => edit.DeleteEntity(extra)),
            new(12, edit => edit.CaptureImage(image => changed = image)),
        ]);
        ApplicationResult result = await Application.RunAsync(
            ["--hardware-adapter", "--graphics-backend", backend, "--no-taa", "--no-vsm"], canceled);
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(12, result.CompletedFrames);
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
            [new(11, edit => edit.CaptureImage(image => expected = image))]), "--no-taa", "--no-vsm");
        SceneImages.AssertEqual(expected!, changed!);
    }

    [SceneTheory]
    [InlineData("d3d12", 4f)]
    [InlineData("d3d12", 64f)]
    [InlineData("vulkan", 4f)]
    [InlineData("vulkan", 64f)]
    public async Task StreamedGeometryKeepsTheSphereInteriorCoveredWhileDetailDoesNotFit(string backend, float rasterEdgePixels)
    {
        var images = new List<SceneImage>();
        SceneImage? full = null;
        await SceneImages.RunAsync(backend, new SceneScript(await content.Streaming, 16,
        [
            new(3, edit => edit.CaptureImage(images.Add)),
            new(7, edit => edit.CaptureImage(images.Add)),
            new(15, edit => edit.CaptureImage(images.Add)),
        ]) { GeometryMemoryBytes = 262144, RasterEdgePixels = rasterEdgePixels }, "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(await content.Streaming, 16,
            [new(15, edit => edit.CaptureImage(image => full = image))]) { RasterEdgePixels = rasterEdgePixels }, "--no-taa", "--no-vsm");
        Assert.Equal(3, images.Count);
        // Radius-two sphere at z=6 covers this whole 8x8 region at both coarse and full detail.
        SceneImages.AssertColorRegion(full!, 28, 28, 8, 8, 186, 0, 0);
        foreach (SceneImage image in images) SceneImages.AssertColorRegion(image, 28, 28, 8, 8, 186, 0, 0);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task CameraMovementRecoversOccludedGeometryLikeRenderingWithFreshOcclusion(string backend)
    {
        SceneImage? hidden = null, recovered = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(0, edit =>
            {
                Entity red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                Entity green = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere");
                edit.SetEntityTransform(red, new(new Vector3(0, 0, 8), Quaternion.Identity));
                edit.SetEntityTransform(green, new(new Vector3(0, 0, 4), Quaternion.Identity, 1.5f));
            }),
            new(7, edit => edit.CaptureImage(image => hidden = image)),
            new(8, edit => { edit.SetCameraTransform(new(3, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.Atan(3f / 8))); edit.CaptureImage(image => recovered = image); }),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(0, edit =>
            {
                Entity red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                Entity green = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere");
                edit.SetEntityTransform(red, new(new Vector3(0, 0, 8), Quaternion.Identity));
                edit.SetEntityTransform(green, new(new Vector3(0, 0, 4), Quaternion.Identity, 1.5f));
            }),
            new(8, edit => { edit.SetCameraTransform(new(3, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.Atan(3f / 8))); edit.CaptureImage(image => expected = image); }),
        ]) { UseCameraHistory = false }, "--no-taa", "--no-vsm");
        SceneImages.AssertColorRegion(hidden!, 28, 28, 8, 8, 0, 186, 0);
        Assert.NotEqual(hidden!.Pixels.ToArray(), recovered!.Pixels.ToArray());
        SceneImages.AssertEqual(expected!, recovered);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task MovingAnOccluderRecoversTheRearGeometryInTheSameFrame(string backend)
    {
        SceneImage? hidden = null, recovered = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(0, edit =>
            {
                Entity red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                Entity green = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere");
                edit.SetEntityTransform(red, new(new Vector3(0, 0, 8), Quaternion.Identity));
                edit.SetEntityTransform(green, new(new Vector3(0, 0, 4), Quaternion.Identity, 1.5f));
            }),
            new(7, edit => edit.CaptureImage(image => hidden = image)),
            new(8, edit =>
            {
                edit.SetEntityTransform(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere"),
                    new(new Vector3(100, 0, 4), Quaternion.Identity, 1.5f));
                edit.CaptureImage(image => recovered = image);
            }),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(0, edit =>
            {
                Entity red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                Entity green = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere");
                edit.SetEntityTransform(red, new(new Vector3(0, 0, 8), Quaternion.Identity));
                edit.SetEntityTransform(green, new(new Vector3(0, 0, 4), Quaternion.Identity, 1.5f));
                edit.SetEntityTransform(green, new(new Vector3(100, 0, 4), Quaternion.Identity, 1.5f));
            }),
            new(8, edit => edit.CaptureImage(image => expected = image)),
        ]), "--no-taa", "--no-vsm");
        SceneImages.AssertColorRegion(hidden!, 28, 28, 8, 8, 0, 186, 0);
        SceneImages.AssertColorRegion(recovered!, 29, 29, 6, 6, 186, 0, 0);
        SceneImages.AssertEqual(expected!, recovered!);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task DeletingAllGeometryShowsTheDeclaredBackground(string backend)
    {
        SceneImage? image = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(8, edit =>
            {
                foreach (Entity entity in edit.MeshEntities) edit.DeleteEntity(entity);
                edit.CaptureImage(value => image = value);
            }),
        ]), "--no-taa", "--no-vsm");
        SceneImages.AssertColorRegion(image!, 0, 0, 64, 64, 0, 0, 0);
    }
}
