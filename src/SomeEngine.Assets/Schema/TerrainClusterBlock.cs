using SomeEngine.Resources.Geometry;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Offline Cluster geometry and the field, surface and generation data from which it was built.</summary>
[AssetBlock("SomeEngine.Assets.Schema.TerrainClusterBlock"), BinarySerializable]
[BinaryFields(nameof(Identity), nameof(VertexEncoding), nameof(MesherReadBorder), nameof(AttributeReadBorder),
    nameof(HeightFieldIds), nameof(VoxelFieldIds), nameof(SurfaceRoot), nameof(HeightSources), nameof(VoxelSources),
    nameof(Blends), nameof(HeightFields), nameof(VoxelFields), nameof(HeightPages), nameof(VoxelPages),
    nameof(Entries), nameof(Pages), nameof(Regions))]
public sealed partial class TerrainClusterBlock : AssetBlock
{
    public partial TerrainGeometryIdentity Identity { get; set; }
    public partial string VertexEncoding { get; set; }
    public partial int MesherReadBorder { get; set; }
    public partial int AttributeReadBorder { get; set; }
    public partial IList<int> HeightFieldIds { get; set; }
    public partial IList<int> VoxelFieldIds { get; set; }
    public partial int SurfaceRoot { get; set; }
    public partial IList<TerrainHeightSource> HeightSources { get; set; }
    public partial IList<TerrainVoxelSource> VoxelSources { get; set; }
    public partial IList<TerrainBlendNode> Blends { get; set; }
    public partial IList<TerrainHeightField> HeightFields { get; set; }
    public partial IList<TerrainVoxelField> VoxelFields { get; set; }
    public partial IList<TerrainHeightPageVersion> HeightPages { get; set; }
    public partial IList<TerrainVoxelPageVersion> VoxelPages { get; set; }
    public partial IList<TerrainClusterEntry> Entries { get; set; }
    public partial IList<TerrainClusterPage> Pages { get; set; }
    public partial IList<TerrainClusterRegion> Regions { get; set; }

    /// <summary>Copies the captured source descriptions used by all entries in this block.</summary>
    public void SetSource(TerrainCpuData source, TerrainFieldInputs fields)
    {
        HeightFieldIds = fields.HeightFieldIds.AsSpan().ToArray().Distinct().Order().ToArray();
        VoxelFieldIds = fields.VoxelFieldIds.AsSpan().ToArray().Distinct().Order().ToArray();
        using var layout = new TerrainLayoutBlock();
        layout.SetSurface(source.Surface);
        SurfaceRoot = layout.SurfaceRoot;
        HeightSources = layout.HeightSources.ToArray();
        VoxelSources = layout.VoxelSources.ToArray();
        Blends = layout.Blends.ToArray();
        var heightIds = HeightSources.Select(static value => value.FieldId).Concat(HeightFieldIds).ToHashSet();
        var voxelIds = VoxelSources.Select(static value => value.FieldId).Concat(VoxelFieldIds).ToHashSet();
        HeightFields = source.HeightFields.Where(field => heightIds.Contains(field.Id)).Select(field =>
            new TerrainHeightField(field.Id, field.Name, field.Grid.Width, field.Grid.Depth, field.Grid.PageSize,
                field.Grid.SampleSpacing, field.InitialHeight, field.Revision)).ToArray();
        VoxelFields = source.VoxelFields.Where(field => voxelIds.Contains(field.Id)).Select(field =>
            new TerrainVoxelField(field.Id, field.Name, field.Grid.PageSize, field.Grid.SampleSpacing,
                field.InitialValue, field.Revision)).ToArray();
        HeightPages = source.HeightFields.Where(field => heightIds.Contains(field.Id)).SelectMany(field =>
            field.Pages.Select(page => new TerrainHeightPageVersion(field.Id, page.X, page.Z, page.Revision, page.Id))).ToArray();
        VoxelPages = source.VoxelFields.Where(field => voxelIds.Contains(field.Id)).SelectMany(field =>
            field.Pages.Select(page => new TerrainVoxelPageVersion(field.Id, page.X, page.Y, page.Z, page.Revision, page.Id))).ToArray();
    }

    /// <summary>Copies existing Mesh page and BVH bytes into a chunk entry.</summary>
    public void AddGeometry(TerrainClusterEntry entry, Mesh? mesh)
    {
        int index = Entries.Count;
        if (mesh is null)
        {
            Entries.Add(entry);
            return;
        }
        Mesh data = mesh.CaptureData();
        var bytes = new byte[checked((int)data.BvhOffset + data.BvhByteLength)];
        for (int page = 0; page < data.PageCount; page++)
        {
            MeshPayloadPageDigest description = data.PageDigests[page];
            data.ReadPage(page, bytes.AsSpan(checked((int)description.Offset), checked((int)description.Length)));
            Pages.Add(new(index, page, description, data.LowestDetailPageIndices.Contains((uint)page)));
        }
        data.ReadBvh(bytes.AsSpan(checked((int)data.BvhOffset)));
        foreach (MeshRegion region in data.Regions) Regions.Add(new(index, region));
        ulong key = checked((ulong)index + 1);
        SetBytes(key, bytes);
        Entries.Add(entry with { PayloadKey = key, BvhOffset = data.BvhOffset, Bounds = data.Bounds });
    }

    /// <summary>Reads ready Mesh values whose managed data outlives the asset file.</summary>
    public async ValueTask<TerrainClusterData> CreateAsync(CancellationToken cancellationToken = default)
    {
        using TerrainClusterBlock copy = Duplicate();
        var meshes = new TerrainClusterMesh[copy.Entries.Count];
        for (int index = 0; index < meshes.Length; index++)
        {
            TerrainClusterEntry entry = copy.Entries[index];
            Mesh? mesh = null;
            if (entry.PayloadKey != 0)
            {
                ReadOnlyMemory<byte> bytes = await copy.ReadBytesAsync(entry.PayloadKey, cancellationToken).ConfigureAwait(false);
                TerrainClusterPage[] pages = copy.Pages.Where(page => page.EntryIndex == index).OrderBy(page => page.PageIndex).ToArray();
                mesh = Mesh.Create($"Terrain chunk ({entry.X}, {entry.Y}, {entry.Z})", copy.VertexEncoding,
                    pages.Select(static page => page.Description).ToArray(), bytes[checked((int)entry.BvhOffset)..],
                    pages.Select(page => bytes.Slice(checked((int)page.Description.Offset), checked((int)page.Description.Length))).ToArray(),
                    entry.Bounds, copy.Regions.Where(region => region.EntryIndex == index).Select(static region => region.Region).ToArray(),
                    pages.Where(static page => page.LowestDetail).Select(static page => checked((uint)page.PageIndex)).ToArray());
            }
            meshes[index] = new(entry, mesh);
        }
        return new(copy, meshes);
    }

    internal TerrainSurfaceValues? GetSurface()
    {
        using var layout = new TerrainLayoutBlock
        {
            SurfaceRoot = SurfaceRoot, HeightSources = HeightSources.ToArray(),
            VoxelSources = VoxelSources.ToArray(), Blends = Blends.ToArray(),
        };
        return layout.GetSurface();
    }
}

/// <summary>User-supplied mesher and attribute-generator versions, including their configuration.</summary>
[BinarySerializable, BinaryValue, BinaryFields(nameof(Mesher), nameof(Attributes))]
public readonly partial record struct TerrainGeometryIdentity(string Mesher, string Attributes);

[BinarySerializable, BinaryValue]
[BinaryFields(nameof(X), nameof(Y), nameof(Z), nameof(CellCount), nameof(CellSize), nameof(Lod), nameof(TransitionFaces),
    nameof(Revision), nameof(PayloadKey), nameof(BvhOffset), nameof(Bounds))]
public readonly partial record struct TerrainClusterEntry(int X, int Y, int Z, int CellCount, float CellSize,
    int Lod, byte TransitionFaces, ulong Revision, ulong PayloadKey = 0, ulong BvhOffset = 0, Bounds? Bounds = null);

[BinarySerializable, BinaryValue, BinaryFields(nameof(EntryIndex), nameof(PageIndex), nameof(Description), nameof(LowestDetail))]
public readonly partial record struct TerrainClusterPage(int EntryIndex, int PageIndex, MeshPayloadPageDigest Description, bool LowestDetail);

[BinarySerializable, BinaryValue, BinaryFields(nameof(EntryIndex), nameof(Region))]
public readonly partial record struct TerrainClusterRegion(int EntryIndex, MeshRegion Region);

[BinarySerializable, BinaryValue, BinaryFields(nameof(FieldId), nameof(X), nameof(Z), nameof(Revision), nameof(Id))]
public readonly partial record struct TerrainHeightPageVersion(int FieldId, int X, int Z, ulong Revision, Guid Id);

[BinarySerializable, BinaryValue, BinaryFields(nameof(FieldId), nameof(X), nameof(Y), nameof(Z), nameof(Revision), nameof(Id))]
public readonly partial record struct TerrainVoxelPageVersion(int FieldId, int X, int Y, int Z, ulong Revision, Guid Id);
