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
        IGlobalSession slang,
        SessionDesc sessionDescription,
        ReadOnlyMemory<SpecializationArg> specializations = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(slang);
        if (sessionDescription.Targets is not { Length: > 0 })
            throw new ArgumentException("Shader loading requires at least one Slang target.", nameof(sessionDescription));

        var shaders = new Dictionary<Guid, Shader>();
        foreach (Guid id in references.Where(static id => id != Guid.Empty).Distinct())
        {
            await using ShaderAsset file = await ShaderAsset.OpenAsync(
                loader,
                new AssetId<ShaderAsset>(id),
                cancellationToken).ConfigureAwait(false);
            Shader shader;
            if (file.ContainsBlock<ShaderBlock>())
            {
                ShaderBlock code = await file.GetBlockAsync<ShaderBlock>(cancellationToken)
                    .ConfigureAwait(false);
                ShaderSourceBlock source = await file.GetBlockAsync<ShaderSourceBlock>(cancellationToken)
                    .ConfigureAwait(false);
                shader = await code.CreateAsync(
                    source,
                    slang,
                    sessionDescription,
                    file.Name,
                    specializations,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ShaderSourceBlock source = await file.GetBlockAsync<ShaderSourceBlock>(cancellationToken)
                    .ConfigureAwait(false);
                shader = await source.CreateAsync(
                    slang,
                    sessionDescription,
                    file.Name,
                    specializations,
                    cancellationToken).ConfigureAwait(false);
            }
            shaders.Add(id, shader);
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
