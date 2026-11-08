using SlangShaderSharp;
using SomeEngine.Core.Collections;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Identifies a named Slang entry point in one semantic shader asset.</summary>
[BinarySerializable(BinaryCompatibility.ExactSchema), TextSerializable]
public sealed partial record ShaderRef
{
    internal EntryPointReflection ResolveEntry(
        IReadOnlyDictionary<AssetId<ShaderAsset>, Shader> shaders,
        int targetIndex = 0)
    {
        Shader shader = shaders[Asset];
        if (!shader.TryGetSlangEntry(
                targetIndex,
                EntryPoint!,
                out EntryPointReflection entry))
        {
            throw new InvalidDataException(
                $"Shader '{shader.Name}' has no Slang entry point named '{EntryPoint}'.");
        }
        return entry;
    }

    public static async ValueTask<Dictionary<AssetId<ShaderAsset>, Shader>> LoadAsync(
        IEnumerable<Guid> references,
        AssetLoader files,
        WeakValues<AssetResourceKey<ShaderAsset>, Shader> values,
        IGlobalSession slang, SessionDesc settings,
        CancellationToken cancellationToken = default)
    {
        AssetId<ShaderAsset>[] ids = references.Where(static id => id != Guid.Empty).Distinct()
            .Select(static id => new AssetId<ShaderAsset>(id)).ToArray();
        Shader[] loaded = await Task.WhenAll(ids.Select(id =>
            ShaderAsset.GetAsync(files, id, values, slang, settings, cancellationToken).AsTask())).ConfigureAwait(false);
        var shaders = new Dictionary<AssetId<ShaderAsset>, Shader>(ids.Length);
        for (int index = 0; index < ids.Length; index++) shaders.Add(ids[index], loaded[index]);
        cancellationToken.ThrowIfCancellationRequested();
        return shaders;
    }

    public AssetId<ShaderAsset> Asset { get; init; }
    public string? EntryPoint { get; init; }

    internal static Guid Require(ShaderRef? shader, string owner, string field)
    {
        if (shader is null)
            throw new InvalidDataException($"{owner} field '{field}' is missing.");
        if (shader.Asset.IsEmpty)
            throw new InvalidDataException($"{owner} field '{field}.Asset' is missing.");
        if (string.IsNullOrWhiteSpace(shader.EntryPoint))
            throw new InvalidDataException($"{owner} field '{field}.EntryPoint' is missing.");
        return shader.Asset.Value;
    }
}
