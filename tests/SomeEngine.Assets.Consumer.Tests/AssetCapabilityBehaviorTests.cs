using Microsoft.Extensions.DependencyInjection;
using SomeEngine.Assets.Importing;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Consumer.Tests;

public sealed class AssetCapabilityBehaviorTests
{
    [Fact]
    public void GeneratedImportersShareOneInstanceWithinEachScope()
    {
        var services = new ServiceCollection();
        Assert.Same(services, ConsumerAssetCapabilities.AddAssetCapabilities(services));
        int count = services.Count;
        ConsumerAssetCapabilities.AddAssetCapabilities(services);
        Assert.Equal(count, services.Count);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var importer = first.ServiceProvider.GetRequiredService<CapabilityImporter>();
        Assert.Same(importer, first.ServiceProvider.GetServices<IAssetImporter<CapabilitySource, CapabilitySettings>>()
            .Single(value => value is CapabilityImporter));
        Assert.NotSame(importer, second.ServiceProvider.GetRequiredService<CapabilityImporter>());
    }

    [Fact]
    public void EditorCapabilitiesDescribeAlternativesWithoutEnablingOrRejectingThem()
    {
        var services = ConsumerAssetCapabilities.AddAssetCapabilities(new ServiceCollection());
        using var provider = services.BuildServiceProvider();
        AssetImporterCapability[] alternatives = provider.GetServices<AssetImporterCapability>()
            .Where(capability => capability.SourceType == typeof(CapabilitySource)).ToArray();
        Assert.Equal(2, alternatives.Length);
        Assert.All(alternatives, capability =>
        {
            Assert.Equal(typeof(CapabilitySettings), capability.SettingsType);
            Assert.Equal(CapabilityData.BlockTypeId, Assert.Single(capability.ProducedBlocks).Id);
        });
        Assert.Contains(ConsumerAssetCapabilities.BlockCapabilities, capability =>
            capability.Id == CapabilityData.BlockTypeId && capability.BlockType == typeof(CapabilityData));
        Assert.Equal(new Guid("90b3ba7e-813b-4ad7-8c24-90c2f45eb356"),
            Assert.Single(alternatives, capability => capability.ImporterType == typeof(CapabilityImporter)).Id);
    }

}

[AssetCapabilities]
public static partial class ConsumerAssetCapabilities;

[AssetBlock("SomeEngine.Assets.Consumer.Tests.CapabilityData"), BinarySerializable, BinaryFields(nameof(Value))]
public sealed partial class CapabilityData : AssetBlock
{
    public partial int Value { get; set; }
}

public sealed class CapabilitySource;
public sealed record CapabilitySettings(int Scale);

[AssetImporter("90b3ba7e-813b-4ad7-8c24-90c2f45eb356", typeof(CapabilityData))]
public sealed class CapabilityImporter : IAssetImporter<CapabilitySource, CapabilitySettings>
{
    public ValueTask ImportAsync(CapabilitySource source, CapabilitySettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

[AssetImporter("25922b24-cb7c-4cde-9491-8d05538b196b", typeof(CapabilityData))]
public sealed class AlternativeCapabilityImporter : IAssetImporter<CapabilitySource, CapabilitySettings>
{
    public ValueTask ImportAsync(CapabilitySource source, CapabilitySettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
