using System.Numerics;
using Game.Content;
using SomeEngine.Core.Math;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Transparency content")]
public sealed class TransparencyIndexTests(TransparencyContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task XyzCandidatesMatchFullScanWhileModelsAndCameraMoveAcrossCells(string backend)
    {
        var images = new List<SceneLinearImage[]>();
        foreach (var (index, triangles, references) in new[]
        {
            ("full", 4096u, 4096u), ("xyz", 4096u, 4096u), ("xyz", 1u, 1u),
        })
        {
            var frames = new SceneLinearImage[2];
            await SceneImages.RunAsync(backend, new SceneScript(content.DirectoryPath, 16,
            [
                new(0, edit =>
                {
                    foreach (var entity in edit.MeshEntities)
                    {
                        var material = (UnlitMaterial)edit.GetMaterial(entity);
                        material.State = material.State with { Surface = SurfaceMode.Translucent };
                        material.BaseColorTint = material.Name == "red sphere" ? new(1, 0, 0, .5f) : new(0, 1, 0, .5f);
                    }
                }),
                new(7, edit => edit.CaptureLinearImage(image => frames[0] = image)),
                new(8, edit =>
                {
                    foreach (var entity in edit.MeshEntities)
                        edit.SetEntityTransform(entity, new TransformQvvs(new Vector3(.25f, 0, 9), Quaternion.Identity));
                    edit.SetCameraTransform(new Vector3(.1f, 0, -2), Quaternion.Identity);
                }),
                new(15, edit => edit.CaptureLinearImage(image => frames[1] = image)),
            ]) { BlendTriangleCapacity = triangles, BlendReferenceCapacity = references, WaitForFrames = false },
                "--blend-index=" + index, "--hardware-raster", "--no-taa", "--no-vsm");
            images.Add(frames);
        }
        SceneLinearImage reference = images[0][0];
        Matrix4x4.Invert(reference.Frame.View * reference.Frame.Projection, out Matrix4x4 inverse);
        Half[] expected = reference.Pixels.ToArray();
        for (int y = 25; y < 39; y++)
        for (int x = 23; x < 41; x++)
        {
            Vector4 world = Vector4.Transform(new Vector4((x + .5f) / 32 - 1, 1 - (y + .5f) / 32, .5f, 1), inverse);
            bool redNear = world.X / world.W < 0;
            int pixel = (y * 64 + x) * 4;
            expected[pixel] = (Half)(redNear ? .5f : .25f);
            expected[pixel + 1] = (Half)(redNear ? .25f : .5f);
            expected[pixel + 2] = (Half)0;
            expected[pixel + 3] = (Half)1;
        }
        SceneImages.AssertLinearEqual(reference with { Pixels = expected }, reference, .001f);
        foreach (SceneLinearImage[] indexed in images.Skip(1))
            for (int frame = 0; frame < 2; frame++)
                SceneImages.AssertLinearEqual(images[0][frame], indexed[frame], .001f);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task StaticBlendCellsAreReusedUntilInstanceMembershipChanges(string backend)
    {
        var measurements = new List<ClusterBlendMetrics>();
        await SceneImages.RunAsync(backend, new SceneScript(content.DirectoryPath, 16,
        [
            new(0, edit =>
            {
                foreach (var entity in edit.MeshEntities)
                {
                    Material material = edit.GetMaterial(entity);
                    material.State = material.State with { Surface = SurfaceMode.Translucent };
                }
            }),
            new(8, edit => edit.AddEntity(edit.MeshEntities[0], new TransformQvvs(new Vector3(.25f, 0, 9), Quaternion.Identity))),
        ]) { BlendFrameCompleted = measurements.Add, WaitForFrames = false },
            "--measure-blending", "--hardware-raster", "--no-taa", "--no-vsm", "--static-scene");

        Assert.Equal(16, measurements.Count);
        ClusterBlendMetrics before = measurements.Single(frame => frame.SimulationFrame == 7);
        ClusterBlendMetrics after = measurements.Single(frame => frame.SimulationFrame == 15);
        Assert.True(before.ReusedIndex);
        Assert.False(measurements.Single(frame => frame.SimulationFrame == 8).ReusedIndex);
        Assert.True(after.ReusedIndex);
        Assert.Equal(0u, before.DecalVolumes);
        Assert.Equal(0u, after.DecalVolumes);
        Assert.True(before.ReusedDecalIndex);
        Assert.True(after.ReusedDecalIndex);
        Assert.Equal(0, before.ProjectionIndexMs);
        Assert.Equal(0, after.ProjectionIndexMs);
        Assert.Equal(0, before.DecalIndexMs);
        Assert.Equal(0, after.DecalIndexMs);
        Assert.Equal(0, before.CoverageReceiverMs);
        Assert.Equal(0, after.CoverageReceiverMs);
        Assert.True(before.MaterialCompositeMs > 0);
        Assert.True(after.MaterialCompositeMs > 0);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task BlendMeasurementsFollowDecalChangesAcrossSubmittedFrames(string backend)
    {
        var measurements = new List<ClusterBlendMetrics>();
        using var red = new StandardMaterial { BaseColorTint = new(1, 0, 0, .5f) };
        using var green = new StandardMaterial { BaseColorTint = new(0, 1, 0, .5f) };
        TransformQvvs volume = new(new Vector3(0, 0, 7), Quaternion.Identity) { Stretch = new(4, 4, 4) };
        await SceneImages.RunAsync(backend, new SceneScript(content.DirectoryPath, 16,
        [
            new(0, edit =>
            {
                var source = (UnlitMaterial)edit.GetMaterial(edit.MeshEntities[0]);
                red.Shader = green.Shader = source.Shader;
                red.AlbedoMap = green.AlbedoMap = source.AlbedoMap;
                edit.AddMaterialDecal(red, volume);
            }),
            new(8, edit => edit.AddMaterialDecal(green, volume, 1)),
        ]) { BlendReferenceCapacity = 1, BlendFrameCompleted = measurements.Add, WaitForFrames = false },
            "--measure-blending", "--hardware-raster", "--no-taa", "--no-vsm", "--static-scene");

        Assert.Equal(16, measurements.Count);
        ClusterBlendMetrics before = measurements.Single(frame => frame.SimulationFrame == 7);
        ClusterBlendMetrics changed = measurements.Single(frame => frame.SimulationFrame == 8);
        Assert.Equal(1u, before.DecalVolumes);
        Assert.Equal(2u, changed.DecalVolumes);
        Assert.True(changed.DecalReferences > 0);
        Assert.InRange(changed.DecalResidentReferences, 0u, 1u);
        Assert.True(changed.DecalUsedBytes > before.DecalUsedBytes);
        Assert.True(changed.DecalCapacityBytes > before.DecalCapacityBytes);
        Assert.True(changed.CapacityBytes > before.CapacityBytes);
        Assert.True(changed.DecalIndexMs > 0);
        Assert.True(changed.OpaqueMaterialMs > 0);
        Assert.False(changed.ReusedDecalIndex);
        Assert.Equal(0, before.DecalIndexMs);
        Assert.Equal(0, before.ProjectionIndexMs);
        Assert.Equal(0, before.CoverageReceiverMs);
        Assert.True(before.OpaqueMaterialMs > 0);
        Assert.True(measurements.Single(frame => frame.SimulationFrame == 15).ReusedDecalIndex);
        Assert.Equal(0, measurements.Single(frame => frame.SimulationFrame == 15).DecalIndexMs);
    }
}
