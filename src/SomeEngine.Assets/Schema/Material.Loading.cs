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
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (asset.Passes is { Count: > 0 } passes)
        {
            foreach (PassEntry pass in passes)
            {
                AssetGuid shaderGuid = ShaderRef.Require(
                    pass.Shader,
                    "Material",
                    "Pass.Shader");
                pass.Shader!.Asset = await context
                    .LoadDependencyAsync(new AssetId<Shader>(shaderGuid))
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        if (asset.Textures is { Count: > 0 } textures)
        {
            foreach (TextureBinding binding in textures)
            {
                AssetGuid textureGuid = RequireGuid(binding.TextureGuid, "Texture.TextureGuid");
                Texture texture = await context.LoadDependencyAsync(new AssetId<Texture>(textureGuid)).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(binding.Name))
                    throw new InvalidDataException("A material texture binding must have a name.");
                values.Add(binding.Name, texture);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        foreach (ScalarParam scalar in asset.Scalars ?? [])
        {
            if (string.IsNullOrWhiteSpace(scalar.Name) || scalar.Value is not { } value)
                throw new InvalidDataException("A material scalar binding must have a name and value.");
            values.Add(scalar.Name, value.Kind switch
            {
                ParamValue.ItemKind.FloatVal => value.FloatVal.V,
                ParamValue.ItemKind.IntVal => value.IntVal.V,
                ParamValue.ItemKind.BoolVal => value.BoolVal.V,
                ParamValue.ItemKind.Vec2Val => new System.Numerics.Vector2(
                    value.Vec2Val.X,
                    value.Vec2Val.Y),
                ParamValue.ItemKind.Vec3Val => new System.Numerics.Vector3(
                    value.Vec3Val.X,
                    value.Vec3Val.Y,
                    value.Vec3Val.Z),
                ParamValue.ItemKind.Vec4Val => new System.Numerics.Vector4(
                    value.Vec4Val.X,
                    value.Vec4Val.Y,
                    value.Vec4Val.Z,
                    value.Vec4Val.W),
                _ => throw new InvalidDataException("A material scalar binding has an unsupported value."),
            });
        }
        asset.ResolveWeakSlots(values);

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
