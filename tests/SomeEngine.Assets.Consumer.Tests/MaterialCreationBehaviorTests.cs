using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Consumer.Tests;

public sealed class MaterialCreationBehaviorTests
{
    [Fact]
    public void ConcreteUpdatePreservesTheConsumerMaterialIdentity()
    {
        var material = new ConsumerCoatingMaterial { Name = "before", Thickness = 0.4f };
        var replacement = new ConsumerCoatingMaterial { Name = "after", Thickness = 0.8f };
        long identity = material.MaterialId;
        ulong version = material.Revision;

        ApplyReplacement(material, replacement);

        Assert.Equal(identity, material.MaterialId);
        Assert.Equal(version + 1, material.Revision);
        Assert.Equal("after", material.Name);
        Assert.Equal(0.8f, material.Thickness);
        Assert.Equal(0.2f, material.Roughness);
    }

    private static void ApplyReplacement<TMaterial>(TMaterial material, TMaterial replacement)
        where TMaterial : Material, IMaterial<TMaterial> => TMaterial.Update(material, replacement);

    [Fact]
    public async Task OpenedAssetCreatesTheChosenConsumerMaterialWithOverridesAndFieldDefaults()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = new AssetProject(directory, []);
            using var asset = new MaterialAsset { Name = "Copper coating" };
            asset.GetBlock<MaterialBlock>().SetParameter(
                new(nameof(ConsumerCoatingMaterial.Thickness), MaterialParameterKind.Float) { Float = 0.7f });
            Guid id = project.RegisterAsset(await asset.SaveAsync(Path.Combine(directory, "coating.material.asset")));
            await using var loader = new AssetLoader(project.CreateStorage());
            using MaterialAsset opened = await MaterialAsset.OpenAsync(loader, new(id));

            ConsumerCoatingMaterial material = await opened.CreateAsync<ConsumerCoatingMaterial>(loader,
                new Dictionary<AssetId<ShaderAsset>, Shader>(), new Dictionary<AssetId<TextureAsset>, Texture>());

            Assert.Equal("Copper coating", material.Name);
            Assert.Equal(0.7f, material.Thickness);
            Assert.Equal(0.2f, material.Roughness);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AssetUpdateRestoresConsumerFieldDefaultsAndPreservesIdentity()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var files = new AssetLoader(new LooseAssetStorage(directory, new AssetManifest()));
            using var asset = new MaterialAsset { Name = "Copper coating" };
            var material = new ConsumerCoatingMaterial { Thickness = 0.7f, Roughness = 0.8f };
            long identity = material.MaterialId;
            ulong revision = material.Revision;

            await asset.UpdateAsync(material, files, new Dictionary<AssetId<ShaderAsset>, Shader>(),
                new Dictionary<AssetId<TextureAsset>, Texture>());

            Assert.Equal(identity, material.MaterialId);
            Assert.Equal(revision + 1, material.Revision);
            Assert.Equal("Copper coating", material.Name);
            Assert.Equal(0f, material.Thickness);
            Assert.Equal(0.2f, material.Roughness);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed partial class ConsumerCoatingMaterial : Material
{
    public partial float Thickness { get; set; }
    public partial float Roughness { get; set; } = 0.2f;
}
