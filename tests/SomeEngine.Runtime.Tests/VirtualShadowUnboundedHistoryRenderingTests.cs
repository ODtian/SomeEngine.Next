using System.Numerics;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Runtime.Tests;

[Trait("Category","Hardware")]
[Collection("Rendering content")]
public sealed class VirtualShadowUnboundedHistoryRenderingTests(RenderingContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task MovingDefaultSpheresPreserveAnIndependentUnboundedCastersLightHistory(string backend)
    {
        var images = new List<SceneLinearImage>();
        Entity[] spheres = [];
        Vector3[] previousPositions = [];
        Entity staticObject = default;
        TransformQvvs staticTransform = default;
        int movingFrames = 0;
        var application = new ApplicationScene(await content.UnboundedHistory)
        {
            Initialize = frame =>
            {
                staticObject = frame.Scene.MeshEntities.Single(entity =>
                    frame.Scene.GetMaterial(entity).Name == "Static unbounded Phong history");
                staticTransform = frame.World.Read<WorldTransform>(staticObject).Qvvs;
                spheres = frame.Scene.MeshEntities.Where(entity => entity != staticObject).ToArray();
                Assert.Equal(1024,spheres.Length);
                previousPositions = spheres.Select(entity => frame.World.Read<WorldTransform>(entity).Qvvs.Position).ToArray();
            },
            FrameCompleted = frame =>
            {
                Assert.Equal(staticTransform,frame.World.Read<WorldTransform>(staticObject).Qvvs);
                for (int index = 0; index < spheres.Length; index++)
                {
                    Vector3 position = frame.World.Read<WorldTransform>(spheres[index]).Qvvs.Position;
                    if (frame.Index != 0) Assert.NotEqual(previousPositions[index],position);
                    previousPositions[index] = position;
                }
                if (frame.Index != 0) movingFrames++;
            },
        };
        await SceneImages.RunAsync(backend,new SceneScript(application,24,
            Enumerable.Range(12,12).Select(index => new SceneAction(index,edit => edit.CaptureLinearImage(images.Add))))
        {
            ShadowSettings = new VirtualShadowMapSettings
            {
                VirtualResolution = 2048, AtlasSize = 2048, MaxPages = 512, MaxShadowLights = 8,
                DirectionalClipmapLevels = 8, DirectionalFirstClipmapLevel = 0,
                DirectionalFirstCoarseClipmapLevel = 6, DirectionalLastCoarseClipmapLevel = 7,
                DynamicResolutionEnabled = false, DirectionalDynamicReceiverRefresh = false,
            },
        },"--no-mesh-shaders","--hardware-raster","--no-taa","--vsm",
            "--dynamic-scene","--synchronous-frames","--fixed-step","0.0166667");

        Assert.Equal(23,movingFrames);
        Assert.Equal(Enumerable.Range(12,12),images.Select(image => image.Frame.Frame));
        foreach (SceneLinearImage image in images)
        {
            var view = image.Frame.Quality!.Shadows!.Views.Single(value =>
                value.LightKind == VirtualShadowLightKind.Spot && value.Projection.LightPosition == new Vector3(40,4,20));
            Assert.True(view.HistoryValid);
            Assert.Contains(view.Mips,mip => mip.MappedPages > 0);
        }
    }
}
