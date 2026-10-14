using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Assets.AotSmoke;

/// <summary>Executes generated registrations, settings codecs and cooperating block imports in the native executable.</summary>
internal static class ImportingSmoke
{
    internal static async Task RunAsync(string directory)
    {
        var services = new ServiceCollection();
        ImportingCapabilities.AddAssetCapabilities(services);
        int registrations = services.Count;
        ImportingCapabilities.AddAssetCapabilities(services);
        Check(registrations == services.Count, "Repeated registration changed the service collection.");
        Guid assetId = Guid.NewGuid();
        string path = Path.Combine(directory, "imported.mesh.asset");
        await using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }))
        {
            AssetImporterCapability[] capabilities = provider.GetServices<AssetImporterCapability>()
                .Where(value => value.SourceType == typeof(SmokeImportSource)).ToArray();
            Check(capabilities.Length == 3 && capabilities.All(value => value.SettingsType == typeof(SmokeImportSettings)),
                "Available importer descriptions lost a source, settings type or unselected alternative.");
            Check(capabilities.All(value => !string.IsNullOrWhiteSpace(value.ImplementationVersion)), "Missing implementation versions.");
            Check(ImportingCapabilities.BlockCapabilities.Any(value => value.BlockType == typeof(TextureBlock)
                && value.Id == TextureBlock.BlockTypeId), "A referenced block was not discovered.");
            await using (var scope = provider.CreateAsyncScope())
            {
                var number = scope.ServiceProvider.GetRequiredService<SmokeNumberImporter>();
                var label = scope.ServiceProvider.GetRequiredService<SmokeLabelImporter>();
                var alternative = scope.ServiceProvider.GetRequiredService<SmokeAlternativeImporter>();
                Check(ReferenceEquals(number, scope.ServiceProvider.GetServices<IAssetImporter<SmokeImportSource, SmokeImportSettings>>()
                    .Single(value => value is SmokeNumberImporter)), "Typed registration created another importer.");
                await using (var another = provider.CreateAsyncScope())
                    Check(!ReferenceEquals(number, another.ServiceProvider.GetRequiredService<SmokeNumberImporter>()), "Different scopes share an importer.");
                SourceImporterSettings numberSelection = ImportingCapabilities.SelectImporter(ImportingCapabilities.ImporterKind.SmokeNumberImporter);
                Guid numberId = numberSelection.ImporterId;
                Check(ImportingCapabilities.ImporterChoices.Single(value => value.Value == ImportingCapabilities.ImporterKind.SmokeNumberImporter).Id == numberId,
                    "Generated GUI choices disagree with the enum API.");
                Guid labelId = capabilities.Single(value => value.ImporterType == typeof(SmokeLabelImporter)).Id;
                Guid alternativeId = capabilities.Single(value => value.ImporterType == typeof(SmokeAlternativeImporter)).Id;
                var source = new SmokeImportSource(7, "native");
                JsonElement settings = JsonSerializer.SerializeToElement(new SmokeImportSettings(3, ":v1"), ImportingJson.Default.SmokeImportSettings);
                var imports = capabilities.ToDictionary(value => value.Id);
                await using (var context = CreateContext())
                {
                    // The dependent conversion is deliberately selected first.
                    context.EnableImporter<SmokeImportSource, MeshAsset>(imports[labelId].Bind<SmokeImportSource>(scope.ServiceProvider, settings));
                    context.EnableImporter<SmokeImportSource, MeshAsset>(imports[numberId].Bind<SmokeImportSource>(scope.ServiceProvider, settings));
                    Task<MeshAsset>[] requests = Enumerable.Range(0, 24)
                        .Select(_ => context.GetAssetAsync<SmokeImportSource, MeshAsset>(source).AsTask()).ToArray();
                    MeshAsset[] results = await Task.WhenAll(requests).ConfigureAwait(false);
                    MeshAsset target = results[0];
                    Check(results.All(value => ReferenceEquals(target, value)), "Concurrent requests returned different assets.");
                    Check(ReferenceEquals(target, await context.GetAssetAsync<SmokeImportSource, MeshAsset>(source)), "A later request rebuilt the asset.");
                    Check(number.Runs == 1 && label.Runs == 1 && alternative.Runs == 0, "Repeated conversion or unwanted producer execution.");
                    Check(target.Id == assetId && target.GetBlock<SmokeNumberBlock>().Value == 21
                        && target.GetBlock<SmokeLabelBlock>().Text == "native:v1:21", "The completed asset lacks its typed blocks.");
                    Check(ImportingCapabilities.ContainsBlock(target, ImportingCapabilities.BlockKind.SmokeNumberBlock)
                        && ReferenceEquals(target.GetBlock<SmokeNumberBlock>(), await ImportingCapabilities.GetBlockAsync(target,
                            ImportingCapabilities.BlockKind.SmokeNumberBlock)), "The generated block enum did not select its typed data.");
                    using var session = new SmokeSessionBlock(Guid.NewGuid());
                    target.SetBlock(session);
                    AssetEntry saved = await target.SaveAsync(path).ConfigureAwait(false);
                    Check(saved.Dependencies.Count == 0 && target.ContainsBlock<SmokeSessionBlock>(), "Process-only data changed persistent references.");
                }
                await using (var conflict = CreateContext())
                {
                    conflict.EnableImporter<SmokeImportSource, MeshAsset>(imports[numberId].Bind<SmokeImportSource>(scope.ServiceProvider, settings));
                    conflict.EnableImporter<SmokeImportSource, MeshAsset>(imports[alternativeId].Bind<SmokeImportSource>(scope.ServiceProvider, settings));
                    bool rejected = false;
                    try { await conflict.GetAssetAsync<SmokeImportSource, MeshAsset>(source); }
                    catch (InvalidOperationException) { rejected = true; }
                    Check(rejected && number.Runs == 1 && alternative.Runs == 0, "Competing producers ran before rejection.");
                }
            }
        }
        using (var reopened = await MeshAsset.OpenAsync(path).ConfigureAwait(false))
        {
            Check(reopened.Id == assetId && !reopened.ContainsBlock<SmokeSessionBlock>(), "The stored identity or process-only content is incorrect.");
            SmokeNumberBlock number = await reopened.GetBlockAsync<SmokeNumberBlock>();
            SmokeLabelBlock label = await reopened.GetBlockAsync<SmokeLabelBlock>();
            Check(number.Value == 21 && label.Text == "native:v1:21", "Native typed block reading failed.");
            for (int index = 0; index < 10000; index++)
                Check(ReferenceEquals(number, reopened.GetBlock<SmokeNumberBlock>()), "Typed lookup returned different data.");
        }
        CheckCatalogReplacement(Path.Combine(directory, "目录"), assetId);
        Console.WriteLine("assets-import-nativeaot:ok generated-di=ok enum-api=ok gui-choices=ok referenced-block=ok descriptions=ok settings=ok scopes=ok dependency-dedup=ok producer-conflict=ok same-mesh=ok persistence=ok process-block=ok catalog-replace=ok managed-data=ok");

        ImportContext CreateContext()
        {
            var metadata = new SourceMeta { SourceGuid = SourceGuid.New(), ElementIds = new() { ["mesh"] = assetId } };
            var context = new ImportContext(directory, "", metadata);
            context.ConfigureAssets<SmokeImportSource, MeshAsset>(new SmokeMeshSource());
            return context;
        }
    }

    private static void CheckCatalogReplacement(string directory, Guid assetId)
    {
        var catalog = new AssetManifest();
        catalog.AddAsset(assetId, "before", "mesh.asset", MeshAsset.FileType, MeshAsset.SchemaHash);
        catalog.Save(directory);
        using var preceding = new FileStream(Path.Combine(directory, AssetManifest.CatalogFileName),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        catalog.AddAsset(assetId, "after", "mesh.asset", MeshAsset.FileType, MeshAsset.SchemaHash);
        catalog.Save(directory);
        Check(AssetManifest.Load(directory).TryGetAsset(assetId, out AssetEntry current) && current.Name == "after",
            "The native executable could not replace and reopen a catalog while another reader held the preceding file.");
        using var previous = JsonDocument.Parse(preceding);
        Check(previous.RootElement.GetProperty("assets").GetProperty("assets")[0].GetProperty("name").GetString() == "before",
            "Replacing the catalog invalidated data held by an existing reader.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

[AssetCapabilities]
public static partial class ImportingCapabilities;

[AssetBlock("SmokeNumberBlock"), BinarySerializable, BinaryFields(nameof(Value))]
public sealed partial class SmokeNumberBlock : AssetBlock
{
    public partial int Value { get; set; }
}

[AssetBlock("SmokeLabelBlock"), BinarySerializable, BinaryFields(nameof(Text))]
public sealed partial class SmokeLabelBlock : AssetBlock
{
    public partial string Text { get; set; }
}

[AssetBlock("SomeEngine.Tests.SmokeSession")]
public sealed partial class SmokeSessionBlock : AssetBlock
{
    public AssetId<Asset> Reference { get; }
    public SmokeSessionBlock(Guid reference) => Reference = new(reference);
    private SmokeSessionBlock(SmokeSessionBlock source, object? syncRoot = null) : base(source, syncRoot) => Reference = source.Reference;
    public override AssetBlock Duplicate() => new SmokeSessionBlock(this);
    public override AssetBlock Duplicate(object syncRoot) => new SmokeSessionBlock(this, syncRoot);
    public override IEnumerable<Guid> References() => [Reference.Value];
}

public sealed record SmokeImportSource(int Value, string Label);
public sealed record SmokeImportSettings(int Multiplier, string Suffix);

public sealed class SmokeImportSettingsCodec : IAssetImporterSettings<SmokeImportSettings>
{
    public SmokeImportSettings DefaultSettings => new(1, "");
    public JsonTypeInfo<SmokeImportSettings> TypeInfo => ImportingJson.Default.SmokeImportSettings;
}

[JsonSerializable(typeof(SmokeImportSettings))]
internal partial class ImportingJson : JsonSerializerContext;

public sealed class SmokeMeshSource : AssetSource<SmokeImportSource, MeshAsset>
{
    public override string GetElementKey(SmokeImportSource source) => "mesh";
    public override MeshAsset CreateAsset(SmokeImportSource source, Guid id) => new(id);
    public override string GetOutputPath(SmokeImportSource source, Guid id) => "imported.mesh.asset";
}

[AssetImporter("c701741a-6fc8-4c5a-87ed-06dc60729e69", typeof(SmokeNumberBlock), SettingsProvider = typeof(SmokeImportSettingsCodec))]
public sealed class SmokeNumberImporter : IAssetImporter<SmokeImportSource, SmokeImportSettings>
{
    public int Runs { get; private set; }
    public async ValueTask ImportAsync(SmokeImportSource source, SmokeImportSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        Runs++;
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        var block = new SmokeNumberBlock() { Value = checked(source.Value * settings.Multiplier) };
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
    }
}

[AssetImporter("79d14e52-32d8-43ae-91b8-8f450c574c35", typeof(SmokeLabelBlock), SettingsProvider = typeof(SmokeImportSettingsCodec))]
public sealed class SmokeLabelImporter
    : IAssetImporter<SmokeImportSource, SmokeImportSettings>
{
    public int Runs { get; private set; }
    public async ValueTask ImportAsync(SmokeImportSource source, SmokeImportSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        Runs++;
        SmokeNumberBlock numberBlock = await context.GetBlockAsync<SmokeNumberBlock>(output.Target, cancellationToken).ConfigureAwait(false);
        var block = new SmokeLabelBlock()
        {
            Text = source.Label + settings.Suffix + ":" + numberBlock.Value.ToString(CultureInfo.InvariantCulture),
        };
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
    }
}

[AssetImporter("9b9e4b20-996d-4bb9-9734-83dd9c261090", typeof(SmokeNumberBlock), SettingsProvider = typeof(SmokeImportSettingsCodec))]
public sealed class SmokeAlternativeImporter : IAssetImporter<SmokeImportSource, SmokeImportSettings>
{
    public int Runs { get; private set; }
    public ValueTask ImportAsync(SmokeImportSource source, SmokeImportSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        Runs++;
        throw new InvalidOperationException("This alternative is available but must not execute without selection.");
    }
}
