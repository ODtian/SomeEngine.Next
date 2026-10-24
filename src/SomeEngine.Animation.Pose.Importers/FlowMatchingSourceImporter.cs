using Microsoft.Extensions.DependencyInjection;
using SomeEngine.Assets;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Animation.Pose.Importers;

/// <summary>Reads one trained JSON export, associates its model asset and completes the selected block producers.</summary>
public sealed class FlowMatchingSourceImporter(IServiceCollection importers) : IAssetSourceImporter<FlowMatchingModel>
{
    public static readonly Guid SourceImporterId = new("7a51098e-1a42-4482-954f-a75fc44fce1c");
    public Guid Id => SourceImporterId;
    public IReadOnlyList<string> SourceExtensions { get; } = [".flow.json"];

    public async ValueTask<FlowMatchingModel> ReadAsync(string sourcePath, CancellationToken cancellationToken)
        => FlowMatchingModel.ReadJson(await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false));

    public async ValueTask ImportAsync(FlowMatchingModel source, ImportContext context, CancellationToken cancellationToken)
    {
        ServiceProvider provider = importers.BuildServiceProvider();
        context.Hold(provider);
        AsyncServiceScope scope = provider.CreateAsyncScope();
        context.Hold(scope);
        var capabilities = scope.ServiceProvider.GetServices<AssetImporterCapability>()
            .Where(static capability => capability.SourceType == typeof(FlowMatchingModel))
            .ToDictionary(static capability => capability.Id);
        foreach (SourceImporterSettings selection in context.Selections)
            if (selection.Enabled)
                context.EnableImporter<FlowMatchingModel, AnimationModelAsset>(
                    capabilities[selection.ImporterId].Bind<FlowMatchingModel>(scope.ServiceProvider, selection.Settings));
        const string key = "model";
        Guid id = context.GetAssetId(key);
        context.AddAsset(source, new AnimationModelAsset(id) { Name = Path.GetFileNameWithoutExtension(context.SourcePath) },
            key, Path.ChangeExtension(context.SourcePath, ".animation-model.asset"));
        await context.GetAssetAsync<FlowMatchingModel, AnimationModelAsset>(source, cancellationToken).ConfigureAwait(false);
    }
}
