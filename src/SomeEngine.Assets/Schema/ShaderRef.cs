using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Identifies one executable entry point in one shader asset.</summary>
[BinarySerializable(BinaryCompatibility.ExactSchema), TextSerializable]
public sealed partial record ShaderRef
{
    internal ShaderEntry Resolve(IReadOnlyDictionary<Guid, Shader> shaders)
        => new(shaders[Asset.Value], EntryPoint!, Stage);

    internal static async ValueTask<Dictionary<Guid, Shader>> LoadAsync(IEnumerable<Guid> references,
        AssetLoader loader, CancellationToken cancellationToken)
    {
        var shaders = new Dictionary<Guid, Shader>();
        try
        {
            foreach (Guid id in references.Where(static id => id != Guid.Empty).Distinct())
            {
                await using ShaderAsset file = await ShaderAsset.OpenAsync(loader,
                    new AssetId<ShaderAsset>(id), cancellationToken).ConfigureAwait(false);
                ShaderBlock code = await file.GetBlockAsync<ShaderBlock>(cancellationToken).ConfigureAwait(false);
                shaders.Add(id, await code.CreateAsync(file.Name, cancellationToken).ConfigureAwait(false));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return shaders;
        }
        catch
        {
            foreach (Shader shader in shaders.Values) shader.Dispose();
            throw;
        }
    }

    public AssetId<ShaderAsset> Asset { get; init; }

    public string? EntryPoint { get; init; }

    public ShaderStage Stage { get; init; }

    internal static Guid Require(
        ShaderRef? shader,
        string owner,
        string field,
        ShaderStage? expectedStage = null)
    {
        if (shader is null)
            throw new InvalidDataException($"{owner} field '{field}' is missing.");
        if (shader.Asset.IsEmpty)
            throw new InvalidDataException($"{owner} field '{field}.Asset' is missing.");
        if (string.IsNullOrWhiteSpace(shader.EntryPoint))
        {
            throw new InvalidDataException(
                $"{owner} field '{field}.EntryPoint' is missing.");
        }
        if (!Enum.IsDefined(shader.Stage))
        {
            throw new InvalidDataException(
                $"{owner} field '{field}.Stage' has invalid value '{shader.Stage}'.");
        }
        if (expectedStage.HasValue && shader.Stage != expectedStage.Value)
        {
            throw new InvalidDataException(
                $"{owner} field '{field}' must reference a {expectedStage.Value} shader entry.");
        }
        return shader.Asset.Value;
    }
}
