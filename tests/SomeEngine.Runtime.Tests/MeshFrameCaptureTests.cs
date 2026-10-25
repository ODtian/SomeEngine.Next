using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class MeshFrameCaptureTests(SceneContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    public async Task LinearCaptureRetainsCopiedMeshAssetsAndTransformsAfterFurtherEdits(string backend)
    {
        Entity source = default;
        Guid sourceAsset = default;
        SceneLinearImage? before = null, copies = null, after = null;
        SceneMeshTransform[] expectedCopies = [];
        TransformQvvs firstTransform = new(new Vector3(0, 0, 6), Quaternion.Identity);
        TransformQvvs lastTransform = new(new Vector3(2, 1, 8), Quaternion.Identity, .75f);
        var script = new SceneScript(content.DirectoryPath, 6,
        [
            new(0, edit =>
            {
                Entity[] entities = edit.MeshEntities.ToArray();
                Assert.NotEmpty(entities);
                source = entities[0];
                foreach (Entity entity in entities.Skip(1)) edit.DeleteEntity(entity);
                edit.SetEntityTransform(source, firstTransform);
                edit.CaptureLinearImage(image =>
                {
                    before = image;
                    sourceAsset = Assert.Single(image.Frame.Meshes).MeshAsset;
                });
            }),
            new(2, edit =>
            {
                for (int index = 1; index < 4; index++) edit.AddEntity(source, firstTransform);
                Entity[] entities = edit.MeshEntities.ToArray();
                expectedCopies = new SceneMeshTransform[entities.Length];
                for (int index = 0; index < entities.Length; index++)
                {
                    TransformQvvs transform = new(new Vector3(index, index * .5f, 6), Quaternion.Identity);
                    edit.SetEntityTransform(entities[index], transform);
                    expectedCopies[index] = new(sourceAsset, transform.ToMatrix());
                }
            }),
            new(3, edit => edit.CaptureLinearImage(image => copies = image)),
            new(4, edit =>
            {
                foreach (Entity entity in edit.MeshEntities)
                    if (!entity.Equals(source)) edit.DeleteEntity(entity);
                edit.SetEntityTransform(source, lastTransform);
            }),
            new(5, edit => edit.CaptureLinearImage(image => after = image)),
        ]);
        await SceneImages.RunAsync(backend, script, "--no-taa", "--no-vsm");

        Assert.NotNull(before);
        Assert.NotNull(copies);
        Assert.NotNull(after);
        Assert.NotEqual(Guid.Empty, sourceAsset);
        Assert.Equal(new SceneMeshTransform(sourceAsset, firstTransform.ToMatrix()), Assert.Single(before.Frame.Meshes));
        Assert.Equal(4, expectedCopies.Length);
        Assert.Equal(expectedCopies, copies.Frame.Meshes);
        Assert.Equal(new SceneMeshTransform(sourceAsset, lastTransform.ToMatrix()), Assert.Single(after.Frame.Meshes));
        Assert.Equal(0, before.Frame.Frame);
        Assert.Equal(3, copies.Frame.Frame);
        Assert.Equal(5, after.Frame.Frame);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task LinearCaptureHasNoMeshInputsAfterAllInstancesAreDeleted(string backend)
    {
        SceneLinearImage? captured = null;
        var script = new SceneScript(content.DirectoryPath, 4,
        [
            new(2, edit =>
            {
                foreach (Entity entity in edit.MeshEntities) edit.DeleteEntity(entity);
            }),
            new(3, edit => edit.CaptureLinearImage(image => captured = image)),
        ]);
        await SceneImages.RunAsync(backend, script, "--no-taa", "--no-vsm");

        Assert.NotNull(captured);
        Assert.Equal(3, captured.Frame.Frame);
        Assert.Empty(captured.Frame.Meshes);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task CanceledLinearCaptureDoesNotPreventCapturingTheNextFrame(string backend)
    {
        Entity source = default;
        var events = new List<string>();
        var images = new List<SceneLinearImage>();
        TransformQvvs canceledTransform = new(new Vector3(0, 0, 6), Quaternion.Identity);
        TransformQvvs nextTransform = new(new Vector3(2, 1, 8), Quaternion.Identity, .75f);

        var script = new SceneScript(content.DirectoryPath, 2,
        [
            new(0, edit =>
            {
                Entity[] entities = edit.MeshEntities.ToArray();
                Assert.NotEmpty(entities);
                source = entities[0];
                foreach (Entity entity in entities.Skip(1)) edit.DeleteEntity(entity);
                edit.SetEntityTransform(source, canceledTransform);
                edit.CaptureLinearImage(_ => events.Add("canceled capture"));
                edit.CancelFrame();
                events.Add("cancel action");
            }),
            new(1, edit =>
            {
                edit.SetEntityTransform(source, nextTransform);
                events.Add("next action");
                edit.CaptureLinearImage(image =>
                {
                    images.Add(image);
                    events.Add("next capture");
                });
            }),
        ]);

        ApplicationResult result = await Application.RunAsync(
            ["--hardware-adapter", "--graphics-backend", backend, "--no-taa", "--no-vsm"],
            script);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(1, result.CompletedFrames);
        Assert.Equal(["cancel action", "next action", "next capture"], events);

        SceneLinearImage captured = Assert.Single(images);
        Assert.Equal(1, captured.Frame.Frame);
        Assert.Equal(0u, captured.Frame.SubmittedFrame);
        SceneMeshTransform mesh = Assert.Single(captured.Frame.Meshes);
        Assert.NotEqual(Guid.Empty, mesh.MeshAsset);
        Assert.Equal(nextTransform.ToMatrix(), mesh.Transform);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task LinearCapturesRetainTheirCompletedShadowQualityAcrossCanceledFrames(string backend)
    {
        var events = new List<string>();
        SceneLinearImage? first = null, resumed = null;
        SceneRenderingQuality? retainedFirstQuality = null;
        ulong firstRenderingFrame = 0;
        VirtualShadowViewQuality[] firstViews = [];
        VirtualShadowMipQuality[][] firstMips = [];
        var script = new SceneScript(content.DirectoryPath, 4,
        [
            new(0, edit =>
            {
                Entity[] lights = edit.PointLights.ToArray();
                Assert.NotEmpty(lights);
                edit.SetDirectionalLight(lights[0], new(Vector3.UnitZ, Vector3.One, 3));
            }),
            new(1, edit => edit.CaptureLinearImage(image =>
            {
                first = image;
                retainedFirstQuality = Assert.IsType<SceneRenderingQuality>(image.Frame.Quality);
                Assert.NotNull(retainedFirstQuality.Shadows);
                firstRenderingFrame = retainedFirstQuality.Shadows.RenderingFrame;
                firstViews = retainedFirstQuality.Shadows.Views.ToArray();
                firstMips = firstViews.Select(view => view.Mips.ToArray()).ToArray();
                events.Add("first capture");
            })),
            new(2, edit =>
            {
                edit.CaptureLinearImage(_ => events.Add("canceled capture"));
                edit.CancelFrame();
                events.Add("cancel action");
            }),
            new(3, edit =>
            {
                events.Add("next action");
                edit.CaptureLinearImage(image =>
                {
                    resumed = image;
                    events.Add("next capture");
                });
            }),
        ])
        {
            WaitForFrames = false,
            ShadowSettings = new VirtualShadowMapSettings { DynamicResolutionEnabled = true },
        };

        ApplicationResult result = await Application.RunAsync(
            ["--hardware-adapter", "--graphics-backend", backend, "--no-taa", "--vsm"],
            script);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(3, result.CompletedFrames);
        Assert.Equal(["first capture", "cancel action", "next action", "next capture"], events);
        Assert.NotNull(first);
        Assert.NotNull(resumed);
        Assert.Equal(1, first.Frame.Frame);
        Assert.Equal(3, resumed.Frame.Frame);
        Assert.Equal(1u, first.Frame.SubmittedFrame);
        Assert.Equal(2u, resumed.Frame.SubmittedFrame);
        SceneRenderingQuality before = Assert.IsType<SceneRenderingQuality>(first.Frame.Quality);
        SceneRenderingQuality after = Assert.IsType<SceneRenderingQuality>(resumed.Frame.Quality);
        Assert.NotNull(before.Shadows);
        Assert.NotNull(after.Shadows);
        Assert.Equal(firstRenderingFrame + 1, after.Shadows.RenderingFrame);
        Assert.Equal(ClusterWorkOverflow.None, after.Shadows.WorkOverflow);
        Assert.NotEmpty(after.Shadows.Views);
        Assert.All(after.Shadows.Views, view =>
        {
            Assert.True(float.IsFinite(view.AppliedResolutionLodBias));
            Assert.NotEmpty(view.Mips);
            Assert.All(view.Mips, mip => Assert.True(mip.Resolution > 0));
        });
        Assert.Same(retainedFirstQuality, before);
        Assert.Equal(firstRenderingFrame, before.Shadows.RenderingFrame);
        Assert.Equal(firstViews, before.Shadows.Views);
        for (int index = 0; index < firstMips.Length; index++)
            Assert.Equal(firstMips[index], before.Shadows.Views[index].Mips);
    }
}
