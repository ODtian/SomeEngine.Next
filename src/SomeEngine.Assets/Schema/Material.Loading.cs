namespace SomeEngine.Assets.Schema;

public partial class Material
{
    internal static async ValueTask<Material> LoadAssetAsync(
        AssetLoadContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SomeEngine.Serialization.Containers.BinaryDocument<Material> document = await context
            .OpenAsync<Material>()
            .ConfigureAwait(false);
        Material asset = document.Root;
        ValidateDependencies(asset);

        if (asset.Passes is { Count: > 0 } passes)
        {
            foreach (PassEntry pass in passes)
            {
                AssetGuid shaderGuid = ShaderRef.Require(
                    pass.Shader,
                    "Material",
                    "Pass.Shader");
                _ = await context.LoadDependencyAsync(new AssetId<Shader>(shaderGuid)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        if (asset.Textures is { Count: > 0 } textures)
        {
            foreach (TextureBinding binding in textures)
            {
                AssetGuid textureGuid = RequireGuid(binding.TextureGuid, "Texture.TextureGuid");
                _ = await context.LoadDependencyAsync(new AssetId<Texture>(textureGuid)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return asset;
    }

    private static void ValidateDependencies(Material asset)
    {
        if (asset.Passes is { Count: > 0 } passes)
        {
            for (int index = 0; index < passes.Count; index++)
            {
                PassEntry? pass = passes[index];
                if (pass is null)
                    throw new InvalidDataException($"Material pass {index} is null.");
                _ = ShaderRef.Require(
                    pass.Shader,
                    "Material",
                    $"Passes[{index}].Shader");
            }
        }

        if (asset.Textures is { Count: > 0 } textures)
        {
            for (int index = 0; index < textures.Count; index++)
            {
                TextureBinding? binding = textures[index];
                if (binding is null)
                    throw new InvalidDataException($"Material texture {index} is null.");
                _ = RequireGuid(binding.TextureGuid, $"Textures[{index}].TextureGuid");
            }
        }
    }

    internal static global::SomeEngine.Assets.AssetGuid RequireGuid(string? value, string field)
    {
        if (!global::SomeEngine.Assets.AssetGuid.TryParse(
                value,
                out global::SomeEngine.Assets.AssetGuid guid)
            || guid.IsEmpty)
            throw new InvalidDataException($"Material field '{field}' has an invalid asset GUID '{value}'.");
        return guid;
    }
}

public partial class MaterialInstance
{
    internal static async ValueTask<MaterialInstance> LoadAssetAsync(
        AssetLoadContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SomeEngine.Serialization.Containers.BinaryDocument<MaterialInstance> document = await context
            .OpenAsync<MaterialInstance>()
            .ConfigureAwait(false);
        MaterialInstance asset = document.Root;
        AssetGuid parentGuid = Material.RequireGuid(asset.ParentGuid, nameof(asset.ParentGuid));

        if (asset.Overrides is { Count: > 0 } overridesToValidate)
        {
            for (int index = 0; index < overridesToValidate.Count; index++)
            {
                ParamOverride? item = overridesToValidate[index];
                if (item is null)
                    throw new InvalidDataException($"Material override {index} is null.");
                _ = Material.RequireGuid(
                    item.TextureGuid,
                    $"Overrides[{index}].TextureGuid");
            }
        }

        _ = await context.LoadDependencyAsync(new AssetId<Material>(parentGuid)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (asset.Overrides is { Count: > 0 } overrides)
        {
            for (int index = 0; index < overrides.Count; index++)
            {
                ParamOverride? item = overrides[index];
                if (item is null)
                    throw new InvalidDataException($"Material override {index} is null.");
                AssetGuid textureGuid = Material.RequireGuid(
                    item.TextureGuid,
                    $"Overrides[{index}].TextureGuid");
                _ = await context.LoadDependencyAsync(new AssetId<Texture>(textureGuid)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return asset;
    }
}
