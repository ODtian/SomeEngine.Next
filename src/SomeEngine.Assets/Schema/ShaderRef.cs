using SlangShaderSharp;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Identifies a named Slang entry point in one semantic shader asset.</summary>
[BinarySerializable(BinaryCompatibility.ExactSchema), TextSerializable]
public sealed partial record ShaderRef
{
    internal EntryPointReflection ResolveEntry(
        IReadOnlyDictionary<Guid, Shader> shaders,
        int targetIndex = 0)
    {
        Shader shader = shaders[Asset.Value];
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

    internal static async ValueTask<Dictionary<Guid, Shader>> LoadAsync(
        IEnumerable<Guid> references,
        AssetLoader loader,
        CancellationToken cancellationToken = default)
    {
        var shaders = new Dictionary<Guid, Shader>();
        foreach (Guid id in references.Where(static id => id != Guid.Empty).Distinct())
        {
            shaders.Add(id, await loader.GetShaderAsync(new(id), cancellationToken).ConfigureAwait(false));
        }
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
