using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class MaterialTextureReferenceRenderingTests(SceneContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    public async Task LastTextureReferenceReplacementSurvivesACanceledFrame(string backend)
    {
        Texture blue = ColorTexture(0, 0, 255);
        Texture red = ColorTexture(255, 0, 0);
        UnlitMaterial material = null!;
        SceneLinearImage? before = null, replaced = null, restored = null;
        var captures = new List<string>();
        var script = new SceneScript(new ApplicationScene(content.DirectoryPath)
            { RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register }, 8,
        [
            new(0, edit =>
            {
                Entity[] entities = edit.MeshEntities.ToArray();
                Assert.NotEmpty(entities);
                Entity source = entities[0];
                foreach (Entity entity in entities.Skip(1)) edit.DeleteEntity(entity);
                // This fixture requires the generated Unlit sphere scene.
                material = Assert.IsType<UnlitMaterial>(edit.GetMaterial(source));
                material.BaseColorTint = Vector4.One;
                material.AlbedoMap = blue;
                edit.SetEntityTransform(source, new TransformQvvs(new Vector3(0, 0, 6), Quaternion.Identity));
                edit.SetCameraTransform(Vector3.Zero, Quaternion.Identity);
            }),
            new(2, edit => edit.CaptureLinearImage(image => before = image)),
            new(3, edit =>
            {
                // No other material or light has ever referenced this generated texture.
                material.AlbedoMap = red;
                edit.CaptureLinearImage(_ => captures.Add("canceled"));
                edit.CancelFrame();
            }),
            new(4, edit => edit.CaptureLinearImage(image =>
            {
                replaced = image;
                captures.Add("replaced");
            })),
            new(6, _ => material.AlbedoMap = blue),
            new(7, edit => edit.CaptureLinearImage(image =>
            {
                restored = image;
                captures.Add("restored");
            })),
        ]) { UseCameraHistory = false };

        await SceneImages.RunAsync(backend, script, "--no-taa", "--no-vsm");

        Assert.Equal(["replaced", "restored"], captures);
        Assert.NotNull(before);
        Assert.NotNull(replaced);
        Assert.NotNull(restored);
        SceneImages.AssertLinearNotEqual(before, replaced);
        SceneImages.AssertLinearEqual(before, restored, 0.0001f);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task ClearingTheLastSampledTextureReferenceRestoresTheMissingTextureImage(string backend)
    {
        Texture red = ColorTexture(255, 0, 0);
        UnlitMaterial material = null!;
        SceneLinearImage? missing = null, textured = null, cleared = null;
        var script = new SceneScript(new ApplicationScene(content.DirectoryPath)
            { RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register }, 8,
        [
            new(0, edit =>
            {
                Entity[] entities = edit.MeshEntities.ToArray();
                Assert.NotEmpty(entities);
                Entity source = entities[0];
                foreach (Entity entity in entities.Skip(1)) edit.DeleteEntity(entity);
                // This fixture requires the generated Unlit sphere scene.
                material = Assert.IsType<UnlitMaterial>(edit.GetMaterial(source));
                material.BaseColorTint = Vector4.One;
                material.AlbedoMap = null;
                edit.SetEntityTransform(source, new TransformQvvs(new Vector3(0, 0, 6), Quaternion.Identity));
                edit.SetCameraTransform(Vector3.Zero, Quaternion.Identity);
            }),
            new(2, edit => edit.CaptureLinearImage(image => missing = image)),
            new(3, _ => material.AlbedoMap = red),
            new(4, edit => edit.CaptureLinearImage(image => textured = image)),
            new(6, _ => material.AlbedoMap = null),
            new(7, edit => edit.CaptureLinearImage(image => cleared = image)),
        ]) { UseCameraHistory = false };

        await SceneImages.RunAsync(backend, script, "--no-taa", "--no-vsm");

        Assert.NotNull(missing);
        Assert.NotNull(textured);
        Assert.NotNull(cleared);
        SceneImages.AssertLinearNotEqual(missing, textured);
        SceneImages.AssertLinearEqual(missing, cleared, 0.0001f);
    }

    private static Texture ColorTexture(byte red, byte green, byte blue) => Texture.Create(
        new TextureDesc(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
            TextureUsages.Sampled | TextureUsages.CopyDestination),
        [new TexturePixels(new TextureMipTile { Width = 1, Height = 1, RowPitch = 4, SlicePitch = 4 },
            new byte[] { red, green, blue, 255 })], TextureViewDimension.Texture2D);
}
