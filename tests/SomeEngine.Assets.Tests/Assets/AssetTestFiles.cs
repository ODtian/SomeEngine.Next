using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets;

internal static class AssetTestFiles
{
    internal static async ValueTask<BinaryDocument<TFields>> OpenAsync<T, TFields>(string path,
        CancellationToken cancellationToken = default)
        where T : class where TFields : class, IBinaryContract<TFields>
    {
        await using Asset<T> file = await AssetLoader.OpenFileAsync<T>(path, cancellationToken: cancellationToken);
        return file.OpenDocumentGenerated<TFields>()!;
    }

    internal static async ValueTask<BinaryDocument<TFields>> OpenAsync<T, TFields>(IAssetStorage storage,
        AssetEntry entry, CancellationToken cancellationToken = default)
        where T : class where TFields : class, IBinaryContract<TFields>
    {
        await using var loader = new AssetLoader(storage);
        await using Asset<T> file = await loader.OpenAsync(new AssetId<T>(entry.AssetGuid), cancellationToken);
        return file.OpenDocumentGenerated<TFields>()!;
    }

    internal static async ValueTask<TFields> ReadAsync<T, TFields>(string path,
        CancellationToken cancellationToken = default)
        where T : class where TFields : class, IBinaryContract<TFields>
    {
        await using Asset<T> file = await AssetLoader.OpenFileAsync<T>(path, cancellationToken: cancellationToken);
        return file.GetFieldsGenerated<TFields>();
    }
}
