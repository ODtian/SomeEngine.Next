using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Resources;
using SomeEngine.Terrains;

namespace DefaultGame;

/// <summary>Public save/reopen flow; the application resolves saved material references into ordinary materials.</summary>
public static class TerrainAssetExample
{
    /// <summary>Saves one field capture together with offline geometry built from that same data.</summary>
    public static async ValueTask<TerrainClusterData> SaveCacheAsync<TAttributes>(string path, Terrain terrain, TerrainEditing editing,
        IReadOnlyList<TerrainChunkBounds> chunks, ITerrainAttributeGenerator<TAttributes> attributes,
        IClusterVertexEncoder<TAttributes> encoder, TerrainGeometryIdentity identity,
        IReadOnlyList<AssetId<MaterialAsset>> materials, ITerrainMesher? mesher = null,
        CancellationToken cancellationToken = default) where TAttributes : unmanaged
    {
        TerrainChunkBounds[] selection = chunks.ToArray();
        AssetId<MaterialAsset>[] references = materials.ToArray();
        TerrainCpuData values = await terrain.CaptureCpuAsync().ConfigureAwait(false);
        using TerrainClusterBlock clusters = await TerrainClusterCache.BuildAsync(values, editing, selection,
            attributes, encoder, identity, mesher).ConfigureAwait(false);
        using var asset = new TerrainAsset { Name = "Cached editable terrain" };
        await asset.UpdateAsync(values, references, cancellationToken).ConfigureAwait(false);
        asset.SetBlock(clusters);
        await asset.SaveAsync(path, cancellationToken).ConfigureAwait(false);
        return await clusters.CreateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reopens editable fields, application materials and ready cached Mesh values.</summary>
    public static async ValueTask<(Terrain Terrain, Material[] Materials, TerrainClusterData Cache)> OpenCacheAsync(
        string path, TerrainEditing editing, Func<AssetId<MaterialAsset>, CancellationToken, ValueTask<Material>> loadMaterial,
        CancellationToken cancellationToken = default)
    {
        using TerrainAsset asset = await TerrainAsset.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        TerrainClusterBlock clusters = await asset.GetBlockAsync<TerrainClusterBlock>(cancellationToken).ConfigureAwait(false);
        TerrainClusterData cache = await clusters.CreateAsync(cancellationToken).ConfigureAwait(false);
        var loaded = await ReadTerrainAsync(asset, editing, loadMaterial, cancellationToken).ConfigureAwait(false);
        return (loaded.Terrain, loaded.Materials, cache);
    }

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
        return await ReadTerrainAsync(asset, editing, loadMaterial, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<(Terrain Terrain, Material[] Materials)> ReadTerrainAsync(TerrainAsset asset,
        TerrainEditing editing, Func<AssetId<MaterialAsset>, CancellationToken, ValueTask<Material>> loadMaterial,
        CancellationToken cancellationToken)
    {
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
