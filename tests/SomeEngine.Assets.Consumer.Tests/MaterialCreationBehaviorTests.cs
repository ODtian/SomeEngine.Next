using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Consumer.Tests;

public sealed class MaterialCreationBehaviorTests
{
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

            using ConsumerCoatingMaterial material = await opened.CreateAsync<ConsumerCoatingMaterial>(loader);

            Assert.Equal("Copper coating", material.Name);
            Assert.Equal(0.7f, material.Thickness);
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
