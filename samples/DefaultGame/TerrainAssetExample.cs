using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace DefaultGame;

/// <summary>Public save/reopen flow; the application resolves saved material references into ordinary materials.</summary>
public static class TerrainAssetExample
{
    public static async ValueTask SaveAsync(string path, Terrain terrain,
        IReadOnlyList<AssetId<MaterialAsset>> materials, CancellationToken cancellationToken = default)
    {
        AssetId<MaterialAsset>[] references = materials.ToArray();
        TerrainCpuData values = await terrain.CaptureCpuAsync().ConfigureAwait(false);
        using var asset = new TerrainAsset { Name = "Editable terrain" };
        await asset.UpdateAsync(values, references, cancellationToken).ConfigureAwait(false);
        await asset.SaveAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The material callback supplies application-owned objects for the returned placement bindings.</summary>
    public static async ValueTask<(Terrain Terrain, Material[] Materials)> OpenAsync(string path, TerrainEditing editing,
        Func<AssetId<MaterialAsset>, CancellationToken, ValueTask<Material>> loadMaterial,
        CancellationToken cancellationToken = default)
    {
        using TerrainAsset asset = await TerrainAsset.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        Terrain terrain = await asset.CreateTerrainAsync(editing, cancellationToken).ConfigureAwait(false);
        TerrainMaterialBlock bindings = await asset.GetBlockAsync<TerrainMaterialBlock>(cancellationToken).ConfigureAwait(false);
        var materials = new Material[bindings.Materials.Count];
        for (int index = 0; index < materials.Length; index++)
            materials[index] = await loadMaterial(bindings.Materials[index], cancellationToken).ConfigureAwait(false);
        return (terrain, materials);
    }
}
