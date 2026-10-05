using System.Numerics;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Runtime.Tests;

// #128: WARP complete Cluster frames time out; image scenarios require hardware.
[Trait("Category", "Hardware")]
[Collection("Rendering content")]
public sealed class GeometryRenderingTests(RenderingContentFixture content)
{
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
        await SceneImages.RunAsync(backend, new SceneScript(content.Streaming, 16,
        [
            new(3, edit => edit.CaptureImage(images.Add)),
            new(7, edit => edit.CaptureImage(images.Add)),
            new(15, edit => edit.CaptureImage(images.Add)),
        ]) { GeometryMemoryBytes = 262144, RasterEdgePixels = rasterEdgePixels }, "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Streaming, 16,
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
