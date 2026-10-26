using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.Resources;

namespace SomeEngine.Geometry.Cluster;

public enum ClusterBuildStrategy
{
    /// <summary>Builds the source triangles into leaf clusters and a BVH for immediate use.</summary>
    Realtime,
    /// <summary>Welds equal vertices, optimizes topology and builds a simplified LOD hierarchy.</summary>
    Offline,
}

/// <summary>A contiguous triangle range and the material region used by its triangles.</summary>
public readonly record struct ClusterMaterialGroup(int IndexStart, int IndexCount, uint MaterialIndex);

public static partial class ClusterMeshBuilder
{
    /// <summary>Builds ready geometry with the caller's attribute type and matching vertex encoding.</summary>
    public static Mesh Build<TAttributes>(
        Vector3[] positions,
        TAttributes[] attributes,
        uint[] indices,
        IReadOnlyList<ClusterMaterialGroup> materialGroups,
        IClusterVertexEncoder<TAttributes> vertexEncoder,
        ClusterBuildStrategy strategy,
        string name) where TAttributes : unmanaged
    {
        var groupedPositions = new List<Vector3>();
        var groupedAttributes = new List<TAttributes>();
        var groupedMaterialIndices = new List<uint>();
        var groupedIndices = new List<uint>(indices.Length);
        var vertices = new Dictionary<(uint Vertex, uint Material), uint>();
        foreach (ClusterMaterialGroup group in materialGroups)
        {
            foreach (uint sourceVertex in indices.AsSpan(group.IndexStart, group.IndexCount))
            {
                var key = (sourceVertex, group.MaterialIndex);
                if (!vertices.TryGetValue(key, out uint vertex))
                {
                    vertex = (uint)groupedPositions.Count;
                    vertices.Add(key, vertex);
                    groupedPositions.Add(positions[sourceVertex]);
                    groupedAttributes.Add(attributes[sourceVertex]);
                    groupedMaterialIndices.Add(group.MaterialIndex);
                }
                groupedIndices.Add(vertex);
            }
        }

        return BuildCore(groupedPositions.ToArray(),
            new TypedVertexData<TAttributes>(groupedAttributes.ToArray(), vertexEncoder),
            groupedIndices.ToArray(), groupedMaterialIndices.ToArray(), name, strategy);
    }

    /// <summary>Builds geometry from named float attributes supplied by source-format integrations.</summary>
    public static Mesh Build(Vector3[] positions, IReadOnlyList<RawAttribute> attributes, uint[] indices,
        int materialCount, IClusterVertexEncoder vertexEncoder, ClusterBuildStrategy strategy, string name)
    {
        RawAttribute? materialIndices = attributes.FirstOrDefault(static attribute => attribute.Name == "_MATERIAL_INDEX");
        var vertexAttributes = attributes.Where(static attribute => attribute.Name != "_MATERIAL_INDEX").ToArray();
        foreach (RawAttribute attribute in vertexAttributes)
        {
            if (attribute.Data.Length < positions.Length * attribute.Dimension)
                throw new ArgumentException("Attribute data must cover every input vertex.", nameof(attributes));
        }
        return BuildCore(positions, new RawVertexData(vertexAttributes, vertexEncoder), indices,
            materialIndices?.Data.Select(static value => checked((uint)value)).ToArray() ?? new uint[positions.Length], name, strategy, materialCount);
    }

    /// <summary>Builds named attributes with full-width per-vertex material indices.</summary>
    public static Mesh Build(Vector3[] positions, IReadOnlyList<RawAttribute> attributes, uint[] indices,
        uint[] materialIndices, IClusterVertexEncoder vertexEncoder, ClusterBuildStrategy strategy, string name)
        => BuildCore(positions, new RawVertexData(attributes.Where(static attribute => attribute.Name != "_MATERIAL_INDEX").ToArray(),
            vertexEncoder), indices, materialIndices, name, strategy);

    private static T[] RemapValues<T>(T[] source, ReadOnlySpan<uint> remap, int vertexCount)
    {
        var result = new T[vertexCount];
        for (int index = 0; index < remap.Length; index++)
            if (remap[index] < vertexCount) result[remap[index]] = source[index];
        return result;
    }

    private readonly record struct VertexStream(Array Data, int Stride);

    private abstract class VertexData
    {
        public abstract string Compatibility { get; }
        public virtual IReadOnlyList<RawAttribute> RawAttributes => [];
        public abstract VertexStream[] Streams { get; }
        public abstract VertexData Remap(ReadOnlySpan<uint> remap, int vertexCount);
        public abstract EncodedClusterVertices Encode(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> vertices);
    }

    private sealed class TypedVertexData<T>(T[] attributes, IClusterVertexEncoder<T> encoder) : VertexData
        where T : unmanaged
    {
        public override string Compatibility => encoder.Compatibility;
        public override VertexStream[] Streams => [new(attributes, Unsafe.SizeOf<T>())];
        public override VertexData Remap(ReadOnlySpan<uint> remap, int vertexCount)
            => new TypedVertexData<T>(RemapValues(attributes, remap, vertexCount), encoder);
        public override EncodedClusterVertices Encode(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> vertices)
            => encoder.Encode(positions, attributes, vertices);
    }

    private sealed class RawVertexData(IReadOnlyList<RawAttribute> attributes, IClusterVertexEncoder encoder) : VertexData
    {
        public override IReadOnlyList<RawAttribute> RawAttributes => attributes;
        public override string Compatibility => encoder.Compatibility;
        public override VertexStream[] Streams => attributes
            .Select(static attribute => new VertexStream(attribute.Data, attribute.Dimension * sizeof(float))).ToArray();

        public override VertexData Remap(ReadOnlySpan<uint> remap, int vertexCount)
        {
            var result = new List<RawAttribute>(attributes.Count);
            foreach (RawAttribute attribute in attributes)
            {
                var data = new float[vertexCount * attribute.Dimension];
                for (int index = 0; index < remap.Length; index++)
                    if (remap[index] < vertexCount)
                        attribute.Data.AsSpan(index * attribute.Dimension, attribute.Dimension)
                            .CopyTo(data.AsSpan((int)remap[index] * attribute.Dimension));
                result.Add(new(attribute.Name, data, attribute.Dimension, attribute.TargetType,
                    attribute.NumComponents, attribute.Normalized));
            }
            return new RawVertexData(result, encoder);
        }

        public override EncodedClusterVertices Encode(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> vertices)
            => encoder.Encode(positions, attributes, vertices);
    }
}
