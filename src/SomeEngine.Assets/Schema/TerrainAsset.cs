using SomeEngine.Assets.Pipeline;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>A terrain identity with neutral field, surface and material data in independent blocks.</summary>
[Asset(".terrain.asset", SchemaName = "SomeEngine.Assets.Schema.Terrain")]
[BinarySerializable(LogicalName = "SomeEngine.Assets.Schema.Terrain")]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class TerrainAsset : Asset
{
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }

    public ValueTask UpdateAsync(TerrainCpuData data, IReadOnlyList<AssetId<MaterialAsset>> materials,
        CancellationToken cancellationToken = default)
    {
        using var bindings = new TerrainMaterialBlock { Materials = materials.ToArray() };
        return UpdateAsync(data, bindings, cancellationToken);
    }

    /// <summary>Replaces the neutral blocks together using one completed capture and copied material bindings.</summary>
    public ValueTask UpdateAsync(TerrainCpuData data, TerrainMaterialBlock materials,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var layout = new TerrainLayoutBlock { Revision = data.Revision };
        using var heights = new HeightFieldsBlock();
        using var voxels = new VoxelFieldsBlock();
        using TerrainMaterialBlock bindings = materials.Duplicate();
        layout.SetSurface(data.Surface);
        ulong key = 1;
        foreach (HeightFieldValues field in data.HeightFields)
        {
            heights.Fields.Add(new(field.Id, field.Name, field.Grid.Width, field.Grid.Depth, field.Grid.PageSize,
                field.Grid.SampleSpacing, field.InitialHeight, field.Revision));
            foreach (HeightPageValues page in field.Pages)
            {
                heights.Pages.Add(new(field.Id, page.X, page.Z, key, page.Revision, page.Id));
                heights.SetBytes(key++, TerrainPageSource.Encode(page.Samples));
            }
        }
        key = 1;
        foreach (VoxelFieldValues field in data.VoxelFields)
        {
            voxels.Fields.Add(new(field.Id, field.Name, field.Grid.PageSize, field.Grid.SampleSpacing,
                field.InitialValue, field.Revision));
            foreach (VoxelPageValues page in field.Pages)
            {
                voxels.Pages.Add(new(field.Id, page.X, page.Y, page.Z, key, page.Revision, page.Id));
                voxels.SetBytes(key++, TerrainPageSource.Encode(page.Samples));
            }
        }
        IReadOnlyList<Exception> errors = ApplyBlocks([layout, heights, voxels, bindings]);
        if (errors.Count != 0) throw new AggregateException(errors);
        return ValueTask.CompletedTask;
    }

    /// <summary>Creates editable fields with independently retained file access and demand-driven page reads.</summary>
    public async ValueTask<Terrain> CreateTerrainAsync(TerrainEditing editing, CancellationToken cancellationToken = default)
    {
        using TerrainAsset copy = Duplicate();
        TerrainLayoutBlock layout = await copy.GetBlockAsync<TerrainLayoutBlock>(cancellationToken).ConfigureAwait(false);
        HeightFieldsBlock heights = await copy.GetBlockAsync<HeightFieldsBlock>(cancellationToken).ConfigureAwait(false);
        VoxelFieldsBlock voxels = await copy.GetBlockAsync<VoxelFieldsBlock>(cancellationToken).ConfigureAwait(false);
        var heightValues = ImmutableArray.CreateBuilder<HeightFieldValues>();
        var voxelValues = ImmutableArray.CreateBuilder<VoxelFieldValues>();
        foreach (TerrainHeightField field in heights.Fields)
        {
            heightValues.Add(new(field.Id, field.Name, new(field.Width, field.Depth, field.PageSize, field.Spacing),
                field.InitialHeight, field.Revision, []));
        }
        foreach (TerrainVoxelField field in voxels.Fields)
        {
            voxelValues.Add(new(field.Id, field.Name, new(field.PageSize, field.Spacing), field.InitialValue,
                field.Revision, []));
        }
        var values = new TerrainCpuData(layout.Revision, heightValues.ToImmutable(), voxelValues.ToImmutable(), layout.GetSurface());
        Terrain terrain = values.CreateTerrain(editing);
        try
        {
            using TerrainPageSource heightSource = heights.OpenPageSource();
            using TerrainPageSource voxelSource = voxels.OpenPageSource();
            int heightReader = terrain.AddFileSource(heightSource), voxelReader = terrain.AddFileSource(voxelSource);
            foreach (TerrainHeightPage page in heights.Pages)
            {
                HeightField field = terrain.HeightFields[page.FieldId];
                int size = field.Grid.PageSize;
                field.Data.Pages.Add((page.X, page.Z), new TerrainFieldPage(size * size,
                    new TerrainFilePage(heightReader, page.ChunkKey), page.Revision, page.Id));
            }
            foreach (TerrainVoxelPage page in voxels.Pages)
            {
                VoxelField field = terrain.VoxelFields[page.FieldId];
                int size = field.Grid.PageSize;
                field.Data.Pages.Add((page.X, page.Y, page.Z), new TerrainFieldPage(size * size * size,
                    new TerrainFilePage(voxelReader, page.ChunkKey), page.Revision, page.Id));
            }
            return terrain;
        }
        catch { terrain.Dispose(); throw; }
    }
}
