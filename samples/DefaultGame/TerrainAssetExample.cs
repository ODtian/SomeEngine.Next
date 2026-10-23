using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace DefaultGame;

/// <summary>Public save/reopen flow; the application resolves saved material references into ordinary materials.</summary>
public static class TerrainAssetExample
{
    /// <summary>Edits a selected height region, preserves material bindings, and saves the resulting terrain.</summary>
    public static async ValueTask<HeightFieldRead> EditHeightRegionAsync(string sourcePath, string targetPath,
        TerrainEditing editing, int fieldId, HeightRegion range, float delta,
        CancellationToken cancellationToken = default)
    {
        using TerrainAsset source = await TerrainAsset.OpenAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        using Terrain terrain = await source.CreateTerrainAsync(editing, cancellationToken).ConfigureAwait(false);
        using TerrainMaterialBlock materials = (await source.GetBlockAsync<TerrainMaterialBlock>(cancellationToken).ConfigureAwait(false)).Duplicate();
        string name = source.Name;
        source.Dispose();
        HeightField field = terrain.HeightFields[fieldId];
        field.EditCpu(new HeightDeltaBrush(range, delta));
        HeightFieldRead changed = await field.ReadCpuAsync(range).ConfigureAwait(false);
        using var output = new TerrainAsset { Name = name };
        await output.UpdateAsync(await terrain.CaptureCpuAsync().ConfigureAwait(false), materials, cancellationToken).ConfigureAwait(false);
        await output.SaveAsync(targetPath, cancellationToken).ConfigureAwait(false);
        return changed;
    }

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
        try
        {
            TerrainMaterialBlock bindings = await asset.GetBlockAsync<TerrainMaterialBlock>(cancellationToken).ConfigureAwait(false);
            var materials = new Material[bindings.Materials.Count];
            for (int index = 0; index < materials.Length; index++)
                materials[index] = await loadMaterial(bindings.Materials[index], cancellationToken).ConfigureAwait(false);
            return (terrain, materials);
        }
        catch { terrain.Dispose(); throw; }
    }
}
