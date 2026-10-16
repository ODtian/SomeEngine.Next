using System.Numerics;
using Game.Content;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Ordered content")]
public sealed class OrderedRenderingTests(OrderedContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    public async Task ReadOnlyStencilMaskKeepsTheHardwareBackground(string backend)
    {
        SceneLinearImage? hardware = null, masked = null;
        Entity red = default;
        await SceneImages.RunAsync(backend, new SceneScript(content.DirectoryPath, 16,
        [
            new(0, edit =>
            {
                foreach (Entity entity in edit.MeshEntities)
                    if (edit.GetMaterial(entity) is UnlitMaterial { Name: "red sphere" } material)
                    {
                        red = entity;
                        material.BaseColorTint = new(1, 0, 0, 1);
                        edit.SetEntityTransform(red, new(new Vector3(20, 0, 6), Quaternion.Identity));
                    }
            }),
            new(7, edit => edit.CaptureLinearImage(image => hardware = image)),
            new(8, edit =>
            {
                UnlitMaterial material = (UnlitMaterial)edit.GetMaterial(red);
                var face = new StencilFaceState(StencilOperation.Keep, StencilOperation.Keep,
                    StencilOperation.Keep, CompareOperation.Equal);
                material.State = material.State with
                {
                    Ordered = true, StencilRef = 1,
                    DepthStencil = new(StencilTest: true, StencilWriteMask: 0, Front: face, Back: face),
                };
                edit.SetEntityTransform(red, new(new Vector3(0, 0, 6), Quaternion.Identity));
            }),
            new(15, edit => edit.CaptureLinearImage(image => masked = image)),
        ]) { WaitForFrames = false }, "--hardware-raster", "--shading-path", "forward", "--no-taa", "--no-vsm");
        SceneImages.AssertLinearEqual(hardware!, masked!, .001f);
    }
}

[CollectionDefinition("Ordered content")]
public sealed class OrderedContentCollection : ICollectionFixture<OrderedContentFixture>;

public sealed class OrderedContentFixture : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-Ordered-{Guid.NewGuid():N}");
    public string DirectoryPath => Path.Combine(_directory, "panels");

    public async Task InitializeAsync()
    {
        string spheres = Path.Combine(_directory, "spheres");
        string? cooked = Environment.GetEnvironmentVariable("SOMEENGINE_TEST_COOKED_CONTENT");
        if (cooked is null)
        {
            string? engine = AppContext.BaseDirectory;
            while (engine is not null && !File.Exists(Path.Combine(engine, "SomeEngine.slnx"))) engine = Path.GetDirectoryName(engine);
            await SomeEngine.SceneContent.SceneContent.GenerateSpheresAsync(spheres, engine!);
        }
        else await SomeEngine.SceneContent.SceneContent.GenerateSpheresFromCookedAsync(spheres, cooked);
        await SomeEngine.SceneContent.BehaviorScenes.CreateIntersectingPanelsAsync(spheres, DirectoryPath);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }
}
