using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Resources;
Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Runtime.Tests;

// #128: WARP complete Cluster frames time out; image scenarios require hardware.
[Trait("Category", "Hardware")]
[Collection("Rendering content")]
public sealed class MaterialRenderingTests(RenderingContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task AddedMaterialMatchesStartingWithTheFinalMaterial(string backend)
    {
        var blue = new UnlitMaterial { Name = "blue", BaseColorTint = new(0, 0, 1, 1) };
        SceneImage? before = null, changed = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(0, edit =>
            {
                Entity red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                var material = (UnlitMaterial)edit.GetMaterial(red);
                blue.Shader = material.Shader;
                blue.AlbedoMap = material.AlbedoMap;
            }),
            new(7, edit => edit.CaptureImage(image => before = image)),
            new(8, edit => { edit.ReplaceMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"), blue); }),
            new(11, edit => edit.CaptureImage(image => changed = image)),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(0, edit =>
            {
                Entity red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                var material = (UnlitMaterial)edit.GetMaterial(red);
                blue.Shader = material.Shader;
                blue.AlbedoMap = material.AlbedoMap;
                edit.ReplaceMaterial(red, blue);
            }),
            new(11, edit => edit.CaptureImage(image => expected = image)),
        ]), "--no-taa", "--no-vsm");
        Assert.NotEqual(before!.Pixels.ToArray(), changed!.Pixels.ToArray());
        SceneImages.AssertEqual(expected!, changed);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task RemovedMaterialMatchesStartingWithTheRemainingMaterial(string backend)
    {
        SceneImage? before = null, changed = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(7, edit => edit.CaptureImage(image => before = image)),
            new(8, edit => { Entity red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                edit.ReplaceMaterial(red, edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere"))); }),
            new(11, edit => edit.CaptureImage(image => changed = image)),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(0, edit =>
            {
                Entity red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                Entity green = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere");
                edit.ReplaceMaterial(red, edit.GetMaterial(green));
            }),
            new(11, edit => edit.CaptureImage(image => expected = image)),
        ]), "--no-taa", "--no-vsm");
        Assert.NotEqual(before!.Pixels.ToArray(), changed!.Pixels.ToArray());
        SceneImages.AssertEqual(expected!, changed);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task ReplacedMaterialMatchesStartingWithTheReplacement(string backend)
    {
        var blue = new UnlitMaterial { Name = "blue", BaseColorTint = new(0, 0, 1, 1) };
        var purple = new UnlitMaterial { Name = "purple", BaseColorTint = new(1, 0, 1, 1) };
        SceneImage? before = null, changed = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(0, edit =>
            {
                Entity red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                var material = (UnlitMaterial)edit.GetMaterial(red);
                blue.Shader = purple.Shader = material.Shader;
                blue.AlbedoMap = purple.AlbedoMap = material.AlbedoMap;
                edit.ReplaceMaterial(red, blue);
            }),
            new(7, edit => edit.CaptureImage(image => before = image)),
            new(8, edit => { edit.ReplaceMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "blue"), purple); }),
            new(11, edit => edit.CaptureImage(image => changed = image)),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(0, edit =>
            {
                Entity red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                var material = (UnlitMaterial)edit.GetMaterial(red);
                blue.Shader = purple.Shader = material.Shader;
                blue.AlbedoMap = purple.AlbedoMap = material.AlbedoMap;
                edit.ReplaceMaterial(red, purple);
            }),
            new(11, edit => edit.CaptureImage(image => expected = image)),
        ]), "--no-taa", "--no-vsm");
        Assert.NotEqual(before!.Pixels.ToArray(), changed!.Pixels.ToArray());
        SceneImages.AssertEqual(expected!, changed);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task AddedTessellatedMaterialMatchesStartingWithThatMaterial(string backend)
    {
        SceneImage? before = null, changed = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Tessellation, 12,
        [
            new(7, edit => edit.CaptureImage(image => before = image)),
            new(8, edit => { Entity red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                edit.ReplaceMaterial(red, edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere"))); }),
            new(11, edit => edit.CaptureImage(image => changed = image)),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Tessellation, 12,
        [
            new(0, edit =>
            {
                Entity red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                Entity green = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere");
                edit.ReplaceMaterial(red, edit.GetMaterial(green));
            }),
            new(11, edit => edit.CaptureImage(image => expected = image)),
        ]), "--no-taa", "--no-vsm");
        Assert.NotEqual(before!.Pixels.ToArray(), changed!.Pixels.ToArray());
        SceneImages.AssertEqual(expected!, changed);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task RemovedTessellatedMaterialMatchesStartingWithLinearMaterials(string backend)
    {
        SceneImage? before = null, changed = null, expected = null;
        UnlitMaterial? linear = null;
        Entity first = default, second = default;
        await SceneImages.RunAsync(backend, new SceneScript(content.Tessellation, 12,
        [
            new(0, edit =>
            {
                Entity red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                Entity green = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere");
                var material = (UnlitMaterial)edit.GetMaterial(red);
                linear = material;
                first = red; second = green;
                edit.ReplaceMaterial(red, edit.GetMaterial(green));
            }),
            new(7, edit => edit.CaptureImage(image => before = image)),
            new(8, edit => { edit.ReplaceMaterial(first, linear!); edit.ReplaceMaterial(second, linear!); }),
            new(11, edit => edit.CaptureImage(image => changed = image)),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Tessellation, 12,
        [
            new(0, edit =>
            {
                Entity red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                Entity green = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere");
                var material = (UnlitMaterial)edit.GetMaterial(red);
                linear = material;
                first = red; second = green;
                edit.ReplaceMaterial(green, material);
            }),
            new(11, edit => edit.CaptureImage(image => expected = image)),
        ]), "--no-taa", "--no-vsm");
        Assert.NotEqual(before!.Pixels.ToArray(), changed!.Pixels.ToArray());
        SceneImages.AssertEqual(expected!, changed);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task ManyMaterialsSharingOneTextureMatchStartingWithAllMaterials(string backend)
    {
        SceneImage? before = null, changed = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.ShaderReplacement, 16,
        [
            new(7, edit => edit.CaptureImage(image => before = image)),
            new(8, edit =>
            {
                Entity source = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                var template = (UnlitMaterial)edit.GetMaterial(source);
                Material other = edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere"));
                for (int index = 0; index < 64; index++)
                {
                    var material = new UnlitMaterial { Shader = index % 2 == 0 ? template.Shader : other.Shader, AlbedoMap = template.AlbedoMap,
                        BaseColorTint = new((index % 3) * .4f + .1f, .2f, .8f, 1),
                        MaterialSampler = template.MaterialSampler with { MipLodBias = index * .125f } };
                    Entity entity = edit.AddEntity(source, new(new Vector3((index % 8 - 3.5f) * .45f,
                        (index / 8 - 3.5f) * .45f, 4), Quaternion.Identity, .15f));
                    edit.ReplaceMaterial(entity, material);
                }
            }),
            new(15, edit => edit.CaptureImage(image => changed = image)),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.ShaderReplacement, 16,
        [
            new(0, edit =>
            {
                Entity source = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                var template = (UnlitMaterial)edit.GetMaterial(source);
                Material other = edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere"));
                for (int index = 0; index < 64; index++)
                {
                    var material = new UnlitMaterial { Shader = index % 2 == 0 ? template.Shader : other.Shader, AlbedoMap = template.AlbedoMap,
                        BaseColorTint = new((index % 3) * .4f + .1f, .2f, .8f, 1),
                        MaterialSampler = template.MaterialSampler with { MipLodBias = index * .125f } };
                    Entity entity = edit.AddEntity(source, new(new Vector3((index % 8 - 3.5f) * .45f,
                        (index / 8 - 3.5f) * .45f, 4), Quaternion.Identity, .15f));
                    edit.ReplaceMaterial(entity, material);
                }
            }),
            new(15, edit => edit.CaptureImage(image => expected = image)),
        ]), "--no-taa", "--no-vsm");
        SceneImages.AssertEqual(expected!, changed!);
        Assert.NotEqual(before!.Pixels.ToArray(), changed!.Pixels.ToArray());
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task UpdatingASharedTextureMatchesStartingWithTheNewPixels(string backend)
    {
        Texture shared = CreateColorTexture(255);
        Texture directTexture = CreateColorTexture(128);
        Task? update = null;
        SceneImage? before = null, changed = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 16,
        [
            new(0, edit => { foreach (Entity entity in edit.MeshEntities) ((UnlitMaterial)edit.GetMaterial(entity)).AlbedoMap = shared; }),
            new(7, edit => edit.CaptureImage(image => before = image)),
            new(8, _ => update = shared.UpdateAsync(CreateColorTexture(128)).AsTask()),
            new(15, edit => edit.CaptureImage(image => changed = image)),
        ]), "--no-taa", "--no-vsm");
        await update!;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 16,
        [
            new(0, edit => { foreach (Entity entity in edit.MeshEntities) ((UnlitMaterial)edit.GetMaterial(entity)).AlbedoMap = directTexture; }),
            new(15, edit => edit.CaptureImage(image => expected = image)),
        ]), "--no-taa", "--no-vsm");
        Assert.NotEqual(before!.Pixels.ToArray(), changed!.Pixels.ToArray());
        SceneImages.AssertEqual(expected!, changed);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task ReplacingTheLastMaterialTextureReferenceMatchesStartingWithReplacement(string backend)
    {
        Texture original = CreateColorTexture(255);
        Texture replacement = CreateColorTexture(128);
        SceneImage? before = null, changed = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(0, edit => ((UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"))).AlbedoMap = original),
            new(7, edit => edit.CaptureImage(image => before = image)),
            new(8, edit => ((UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"))).AlbedoMap = replacement),
            new(11, edit => edit.CaptureImage(image => changed = image)),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(0, edit => ((UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"))).AlbedoMap = replacement),
            new(11, edit => edit.CaptureImage(image => expected = image)),
        ]), "--no-taa", "--no-vsm");
        Assert.NotEqual(before!.Pixels.ToArray(), changed!.Pixels.ToArray());
        SceneImages.AssertEqual(expected!, changed);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task ShaderContentReplacementMatchesStartingWithTheReplacementShader(string backend)
    {
        SceneImage? before = null, changed = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.ShaderReplacement, 12,
        [
            new(7, edit => edit.CaptureImage(image => before = image)),
            new(8, edit =>
            {
                Material red = edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"));
                red.Shader = edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere")).Shader;
            }),
            new(11, edit => edit.CaptureImage(image => changed = image)),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.ShaderReplacement, 12,
        [
            new(0, edit =>
            {
                Material red = edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"));
                red.Shader = edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere")).Shader;
            }),
            new(11, edit => edit.CaptureImage(image => expected = image)),
        ]), "--no-taa", "--no-vsm");
        Assert.NotEqual(before!.Pixels.ToArray(), changed!.Pixels.ToArray());
        SceneImages.AssertEqual(expected!, changed);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task MaterialColorChangesMatchStartingWithThatColor(string backend)
    {
        SceneImage? before = null, changed = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(7, edit => edit.CaptureImage(image => before = image)),
            new(8, edit =>
            {
                var material = (UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"));
                material.BaseColorTint = new(.1f, .8f, .2f, 1);
            }),
            new(11, edit => edit.CaptureImage(image => changed = image)),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(0, edit =>
            {
                var material = (UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"));
                material.BaseColorTint = new(.1f, .8f, .2f, 1);
            }),
            new(11, edit => edit.CaptureImage(image => expected = image)),
        ]), "--no-taa", "--no-vsm");
        Assert.NotEqual(before!.Pixels.ToArray(), changed!.Pixels.ToArray());
        SceneImages.AssertEqual(expected!, changed);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task SamplerReplacementMatchesStartingWithThatSampler(string backend)
    {
        SceneImage? changed = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(8, edit =>
            {
                var material = (UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"));
                material.MaterialSampler = new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                    AddressType.Repeat, AddressType.Repeat, AddressType.Repeat);
            }),
            new(11, edit => edit.CaptureImage(image => changed = image)),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(0, edit =>
            {
                var material = (UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"));
                material.MaterialSampler = new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                    AddressType.Repeat, AddressType.Repeat, AddressType.Repeat);
            }),
            new(11, edit => edit.CaptureImage(image => expected = image)),
        ]), "--no-taa", "--no-vsm");
        SceneImages.AssertEqual(expected!, changed!);
        SceneImages.AssertColorRegion(changed!, 37, 29, 6, 6, 186, 0, 0);
    }

    private static Texture CreateColorTexture(byte value) => Texture.Create(
        new TextureDesc(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
            TextureUsages.Sampled | TextureUsages.CopyDestination),
        [new TexturePixels(new TextureMipTile { Width = 1, Height = 1, RowPitch = 4, SlicePitch = 4 },
            new byte[] { value, value, value, 255 })], TextureViewDimension.Texture2D);
}
