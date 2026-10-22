using System.Numerics;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[AssetBlock("SomeEngine.Assets.Schema.TerrainLayoutBlock"), BinarySerializable]
[BinaryFields(nameof(Revision), nameof(SurfaceRoot), nameof(HeightSources), nameof(VoxelSources), nameof(Blends))]
public sealed partial class TerrainLayoutBlock : AssetBlock
{
    public partial ulong Revision { get; set; }
    public partial int SurfaceRoot { get; set; }
    public partial IList<TerrainHeightSource> HeightSources { get; set; }
    public partial IList<TerrainVoxelSource> VoxelSources { get; set; }
    public partial IList<TerrainBlendNode> Blends { get; set; }

    internal void SetSurface(TerrainSurfaceValues? surface)
    {
        var nodes = new Dictionary<TerrainSurfaceValues, int>(ReferenceEqualityComparer.Instance);
        SurfaceRoot = surface is null ? -1 : Add(surface);

        int Add(TerrainSurfaceValues values)
        {
            if (nodes.TryGetValue(values, out int existing)) return existing;
            int id;
            switch (values)
            {
                case HeightSurfaceValues height:
                    id = nodes.Count;
                    HeightSources.Add(new(id, height.FieldId));
                    break;
                case VoxelSurfaceValues voxel:
                    id = nodes.Count;
                    VoxelSources.Add(new(id, voxel.FieldId));
                    break;
                case BlendSurfaceValues blend:
                    int first = Add(blend.First), second = Add(blend.Second);
                    id = nodes.Count;
                    Blends.Add(new(id, first, second, blend.Weight, blend.Region.HasValue,
                        ToVec3(blend.Region?.Min ?? default), ToVec3(blend.Region?.Max ?? default), blend.FadeDistance));
                    break;
                default: throw new NotSupportedException($"Surface data {values.GetType().Name} has no terrain asset encoding.");
            }
            nodes.Add(values, id);
            return id;
        }
    }

    internal TerrainSurfaceValues? GetSurface()
    {
        if (SurfaceRoot < 0) return null;
        var nodes = new Dictionary<int, TerrainSurfaceValues>();
        foreach (TerrainHeightSource source in HeightSources) nodes.Add(source.Id, new HeightSurfaceValues(source.FieldId));
        foreach (TerrainVoxelSource source in VoxelSources) nodes.Add(source.Id, new VoxelSurfaceValues(source.FieldId));
        var blends = Blends.ToDictionary(blend => blend.Id);
        return Read(SurfaceRoot);

        TerrainSurfaceValues Read(int id)
        {
            if (nodes.TryGetValue(id, out TerrainSurfaceValues? existing)) return existing;
            TerrainBlendNode blend = blends[id];
            var values = new BlendSurfaceValues(Read(blend.First), Read(blend.Second), blend.Weight,
                blend.Local ? new TerrainBounds(new(blend.Min.X, blend.Min.Y, blend.Min.Z),
                    new(blend.Max.X, blend.Max.Y, blend.Max.Z)) : null, blend.FadeDistance);
            nodes.Add(id, values);
            return values;
        }
    }

    private static Vec3 ToVec3(Vector3 value) => new() { X = value.X, Y = value.Y, Z = value.Z };
}

[BinarySerializable, BinaryValue, BinaryFields(nameof(Id), nameof(FieldId))]
public readonly partial record struct TerrainHeightSource(int Id, int FieldId);

[BinarySerializable, BinaryValue, BinaryFields(nameof(Id), nameof(FieldId))]
public readonly partial record struct TerrainVoxelSource(int Id, int FieldId);

[BinarySerializable, BinaryValue]
[BinaryFields(nameof(Id), nameof(First), nameof(Second), nameof(Weight), nameof(Local), nameof(Min), nameof(Max), nameof(FadeDistance))]
public readonly partial record struct TerrainBlendNode(int Id, int First, int Second, float Weight,
    bool Local, Vec3 Min, Vec3 Max, float FadeDistance);
