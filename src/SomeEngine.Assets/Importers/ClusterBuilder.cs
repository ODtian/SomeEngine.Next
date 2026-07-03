using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MeshOptimizer;
using SharpGLTF.Schema2;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Schema;
using ValueType = SomeEngine.Assets.Data.ValueType;

namespace SomeEngine.Assets.Importers;

public sealed class ClusterBuilderOptions
{
    public bool GenerateMissingTangents { get; init; }
}

public struct ClusterLodConfig
{
    public int MaxVertices;
    public int MinTriangles;
    public int MaxTriangles;
    public bool PartitionSpatial;
    public bool PartitionSort;
    public int PartitionSize;
    public bool ClusterSpatial;
    public float ClusterFillWeight;
    public float ClusterSplitFactor;
    public float SimplifyRatio;
    public float SimplifyThreshold;
    public float SimplifyErrorFactorSloppy;
    public float SimplifyErrorEdgeLimit;
    public bool SimplifyPermissive;
    public bool SimplifyFallbackPermissive;
    public bool SimplifyFallbackSloppy;
    public bool SimplifyRegularize;
    public bool OptimizeBounds;
    public bool OptimizeClusters;

    public static ClusterLodConfig GetDefault(int maxTriangles = 124)
    {
        return new ClusterLodConfig
        {
            MaxVertices = 64,
            MinTriangles = maxTriangles / 3,
            MaxTriangles = maxTriangles,
            PartitionSpatial = true,
            PartitionSort = false,
            PartitionSize = 16,
            ClusterSpatial = false,
            ClusterFillWeight = 0.5f,
            ClusterSplitFactor = 2.0f,
            SimplifyRatio = 0.5f,
            SimplifyThreshold = 0.85f,
            SimplifyErrorFactorSloppy = 2.0f,
            SimplifyErrorEdgeLimit = 0.0f,
            SimplifyPermissive = true,
            SimplifyFallbackPermissive = false,
            SimplifyFallbackSloppy = true,
            SimplifyRegularize = false,
            OptimizeBounds = true,
            OptimizeClusters = true,
        };
    }
}

public static partial class ClusterBuilder
{
    private const int MaxVerticesPerMeshlet = 64;
    private const int MaxTrianglesPerMeshlet = 124;
    private const float ConeWeight = 0.0f;
    private const int GroupSize = 4;
    private const float SimplifyRatio = 0.5f;
    private const int PageSize = 128 * 1024; // 128KB
    private const int PageHeaderSize = MeshPageHeader.Size;
    private const int MaxEncodedTriangleStart = ushort.MaxValue;

    private struct BuilderMeshlet
    {
        public int IndicesOffset;
        public int IndicesCount;
        public int Level;
        public float Error;
        public float ParentError;
        public int GroupId;
        public int ParentGroupId;
        public Vector3 Center;
        public float Radius;
        public Vector3 LodCenter;
        public float LodRadius;
        public Vector3 SelfLodCenter;
        public float SelfLodRadius;
        public int VertexCount;

        public byte Mat0;
        public byte Mat1;
        public byte Mat2;
        public byte Range0End;
        public byte Range1End;

        // VRB batch info (packed uint, see BuildVrb)
        public uint VRBBatchInfo;
    }

    private struct ClusterLodBounds
    {
        public Vector3 Center;
        public float Radius;
        public float Error;
    }

    private struct MeshPageInfo
    {
        public uint ClusterCount;
        public uint TotalVertexCount;
        public uint TotalTriangleCount;
        public uint ClustersOffset;
        public uint PositionsOffset;
        public uint AttributesOffset;
        public uint IndicesOffset;
        public long FileOffset;
    }

    private struct ClusterInfo
    {
        public Vector3 BoundMin;
        public Vector3 BoundMax;
        public Vector4 LODSphere; // xyz: center, w: radius
        public float LODError;
        public uint PageIndex;
        public uint ClusterStart;
        public int ParentGroupId;
    }

    private readonly record struct BvhBounds(
        Vector3 Min,
        Vector3 Max,
        Vector3 Center,
        float Radius,
        float Error);

    private sealed class MeshAssemblyState
    {
        public List<Vector3> Positions { get; } = [];
        public List<uint> Indices { get; } = [];
        public Dictionary<string, List<float>> Attributes { get; } = [];
        public List<float> MaterialIndices { get; } = [];
        public List<MeshMaterialSlot> MaterialSlots { get; } = [];
    }

    private sealed class LodWorkspace
    {
        public List<int> GroupOffsets { get; } = [];
        public List<uint> MergedIndices { get; } = [];
        public List<uint> SimplifiedIndices { get; } = [];
    }

    private readonly record struct AttributeDefinition(
        string Name,
        int Dimension,
        ValueType TargetType,
        byte NumComponents,
        bool Normalized);

    private readonly record struct MeshProcessingData(
        Vector3[] Positions,
        List<RawAttribute> RawAttributes,
        uint[] Indices,
        float[] MaterialIndices,
        List<MeshMaterialSlot> MaterialSlots);

    private readonly record struct TriangleIndices(int I0, int I1, int I2);

    private readonly record struct TangentContribution(Vector3 Surface, Vector3 Bitangent);

    // Morton Code Helpers
    private static uint ExpandBits(uint v)
    {
        v = (v * 0x00010001u) & 0xFF0000FFu;
        v = (v * 0x00000101u) & 0x0F00F00Fu;
        v = (v * 0x00000011u) & 0xC30C30C3u;
        v = (v * 0x00000005u) & 0x49249249u;
        return v;
    }

    private static uint Morton3D(Vector3 p)
    {
        p.X = Math.Min(Math.Max(p.X * 1024.0f, 0.0f), 1023.0f);
        p.Y = Math.Min(Math.Max(p.Y * 1024.0f, 0.0f), 1023.0f);
        p.Z = Math.Min(Math.Max(p.Z * 1024.0f, 0.0f), 1023.0f);
        return ExpandBits((uint)p.X) * 4 + ExpandBits((uint)p.Y) * 2 + ExpandBits((uint)p.Z);
    }

    private static List<ClusterBVHNode> BuildBvh(List<ClusterInfo> clusters)
    {
        var nodes = new List<ClusterBVHNode>();
        if (clusters.Count == 0)
        {
            return nodes;
        }

        List<int> currentLevelIndices = CreateBvhLeafNodes(clusters, nodes);
        AddBvhInternalLevels(nodes, currentLevelIndices);
        return nodes;
    }

    private static List<int> CreateBvhLeafNodes(
        List<ClusterInfo> clusters,
        List<ClusterBVHNode> nodes)
    {
        var currentLevelIndices = new List<int>();
        int clusterIndex = 0;
        while (clusterIndex < clusters.Count)
        {
            int count = CountLeafClusters(clusters, clusterIndex);
            nodes.Add(CreateBvhLeafNode(clusters, clusterIndex, count));
            currentLevelIndices.Add(nodes.Count - 1);
            clusterIndex += count;
        }

        return currentLevelIndices;
    }

    private static int CountLeafClusters(List<ClusterInfo> clusters, int start)
    {
        uint currentPage = clusters[start].PageIndex;
        int currentParent = clusters[start].ParentGroupId;
        int count = 0;
        while (start + count < clusters.Count
            && clusters[start + count].PageIndex == currentPage
            && clusters[start + count].ParentGroupId == currentParent
            && count < 128)
        {
            count++;
        }

        return count;
    }

    private static ClusterBVHNode CreateBvhLeafNode(
        List<ClusterInfo> clusters,
        int start,
        int count)
    {
        BvhBounds bounds = ComputeLeafBounds(clusters, start, count);
        ClusterInfo first = clusters[start];
        var node = new ClusterBVHNode
        {
            BoundMin = new Vector4(bounds.Min, 0),
            BoundMax = new Vector4(bounds.Max, 0),
            LODSphere = first.LODSphere,
            LODError = first.LODError,
            ChildPointer = first.PageIndex,
            NodeType = 1,
        };
        node.SetLeafData(first.ClusterStart, (uint)count);
        return node;
    }

    private static BvhBounds ComputeLeafBounds(
        List<ClusterInfo> clusters,
        int start,
        int count)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        for (int offset = 0; offset < count; offset++)
        {
            ClusterInfo cluster = clusters[start + offset];
            min = Vector3.Min(min, cluster.BoundMin);
            max = Vector3.Max(max, cluster.BoundMax);
        }

        return new BvhBounds(min, max, Vector3.Zero, 0, 0);
    }

    private static void AddBvhInternalLevels(
        List<ClusterBVHNode> nodes,
        List<int> currentLevelIndices)
    {
        while (currentLevelIndices.Count > 1)
        {
            var nextLevelIndices = new List<int>();
            for (int start = 0; start < currentLevelIndices.Count; start += 16)
            {
                int count = Math.Min(16, currentLevelIndices.Count - start);
                nodes.Add(CreateBvhInternalNode(nodes, currentLevelIndices, start, count));
                nextLevelIndices.Add(nodes.Count - 1);
            }

            currentLevelIndices = nextLevelIndices;
        }
    }

    private static ClusterBVHNode CreateBvhInternalNode(
        List<ClusterBVHNode> nodes,
        List<int> currentLevelIndices,
        int start,
        int count)
    {
        BvhBounds bounds = ComputeInternalBounds(nodes, currentLevelIndices, start, count);
        return new ClusterBVHNode
        {
            ChildPointer = (uint)currentLevelIndices[start],
            ChildCount = (uint)count,
            NodeType = 0,
            BoundMin = new Vector4(bounds.Min, 0),
            BoundMax = new Vector4(bounds.Max, 0),
            LODSphere = new Vector4(bounds.Center.X, bounds.Center.Y, bounds.Center.Z, bounds.Radius),
            LODError = bounds.Error,
        };
    }

    private static BvhBounds ComputeInternalBounds(
        List<ClusterBVHNode> nodes,
        List<int> currentLevelIndices,
        int start,
        int count)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        float maxError = 0;
        Vector3 centerSum = Vector3.Zero;
        for (int offset = 0; offset < count; offset++)
        {
            ClusterBVHNode child = nodes[currentLevelIndices[start + offset]];
            min = Vector3.Min(min, ToVector3(child.BoundMin));
            max = Vector3.Max(max, ToVector3(child.BoundMax));
            maxError = Math.Max(maxError, child.LODError);
            centerSum += ToVector3(child.LODSphere);
        }

        Vector3 center = centerSum / count;
        float radius = ComputeInternalLodRadius(nodes, currentLevelIndices, start, count, center);
        return new BvhBounds(min, max, center, radius, maxError);
    }

    private static float ComputeInternalLodRadius(
        List<ClusterBVHNode> nodes,
        List<int> currentLevelIndices,
        int start,
        int count,
        Vector3 center)
    {
        float radius = 0;
        for (int offset = 0; offset < count; offset++)
        {
            ClusterBVHNode child = nodes[currentLevelIndices[start + offset]];
            float distance = Vector3.Distance(center, ToVector3(child.LODSphere)) + child.LODSphere.W;
            radius = Math.Max(radius, distance);
        }

        return radius;
    }

    private static Vector3 ToVector3(Vector4 value)
        => new(value.X, value.Y, value.Z);
}

public static partial class ClusterBuilder
{


    public static MeshAsset Process(string filePath, Func<string, AssetGuid>? materialGuidResolver = null)
    {
        var model = ModelRoot.Load(filePath);
        var mesh = model.LogicalMeshes[0];
        IReadOnlyList<MeshMaterialSlot> materialSlots = mesh.Primitives
            .Select(primitive => new MeshMaterialSlot(materialGuidResolver?.Invoke(primitive.Material?.Name ?? string.Empty) ?? AssetGuid.Empty))
            .ToArray();
        return ProcessMesh(mesh, materialSlots, mesh.Name ?? "Unnamed");
    }

    public static MeshAsset ProcessMesh(
        Mesh mesh,
        IReadOnlyList<MeshMaterialSlot> materialSlots,
        string name,
        ClusterBuilderOptions? options = null)
    {
        MeshProcessingData meshData = BuildMeshProcessingData(mesh, materialSlots);
        AddGeneratedTangentIfNeeded(meshData.RawAttributes, meshData.Positions, meshData.Indices, options, name);
        meshData.RawAttributes.Add(
            new RawAttribute(
                "_MATERIAL_INDEX",
                meshData.MaterialIndices,
                1,
                ValueType.UInt8,
                1,
                false));
        meshData.RawAttributes.Sort(CompareRawAttributes);
        return ProcessRaw(
            meshData.Positions,
            meshData.RawAttributes,
            meshData.Indices,
            meshData.MaterialSlots,
            name);
    }

    private static MeshProcessingData BuildMeshProcessingData(
        Mesh mesh,
        IReadOnlyList<MeshMaterialSlot> materialSlots)
    {
        AttributeDefinition[] definitions = ReadAttributeDefinitions(mesh.Primitives[0]);
        MeshAssemblyState state = CreateMeshAssemblyState(definitions);
        uint vertexOffset = 0;
        for (int primitiveIndex = 0; primitiveIndex < mesh.Primitives.Count; primitiveIndex++)
        {
            vertexOffset = AppendPrimitive(
                state,
                definitions,
                mesh.Primitives[primitiveIndex],
                materialSlots,
                primitiveIndex,
                vertexOffset);
        }

        return new MeshProcessingData(
            state.Positions.ToArray(),
            CreateRawAttributes(definitions, state.Attributes),
            state.Indices.ToArray(),
            state.MaterialIndices.ToArray(),
            state.MaterialSlots);
    }

    private static MeshAssemblyState CreateMeshAssemblyState(AttributeDefinition[] definitions)
    {
        var state = new MeshAssemblyState();
        for (int index = 0; index < definitions.Length; index++)
        {
            state.Attributes[definitions[index].Name] = [];
        }

        return state;
    }

    private static AttributeDefinition[] ReadAttributeDefinitions(MeshPrimitive primitive)
    {
        var definitions = new List<AttributeDefinition>();
        foreach (string key in primitive.VertexAccessors.Keys)
        {
            if (key == "POSITION")
            {
                continue;
            }

            definitions.Add(CreateAttributeDefinition(key, primitive.GetVertexAccessor(key)));
        }

        return definitions.ToArray();
    }

    private static AttributeDefinition CreateAttributeDefinition(string key, Accessor accessor)
    {
        int dimension = AccessorDimension(accessor);
        (ValueType targetType, bool normalized) = AttributeStorage(key, accessor.Normalized);
        return new AttributeDefinition(key, dimension, targetType, (byte)dimension, normalized);
    }

    private static int AccessorDimension(Accessor accessor)
        => accessor.Dimensions switch
        {
            DimensionType.SCALAR => 1,
            DimensionType.VEC2 => 2,
            DimensionType.VEC3 => 3,
            DimensionType.VEC4 => 4,
            _ => 1,
        };

    private static (ValueType TargetType, bool Normalized) AttributeStorage(
        string key,
        bool normalized)
    {
        if (key == "NORMAL" || key == "TANGENT")
        {
            return (ValueType.Int8, true);
        }

        if (key.StartsWith("TEXCOORD", StringComparison.Ordinal))
        {
            return (ValueType.Float16, normalized);
        }

        if (key.StartsWith("COLOR", StringComparison.Ordinal))
        {
            return (ValueType.UInt8, true);
        }

        if (key.StartsWith("JOINTS", StringComparison.Ordinal))
        {
            return (ValueType.UInt16, normalized);
        }

        return key.StartsWith("WEIGHTS", StringComparison.Ordinal)
            ? (ValueType.UInt8, true)
            : (ValueType.Float32, normalized);
    }

    private static uint AppendPrimitive(
        MeshAssemblyState state,
        AttributeDefinition[] definitions,
        MeshPrimitive primitive,
        IReadOnlyList<MeshMaterialSlot> materialSlots,
        int primitiveIndex,
        uint vertexOffset)
    {
        Vector3[] positions = primitive.GetVertexAccessor("POSITION").AsVector3Array().ToArray();
        state.Positions.AddRange(positions);
        AppendPrimitiveIndices(state.Indices, primitive, vertexOffset);
        AppendPrimitiveAttributes(state.Attributes, definitions, primitive, positions.Length);
        state.MaterialSlots.Add(
            primitiveIndex < materialSlots.Count
                ? materialSlots[primitiveIndex]
                : new MeshMaterialSlot(AssetGuid.Empty));
        AddMaterialIndices(state.MaterialIndices, primitiveIndex, positions.Length);
        return vertexOffset + (uint)positions.Length;
    }

    private static void AppendPrimitiveIndices(
        List<uint> indices,
        MeshPrimitive primitive,
        uint vertexOffset)
    {
        var source = primitive.GetIndexAccessor().AsIndicesArray();
        for (int index = 0; index < source.Count; index++)
        {
            indices.Add((uint)(source[index] + vertexOffset));
        }
    }

    private static void AppendPrimitiveAttributes(
        Dictionary<string, List<float>> attributes,
        AttributeDefinition[] definitions,
        MeshPrimitive primitive,
        int positionCount)
    {
        for (int index = 0; index < definitions.Length; index++)
        {
            AttributeDefinition definition = definitions[index];
            if (primitive.VertexAccessors.TryGetValue(definition.Name, out Accessor? accessor))
            {
                attributes[definition.Name].AddRange(ReadFloats(accessor));
            }
            else
            {
                attributes[definition.Name].AddRange(new float[positionCount * definition.Dimension]);
            }
        }
    }

    private static void AddMaterialIndices(List<float> destination, int primitiveIndex, int count)
    {
        for (int index = 0; index < count; index++)
        {
            destination.Add(primitiveIndex);
        }
    }

    private static List<RawAttribute> CreateRawAttributes(
        AttributeDefinition[] definitions,
        Dictionary<string, List<float>> combinedAttributes)
    {
        var rawAttributes = new List<RawAttribute>(definitions.Length);
        for (int index = 0; index < definitions.Length; index++)
        {
            AttributeDefinition definition = definitions[index];
            rawAttributes.Add(
                new RawAttribute(
                    definition.Name,
                    combinedAttributes[definition.Name].ToArray(),
                    definition.Dimension,
                    definition.TargetType,
                    definition.NumComponents,
                    definition.Normalized));
        }

        return rawAttributes;
    }

    private static float[] ReadFloats(Accessor accessor)
    {
        return accessor.Dimensions switch
        {
            DimensionType.SCALAR => accessor.AsScalarArray().ToArray(),
            DimensionType.VEC2 => ReadVector2Array(accessor),
            DimensionType.VEC3 => ReadVector3Array(accessor),
            DimensionType.VEC4 => ReadVector4Array(accessor),
            _ => throw new NotSupportedException($"Unsupported accessor dimension: {accessor.Dimensions}"),
        };
    }

    private static float[] ReadVector2Array(Accessor accessor)
    {
        var values = accessor.AsVector2Array();
        float[] result = new float[checked(values.Count * 2)];
        for (int index = 0; index < values.Count; index++)
        {
            Vector2 value = values[index];
            int offset = index * 2;
            result[offset + 0] = value.X;
            result[offset + 1] = value.Y;
        }

        return result;
    }

    private static float[] ReadVector3Array(Accessor accessor)
    {
        var values = accessor.AsVector3Array();
        float[] result = new float[checked(values.Count * 3)];
        for (int index = 0; index < values.Count; index++)
        {
            Vector3 value = values[index];
            int offset = index * 3;
            result[offset + 0] = value.X;
            result[offset + 1] = value.Y;
            result[offset + 2] = value.Z;
        }

        return result;
    }

    private static float[] ReadVector4Array(Accessor accessor)
    {
        var values = accessor.AsVector4Array();
        float[] result = new float[checked(values.Count * 4)];
        for (int index = 0; index < values.Count; index++)
        {
            Vector4 value = values[index];
            int offset = index * 4;
            result[offset + 0] = value.X;
            result[offset + 1] = value.Y;
            result[offset + 2] = value.Z;
            result[offset + 3] = value.W;
        }

        return result;
    }

    private static void AddGeneratedTangentIfNeeded(
        List<RawAttribute> rawAttributes,
        Vector3[] positions,
        uint[] indices,
        ClusterBuilderOptions? options,
        string name)
    {
        if (options?.GenerateMissingTangents == true
            && !rawAttributes.Any(static attribute => attribute.Name == "TANGENT"))
        {
            rawAttributes.Add(GenerateTangentAttribute(positions, indices, rawAttributes, name));
        }
    }

    private static int CompareRawAttributes(RawAttribute left, RawAttribute right)
    {
        int leftOrder = AttributeOrder(left.Name);
        int rightOrder = AttributeOrder(right.Name);
        return leftOrder != rightOrder
            ? leftOrder.CompareTo(rightOrder)
            : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
    }

    private static int AttributeOrder(string name)
        => name switch
        {
            "NORMAL" => 0,
            "TANGENT" => 1,
            _ when name.StartsWith("TEXCOORD", StringComparison.Ordinal) => 2,
            _ when name.StartsWith("COLOR", StringComparison.Ordinal) => 3,
            _ when name.StartsWith("JOINTS", StringComparison.Ordinal) => 4,
            _ when name.StartsWith("WEIGHTS", StringComparison.Ordinal) => 5,
            "_MATERIAL_INDEX" => 98,
            _ => 6,
        };
}

public static partial class ClusterBuilder
{

    private static RawAttribute GenerateTangentAttribute(
        Vector3[] positions,
        uint[] indices,
        IReadOnlyList<RawAttribute> rawAttributes,
        string meshName)
    {
        RawAttribute normal = FindRequiredAttribute(rawAttributes, "NORMAL", meshName);
        RawAttribute uv = FindRequiredAttribute(rawAttributes, "TEXCOORD_0", meshName);
        ValidateTangentInputs(normal, uv, positions.Length, meshName);
        Vector3[] tan1 = new Vector3[positions.Length];
        Vector3[] tan2 = new Vector3[positions.Length];
        AccumulateTangents(positions, indices, uv, tan1, tan2, meshName);
        return new RawAttribute(
            "TANGENT",
            CreateTangentData(positions, normal, tan1, tan2, meshName),
            4,
            ValueType.Int8,
            4,
            true);
    }

    private static void ValidateTangentInputs(
        RawAttribute normal,
        RawAttribute uv,
        int positionCount,
        string meshName)
    {
        if (normal.Dimension != 3 || normal.Data.Length != positionCount * 3)
        {
            throw new InvalidOperationException(
                $"Cannot generate TANGENT for mesh '{meshName}': NORMAL must be vec3 and match vertex count.");
        }

        if (uv.Dimension != 2 || uv.Data.Length != positionCount * 2)
        {
            throw new InvalidOperationException(
                $"Cannot generate TANGENT for mesh '{meshName}': TEXCOORD_0 must be vec2 and match vertex count.");
        }
    }

    private static void AccumulateTangents(
        Vector3[] positions,
        uint[] indices,
        RawAttribute uv,
        Vector3[] tan1,
        Vector3[] tan2,
        string meshName)
    {
        for (int index = 0; index < indices.Length; index += 3)
        {
            TriangleIndices triangle = ReadTriangleIndices(indices, index, positions.Length, meshName);
            TangentContribution contribution = ComputeTangentContribution(positions, uv, triangle, index, meshName);
            AddTangentContribution(tan1, tan2, triangle, contribution);
        }
    }

    private static TriangleIndices ReadTriangleIndices(
        uint[] indices,
        int offset,
        int positionCount,
        string meshName)
    {
        var triangle = new TriangleIndices(
            checked((int)indices[offset + 0]),
            checked((int)indices[offset + 1]),
            checked((int)indices[offset + 2]));
        if ((uint)triangle.I0 >= positionCount
            || (uint)triangle.I1 >= positionCount
            || (uint)triangle.I2 >= positionCount)
        {
            throw new InvalidOperationException(
                $"Cannot generate TANGENT for mesh '{meshName}': triangle index references a missing vertex.");
        }

        return triangle;
    }

    private static TangentContribution ComputeTangentContribution(
        Vector3[] positions,
        RawAttribute uv,
        TriangleIndices triangle,
        int index,
        string meshName)
    {
        Vector3 p0 = positions[triangle.I0];
        Vector3 p1 = positions[triangle.I1];
        Vector3 p2 = positions[triangle.I2];
        Vector2 w0 = ReadVector2(uv.Data, triangle.I0);
        Vector2 w1 = ReadVector2(uv.Data, triangle.I1);
        Vector2 w2 = ReadVector2(uv.Data, triangle.I2);
        float denominator = (w1.X - w0.X) * (w2.Y - w0.Y) - (w2.X - w0.X) * (w1.Y - w0.Y);
        if (denominator == 0.0f)
        {
            throw new InvalidOperationException(
                $"Cannot generate TANGENT for mesh '{meshName}': triangle {index / 3} has degenerate TEXCOORD_0 parameterization.");
        }

        return CreateTangentContribution(p0, p1, p2, w0, w1, w2, denominator);
    }

    private static TangentContribution CreateTangentContribution(
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector2 w0,
        Vector2 w1,
        Vector2 w2,
        float denominator)
    {
        Vector3 edge1 = p1 - p0;
        Vector3 edge2 = p2 - p0;
        float s1 = w1.X - w0.X;
        float s2 = w2.X - w0.X;
        float t1 = w1.Y - w0.Y;
        float t2 = w2.Y - w0.Y;
        float scale = 1.0f / denominator;
        return new TangentContribution(
            new Vector3(t2 * edge1.X - t1 * edge2.X, t2 * edge1.Y - t1 * edge2.Y, t2 * edge1.Z - t1 * edge2.Z) * scale,
            new Vector3(s1 * edge2.X - s2 * edge1.X, s1 * edge2.Y - s2 * edge1.Y, s1 * edge2.Z - s2 * edge1.Z) * scale);
    }

    private static void AddTangentContribution(
        Vector3[] tan1,
        Vector3[] tan2,
        TriangleIndices triangle,
        TangentContribution contribution)
    {
        tan1[triangle.I0] += contribution.Surface;
        tan1[triangle.I1] += contribution.Surface;
        tan1[triangle.I2] += contribution.Surface;
        tan2[triangle.I0] += contribution.Bitangent;
        tan2[triangle.I1] += contribution.Bitangent;
        tan2[triangle.I2] += contribution.Bitangent;
    }

    private static float[] CreateTangentData(
        Vector3[] positions,
        RawAttribute normal,
        Vector3[] tan1,
        Vector3[] tan2,
        string meshName)
    {
        float[] tangents = new float[positions.Length * 4];
        for (int vertex = 0; vertex < positions.Length; vertex++)
        {
            WriteTangent(tangents, vertex, normal, tan1, tan2, meshName);
        }

        return tangents;
    }

    private static void WriteTangent(
        float[] tangents,
        int vertex,
        RawAttribute normal,
        Vector3[] tan1,
        Vector3[] tan2,
        string meshName)
    {
        Vector3 n = NormalizedNormal(normal, vertex, meshName);
        Vector3 tangent = tan1[vertex] - n * Vector3.Dot(n, tan1[vertex]);
        if (tangent.LengthSquared() == 0.0f)
        {
            throw new InvalidOperationException(
                $"Cannot generate TANGENT for mesh '{meshName}': vertex {vertex} has no non-zero tangent contribution.");
        }

        tangent = Vector3.Normalize(tangent);
        float handedness = Vector3.Dot(Vector3.Cross(n, tangent), tan2[vertex]) < 0.0f ? -1.0f : 1.0f;
        int offset = vertex * 4;
        tangents[offset + 0] = tangent.X;
        tangents[offset + 1] = tangent.Y;
        tangents[offset + 2] = tangent.Z;
        tangents[offset + 3] = handedness;
    }

    private static Vector3 NormalizedNormal(RawAttribute normal, int vertex, string meshName)
    {
        Vector3 n = ReadVector3(normal.Data, vertex);
        if (n.LengthSquared() == 0.0f)
        {
            throw new InvalidOperationException(
                $"Cannot generate TANGENT for mesh '{meshName}': vertex {vertex} has zero NORMAL.");
        }

        return Vector3.Normalize(n);
    }

    private static RawAttribute FindRequiredAttribute(
        IReadOnlyList<RawAttribute> rawAttributes,
        string name,
        string meshName)
        => rawAttributes.FirstOrDefault(attribute => attribute.Name == name)
            ?? throw new InvalidOperationException(
                $"Cannot generate TANGENT for mesh '{meshName}': required attribute '{name}' is missing.");

    private static Vector2 ReadVector2(float[] values, int vertex)
    {
        int offset = checked(vertex * 2);
        return new Vector2(values[offset + 0], values[offset + 1]);
    }

    private static Vector3 ReadVector3(float[] values, int vertex)
    {
        int offset = checked(vertex * 3);
        return new Vector3(values[offset + 0], values[offset + 1], values[offset + 2]);
    }
}

public static partial class ClusterBuilder
{


    public static MeshAsset ProcessRaw(
        Vector3[] rawPos,
        List<RawAttribute> rawAttributes,
        uint[] rawIndices,
        IReadOnlyList<MeshMaterialSlot> materialSlots,
        string name)
    {
        List<string> regionNames = materialSlots
            .Select(static (slot, index) => $"region_{index}")
            .ToList();
        return ProcessRaw(
            rawPos,
            rawAttributes,
            rawIndices,
            regionNames,
            name);
    }

    private static void BuildClusterLod(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices
    )
    {
        var locks = ArrayPool<byte>.Shared.Rent(positions.Length);
        var remap = ArrayPool<uint>.Shared.Rent(positions.Length);
        try
        {
            GeneratePositionRemap(positions, remap.AsSpan(0, positions.Length));
            Clusterize(config, indices, positions, materialIndicesArray, clusters, globalIndices);
            int nextGroupId = InitializeLeafClusters(positions, clusters, globalIndices);
            ReduceClusterLod(
                config,
                positions,
                materialIndicesArray,
                clusters,
                globalIndices,
                locks.AsSpan(0, positions.Length),
                remap.AsSpan(0, positions.Length),
                nextGroupId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(locks);
            ArrayPool<uint>.Shared.Return(remap);
        }
    }

    private static void GeneratePositionRemap(
        ReadOnlySpan<Vector3> positions,
        Span<uint> remap)
    {
        Meshopt.GeneratePositionRemap(
            remap,
            MemoryMarshal.Cast<Vector3, float>(positions),
            (nuint)Unsafe.SizeOf<Vector3>());
    }

    private static int InitializeLeafClusters(
        ReadOnlySpan<Vector3> positions,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices)
    {
        int nextGroupId = 0;
        for (int index = 0; index < clusters.Count; index++)
        {
            BuilderMeshlet cluster = clusters[index];
            ReadOnlySpan<uint> clusterIndices = CollectionsMarshal
                .AsSpan(globalIndices)
                .Slice(cluster.IndicesOffset, cluster.IndicesCount);
            ClusterLodBounds bounds = BoundsCompute(positions, clusterIndices, 0);
            clusters[index] = InitializeLeafCluster(cluster, bounds, nextGroupId++);
        }

        return nextGroupId;
    }

    private static BuilderMeshlet InitializeLeafCluster(
        BuilderMeshlet cluster,
        ClusterLodBounds bounds,
        int groupId)
    {
        cluster.Center = bounds.Center;
        cluster.Radius = bounds.Radius;
        cluster.LodCenter = bounds.Center;
        cluster.LodRadius = bounds.Radius;
        cluster.SelfLodCenter = bounds.Center;
        cluster.SelfLodRadius = bounds.Radius;
        cluster.Error = 0;
        cluster.Level = 0;
        cluster.GroupId = groupId;
        return cluster;
    }

    private static void ReduceClusterLod(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        Span<byte> locks,
        ReadOnlySpan<uint> remap,
        int nextGroupId)
    {
        List<int> pending = CreatePendingClusters(clusters.Count);
        var workspace = new LodWorkspace();
        int depth = 0;
        while (pending.Count > 1)
        {
            pending = ReduceClusterLodDepth(
                config,
                positions,
                materialIndicesArray,
                clusters,
                globalIndices,
                locks,
                remap,
                pending,
                workspace,
                ref nextGroupId,
                depth++);
        }

        AssignRootParentError(clusters, pending);
    }

    private static List<int> CreatePendingClusters(int clusterCount)
    {
        var pending = new List<int>(clusterCount);
        for (int index = 0; index < clusterCount; index++)
        {
            pending.Add(index);
        }

        return pending;
    }

    private static List<int> ReduceClusterLodDepth(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        Span<byte> locks,
        ReadOnlySpan<uint> remap,
        List<int> pending,
        LodWorkspace workspace,
        ref int nextGroupId,
        int depth)
    {
        Partition(config, positions, clusters, globalIndices, pending, remap, workspace.GroupOffsets);
        LockBoundary(locks, clusters, globalIndices, pending, workspace.GroupOffsets, remap);
        var nextPending = new List<int>();
        Span<int> pendingSpan = CollectionsMarshal.AsSpan(pending);
        for (int groupIndex = 0; groupIndex < workspace.GroupOffsets.Count - 1; groupIndex++)
        {
            Span<int> group = PendingGroup(pendingSpan, workspace.GroupOffsets, groupIndex);
            ReduceClusterGroup(
                config,
                positions,
                materialIndicesArray,
                clusters,
                globalIndices,
                locks,
                group,
                workspace,
                nextPending,
                ref nextGroupId,
                depth);
        }

        return nextPending;
    }

    private static Span<int> PendingGroup(
        Span<int> pending,
        List<int> groupOffsets,
        int groupIndex)
    {
        int start = groupOffsets[groupIndex];
        int count = groupOffsets[groupIndex + 1] - start;
        return pending.Slice(start, count);
    }

    private static void ReduceClusterGroup(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        Span<byte> locks,
        Span<int> group,
        LodWorkspace workspace,
        List<int> nextPending,
        ref int nextGroupId,
        int depth)
    {
        MergeGroupIndices(globalIndices, clusters, group, workspace.MergedIndices);
        int targetSize = (int)((workspace.MergedIndices.Count / 3) * config.SimplifyRatio) * 3;
        ClusterLodBounds groupBounds = BoundsMerge(clusters, group);
        Simplify(config, positions, CollectionsMarshal.AsSpan(workspace.MergedIndices), locks, targetSize, out float error, workspace.SimplifiedIndices);
        if (workspace.SimplifiedIndices.Count > workspace.MergedIndices.Count * config.SimplifyThreshold)
        {
            MarkUnmergedGroup(clusters, group);
            return;
        }

        float groupError = groupBounds.Error + error;
        int groupId = nextGroupId++;
        AssignGroupParent(clusters, group, groupBounds, groupError, groupId);
        BuildParentClusters(
            config,
            positions,
            materialIndicesArray,
            clusters,
            globalIndices,
            workspace.SimplifiedIndices,
            groupBounds,
            groupError,
            groupId,
            depth,
            nextPending);
    }

    private static void MergeGroupIndices(
        List<uint> globalIndices,
        List<BuilderMeshlet> clusters,
        Span<int> group,
        List<uint> destination)
    {
        destination.Clear();
        Span<uint> globalSpan = CollectionsMarshal.AsSpan(globalIndices);
        foreach (int clusterIndex in group)
        {
            BuilderMeshlet cluster = clusters[clusterIndex];
            ReadOnlySpan<uint> clusterIndices = globalSpan.Slice(cluster.IndicesOffset, cluster.IndicesCount);
            for (int index = 0; index < clusterIndices.Length; index++)
            {
                destination.Add(clusterIndices[index]);
            }
        }
    }

    private static void MarkUnmergedGroup(List<BuilderMeshlet> clusters, Span<int> group)
    {
        foreach (int index in group)
        {
            BuilderMeshlet cluster = clusters[index];
            cluster.ParentError = float.MaxValue;
            clusters[index] = cluster;
        }
    }

    private static void AssignGroupParent(
        List<BuilderMeshlet> clusters,
        Span<int> group,
        ClusterLodBounds bounds,
        float error,
        int groupId)
    {
        foreach (int index in group)
        {
            BuilderMeshlet cluster = clusters[index];
            cluster.ParentError = error;
            cluster.ParentGroupId = groupId;
            cluster.LodCenter = bounds.Center;
            cluster.LodRadius = bounds.Radius;
            clusters[index] = cluster;
        }
    }

    private static void BuildParentClusters(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        List<uint> simplifiedIndices,
        ClusterLodBounds bounds,
        float error,
        int groupId,
        int depth,
        List<int> nextPending)
    {
        int start = clusters.Count;
        Clusterize(config, CollectionsMarshal.AsSpan(simplifiedIndices), positions, materialIndicesArray, clusters, globalIndices);
        for (int index = start; index < clusters.Count; index++)
        {
            clusters[index] = InitializeParentCluster(
                clusters[index],
                positions,
                globalIndices,
                bounds,
                error,
                groupId,
                depth + 1);
            nextPending.Add(index);
        }
    }

    private static BuilderMeshlet InitializeParentCluster(
        BuilderMeshlet cluster,
        ReadOnlySpan<Vector3> positions,
        List<uint> globalIndices,
        ClusterLodBounds bounds,
        float error,
        int groupId,
        int level)
    {
        ReadOnlySpan<uint> clusterIndices = CollectionsMarshal
            .AsSpan(globalIndices)
            .Slice(cluster.IndicesOffset, cluster.IndicesCount);
        ClusterLodBounds selfBounds = BoundsCompute(positions, clusterIndices, 0);
        cluster.Level = level;
        cluster.Center = selfBounds.Center;
        cluster.Radius = selfBounds.Radius;
        cluster.Error = error;
        cluster.GroupId = groupId;
        cluster.LodCenter = bounds.Center;
        cluster.LodRadius = bounds.Radius;
        cluster.SelfLodCenter = bounds.Center;
        cluster.SelfLodRadius = bounds.Radius;
        return cluster;
    }

    private static void AssignRootParentError(List<BuilderMeshlet> clusters, List<int> pending)
    {
        if (pending.Count != 1)
        {
            return;
        }

        BuilderMeshlet cluster = clusters[pending[0]];
        cluster.ParentError = float.MaxValue;
        clusters[pending[0]] = cluster;
    }
}

public static partial class ClusterBuilder
{


    public static MeshAsset ProcessRaw(
        Vector3[] rawPos,
        List<RawAttribute> rawAttributes,
        uint[] rawIndices,
        List<string> regionNames,
        string name
    )
    {
        return ProcessRawCore(rawPos, rawAttributes, rawIndices, regionNames, name);
    }

    private static void PackAttribute(List<byte> output, RawAttribute attr, int index)
    {
        int baseIdx = index * attr.Dimension;

        for (int c = 0; c < attr.NumComponents; ++c)
        {
            float val = (c < attr.Dimension) ? attr.Data[baseIdx + c] : 0.0f;

            switch (attr.TargetType)
            {
                case ValueType.Int8:
                    if (attr.Normalized)
                        output.Add((byte)(sbyte)Math.Clamp(val * 127.0f, -128, 127));
                    else
                        output.Add((byte)(sbyte)Math.Clamp(val, -128, 127));
                    break;
                case ValueType.UInt8:
                    if (attr.Normalized)
                        output.Add((byte)Math.Clamp(val * 255.0f, 0, 255));
                    else
                        output.Add((byte)Math.Clamp(val, 0, 255));
                    break;
                case ValueType.Int16:
                    short s = attr.Normalized
                        ? (short)Math.Clamp(val * 32767.0f, -32768, 32767)
                        : (short)val;
                    output.Add((byte)(s & 0xFF));
                    output.Add((byte)((s >> 8) & 0xFF));
                    break;
                case ValueType.UInt16:
                    ushort us = attr.Normalized
                        ? (ushort)Math.Clamp(val * 65535.0f, 0, 65535)
                        : (ushort)val;
                    output.Add((byte)(us & 0xFF));
                    output.Add((byte)((us >> 8) & 0xFF));
                    break;
                case ValueType.Float16:
                    Half h = (Half)val;
                    ushort hs = BitConverter.HalfToUInt16Bits(h);
                    output.Add((byte)(hs & 0xFF));
                    output.Add((byte)((hs >> 8) & 0xFF));
                    break;
                case ValueType.Float32:
                    unsafe
                    {
                        uint u = *(uint*)&val;
                        output.Add((byte)(u & 0xFF));
                        output.Add((byte)((u >> 8) & 0xFF));
                        output.Add((byte)((u >> 16) & 0xFF));
                        output.Add((byte)((u >> 24) & 0xFF));
                    }
                    break;
                // TODO: Other types
            }
        }
    }

    private static unsafe nuint BuildRemap(
        Span<uint> destination,
        ReadOnlySpan<uint> indices,
        Vector3[] positions,
        IReadOnlyList<RawAttribute> attributes)
    {
        if (destination.Length < positions.Length)
            throw new ArgumentException("Vertex remap destination is smaller than the source vertex count.", nameof(destination));

        MeshOptimizer.Stream[] streams = new MeshOptimizer.Stream[checked(attributes.Count + 1)];
        GCHandle[] handles = new GCHandle[streams.Length];

        try
        {
            handles[0] = GCHandle.Alloc(positions, GCHandleType.Pinned);
            streams[0] = new MeshOptimizer.Stream(
                handles[0].AddrOfPinnedObject().ToPointer(),
                (nuint)Unsafe.SizeOf<Vector3>(),
                (nuint)Unsafe.SizeOf<Vector3>());

            for (int i = 0; i < attributes.Count; i++)
            {
                RawAttribute attribute = attributes[i];
                int requiredLength = checked(positions.Length * attribute.Dimension);
                if (attribute.Data.Length < requiredLength)
                {
                    throw new ArgumentException(
                        $"Attribute '{attribute.Name}' has {attribute.Data.Length} values, but {requiredLength} are required for {positions.Length} vertices.",
                        nameof(attributes));
                }

                handles[i + 1] = GCHandle.Alloc(attribute.Data, GCHandleType.Pinned);
                nuint stride = checked((nuint)(attribute.Dimension * sizeof(float)));
                streams[i + 1] = new MeshOptimizer.Stream(
                    handles[i + 1].AddrOfPinnedObject().ToPointer(),
                    stride,
                    stride);
            }

            return Meshopt.GenerateVertexRemapMulti<byte>(
                destination,
                indices,
                (nuint)positions.Length,
                streams);
        }
        finally
        {
            for (int i = 0; i < handles.Length; i++)
            {
                if (handles[i].IsAllocated)
                    handles[i].Free();
            }
        }
    }

    private struct TempTri
    {
        public uint v0, v1, v2;
        public byte mat;
    }

    private static void EmitSplitMeshlet(
        ReadOnlySpan<TempTri> tris,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices
    )
    {
        // Build VRB batch info (linear scan, no reorder)
        uint vrbBatchInfo = BuildVrb(tris);

        int startIndex = globalIndices.Count;
        var uniqueMats = new List<byte>();
        int range0End = 0, range1End = 0;

        var uniqueVerts = new HashSet<uint>();

        for (int i = 0; i < tris.Length; i++)
        {
            ref readonly var t = ref tris[i];
            if (!uniqueMats.Contains(t.mat))
            {
                uniqueMats.Add(t.mat);
                if (uniqueMats.Count == 2) range0End = i;
                if (uniqueMats.Count == 3) range1End = i;
            }
            globalIndices.Add(t.v0);
            globalIndices.Add(t.v1);
            globalIndices.Add(t.v2);
            uniqueVerts.Add(t.v0);
            uniqueVerts.Add(t.v1);
            uniqueVerts.Add(t.v2);
        }

        if (uniqueMats.Count < 2) range0End = tris.Length;
        if (uniqueMats.Count < 3) range1End = tris.Length;

        clusters.Add(
            new BuilderMeshlet
            {
                IndicesOffset = startIndex,
                IndicesCount = tris.Length * 3,
                VertexCount = uniqueVerts.Count,
                GroupId = -1,
                ParentGroupId = -1,
                Mat0 = uniqueMats.Count > 0 ? uniqueMats[0] : (byte)0,
                Mat1 = uniqueMats.Count > 1 ? uniqueMats[1] : (byte)0,
                Mat2 = uniqueMats.Count > 2 ? uniqueMats[2] : (byte)0,
                Range0End = (byte)range0End,
                Range1End = (byte)range1End,
                VRBBatchInfo = vrbBatchInfo,
            }
        );
    }
}

public static partial class ClusterBuilder
{



    /// <summary>
    /// Build VRB batch info by linearly scanning triangles and closing batches
    /// when unique vertex count exceeds 32. Preserves meshopt ordering (no reorder).
    /// Returns a packed uint encoding up to 5 batch tri-counts.
    /// Triangles beyond the 5th batch are implicitly a "slow residual" at runtime.
    /// </summary>
    private static uint BuildVrb(ReadOnlySpan<TempTri> tris)
    {
        const int MaxBatchesEncoded = 5;
        Span<int> batchTriCounts = stackalloc int[MaxBatchesEncoded + 16];
        var batchVerts = new HashSet<uint>();
        int batchIndex = 0;
        int currentBatchTriCount = 0;
        byte currentMat = tris[0].mat;

        for (int index = 0; index < tris.Length; index++)
        {
            ref readonly TempTri tri = ref tris[index];
            CloseVrbBatchAtMaterialBoundary(
                tri,
                ref currentMat,
                batchTriCounts,
                ref batchIndex,
                ref currentBatchTriCount,
                batchVerts);
            AddVrbTriangleToBatch(tri, batchTriCounts, ref batchIndex, ref currentBatchTriCount, batchVerts);
            CloseFullVrbBatch(batchTriCounts, ref batchIndex, ref currentBatchTriCount, batchVerts);
        }

        CloseVrbBatch(batchTriCounts, ref batchIndex, ref currentBatchTriCount, batchVerts);
        return PackVrbBatchCounts(batchTriCounts, batchIndex, MaxBatchesEncoded);
    }

    private static void CloseVrbBatchAtMaterialBoundary(
        TempTri tri,
        ref byte currentMat,
        Span<int> batchTriCounts,
        ref int batchIndex,
        ref int currentBatchTriCount,
        HashSet<uint> batchVerts)
    {
        if (tri.mat != currentMat && currentBatchTriCount > 0)
        {
            CloseVrbBatch(batchTriCounts, ref batchIndex, ref currentBatchTriCount, batchVerts);
            currentMat = tri.mat;
        }
    }

    private static void AddVrbTriangleToBatch(
        TempTri tri,
        Span<int> batchTriCounts,
        ref int batchIndex,
        ref int currentBatchTriCount,
        HashSet<uint> batchVerts)
    {
        if (VrbVertexCountAfterAdd(batchVerts, tri) > 32 && currentBatchTriCount > 0)
        {
            CloseVrbBatch(batchTriCounts, ref batchIndex, ref currentBatchTriCount, batchVerts);
        }

        AddVrbTriangleVertices(batchVerts, tri);
        currentBatchTriCount++;
    }

    private static int VrbVertexCountAfterAdd(HashSet<uint> batchVerts, TempTri tri)
    {
        int count = batchVerts.Count;
        if (!batchVerts.Contains(tri.v0)) count++;
        if (!batchVerts.Contains(tri.v1)) count++;
        if (!batchVerts.Contains(tri.v2)) count++;
        return count;
    }

    private static void AddVrbTriangleVertices(HashSet<uint> batchVerts, TempTri tri)
    {
        batchVerts.Add(tri.v0);
        batchVerts.Add(tri.v1);
        batchVerts.Add(tri.v2);
    }

    private static void CloseFullVrbBatch(
        Span<int> batchTriCounts,
        ref int batchIndex,
        ref int currentBatchTriCount,
        HashSet<uint> batchVerts)
    {
        if (currentBatchTriCount == 32)
        {
            CloseVrbBatch(batchTriCounts, ref batchIndex, ref currentBatchTriCount, batchVerts);
        }
    }

    private static void CloseVrbBatch(
        Span<int> batchTriCounts,
        ref int batchIndex,
        ref int currentBatchTriCount,
        HashSet<uint> batchVerts)
    {
        if (currentBatchTriCount <= 0)
        {
            return;
        }

        batchTriCounts[batchIndex++] = currentBatchTriCount;
        currentBatchTriCount = 0;
        batchVerts.Clear();
    }

    private static uint PackVrbBatchCounts(
        Span<int> batchTriCounts,
        int batchIndex,
        int maxBatchesEncoded)
    {
        int encodedCount = Math.Min(batchIndex, maxBatchesEncoded);
        uint packed = 0;
        for (int index = 0; index < encodedCount; index++)
        {
            packed |= (uint)(batchTriCounts[index] - 1) << (index * 5);
        }

        return packed | ((uint)(encodedCount - 1) << 25);
    }



    private static void Clusterize(
        ClusterLodConfig config,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<Vector3> positions,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices
    )
    {
        if (indices.IsEmpty)
        {
            return;
        }

        MeshletScratch scratch = RentMeshletScratch(config, indices.Length);
        try
        {
            nuint meshletCount = BuildMeshlets(config, indices, positions, scratch);
            EmitMeshlets(scratch, meshletCount, materialIndicesArray, clusters, globalIndices, config.OptimizeClusters);
        }
        finally
        {
            scratch.Return();
        }
    }

    private static MeshletScratch RentMeshletScratch(ClusterLodConfig config, int indexCount)
    {
        nuint maxMeshlets = Meshopt.BuildMeshletsBound(
            (nuint)indexCount,
            (nuint)config.MaxVertices,
            (nuint)config.MaxTriangles);
        return new MeshletScratch(
            ArrayPool<MeshOptimizer.Meshlet>.Shared.Rent((int)maxMeshlets),
            ArrayPool<uint>.Shared.Rent((int)maxMeshlets * config.MaxVertices),
            ArrayPool<byte>.Shared.Rent((int)maxMeshlets * config.MaxTriangles * 3));
    }

    private static nuint BuildMeshlets(
        ClusterLodConfig config,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<Vector3> positions,
        MeshletScratch scratch)
    {
        ReadOnlySpan<float> positionSpan = MemoryMarshal.Cast<Vector3, float>(positions);
        return config.ClusterSpatial
            ? BuildSpatialMeshlets(config, indices, positionSpan, scratch)
            : BuildFlexibleMeshlets(config, indices, positionSpan, scratch);
    }

    private static nuint BuildSpatialMeshlets(
        ClusterLodConfig config,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<float> positions,
        MeshletScratch scratch)
        => Meshopt.BuildMeshletsSpatial(
            scratch.Meshlets.AsSpan(),
            scratch.Vertices.AsSpan(),
            scratch.Triangles.AsSpan(),
            indices,
            positions,
            (nuint)Unsafe.SizeOf<Vector3>(),
            (nuint)config.MaxVertices,
            (nuint)config.MinTriangles,
            (nuint)config.MaxTriangles,
            config.ClusterFillWeight);

    private static nuint BuildFlexibleMeshlets(
        ClusterLodConfig config,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<float> positions,
        MeshletScratch scratch)
        => Meshopt.BuildMeshletsFlex(
            scratch.Meshlets.AsSpan(),
            scratch.Vertices.AsSpan(),
            scratch.Triangles.AsSpan(),
            indices,
            positions,
            (nuint)Unsafe.SizeOf<Vector3>(),
            (nuint)config.MaxVertices,
            (nuint)config.MinTriangles,
            (nuint)config.MaxTriangles,
            0.0f,
            config.ClusterSplitFactor);

    private static void EmitMeshlets(
        MeshletScratch scratch,
        nuint meshletCount,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        bool optimizeClusters)
    {
        for (int index = 0; index < (int)meshletCount; index++)
        {
            EmitMeshlet(scratch, index, materialIndicesArray, clusters, globalIndices, optimizeClusters);
        }
    }

    private static void EmitMeshlet(
        MeshletScratch scratch,
        int meshletIndex,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        bool optimizeClusters)
    {
        ref MeshOptimizer.Meshlet meshlet = ref scratch.Meshlets[meshletIndex];
        if (optimizeClusters)
        {
            OptimizeMeshlet(scratch, meshlet);
        }

        TempTri[] triangles = ReadMeshletTriangles(scratch, meshlet, materialIndicesArray);
        SortTrianglesByMaterial(triangles);
        EmitMaterialChunks(triangles, clusters, globalIndices);
    }

    private static void OptimizeMeshlet(MeshletScratch scratch, MeshOptimizer.Meshlet meshlet)
    {
        Meshopt.OptimizeMeshlet(
            scratch.Vertices.AsSpan((int)meshlet.vertex_offset, (int)meshlet.vertex_count),
            scratch.Triangles.AsSpan((int)meshlet.triangle_offset, (int)meshlet.triangle_count * 3),
            meshlet.triangle_count,
            meshlet.vertex_count);
    }

    private static TempTri[] ReadMeshletTriangles(
        MeshletScratch scratch,
        MeshOptimizer.Meshlet meshlet,
        float[] materialIndicesArray)
    {
        TempTri[] triangles = new TempTri[meshlet.triangle_count];
        for (uint triangleIndex = 0; triangleIndex < meshlet.triangle_count; triangleIndex++)
        {
            triangles[triangleIndex] = ReadMeshletTriangle(scratch, meshlet, triangleIndex, materialIndicesArray);
        }

        return triangles;
    }

    private static TempTri ReadMeshletTriangle(
        MeshletScratch scratch,
        MeshOptimizer.Meshlet meshlet,
        uint triangleIndex,
        float[] materialIndicesArray)
    {
        int triangleOffset = (int)meshlet.triangle_offset + (int)triangleIndex * 3;
        uint v0 = scratch.Vertices[(int)meshlet.vertex_offset + scratch.Triangles[triangleOffset + 0]];
        uint v1 = scratch.Vertices[(int)meshlet.vertex_offset + scratch.Triangles[triangleOffset + 1]];
        uint v2 = scratch.Vertices[(int)meshlet.vertex_offset + scratch.Triangles[triangleOffset + 2]];
        return new TempTri { v0 = v0, v1 = v1, v2 = v2, mat = (byte)materialIndicesArray[v0] };
    }

    private static void SortTrianglesByMaterial(TempTri[] triangles)
    {
        List<TempTri>[] buckets = new List<TempTri>[256];
        for (int index = 0; index < triangles.Length; index++)
        {
            byte material = triangles[index].mat;
            buckets[material] ??= [];
            buckets[material].Add(triangles[index]);
        }

        CopyMaterialBuckets(buckets, triangles);
    }

    private static void CopyMaterialBuckets(List<TempTri>[] buckets, TempTri[] triangles)
    {
        int destination = 0;
        for (int material = 0; material < buckets.Length; material++)
        {
            if (buckets[material] == null)
            {
                continue;
            }

            foreach (TempTri triangle in buckets[material])
            {
                triangles[destination++] = triangle;
            }
        }
    }
}

public static partial class ClusterBuilder
{


    private static void EmitMaterialChunks(
        TempTri[] triangles,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices)
    {
        var uniqueMaterials = new List<byte>();
        int chunkStart = 0;
        for (int index = 0; index < triangles.Length; index++)
        {
            if (TryStartMaterialChunk(triangles, index, uniqueMaterials, ref chunkStart, clusters, globalIndices))
            {
                continue;
            }
        }

        EmitRemainingMaterialChunk(triangles, chunkStart, clusters, globalIndices);
    }

    private static bool TryStartMaterialChunk(
        TempTri[] triangles,
        int index,
        List<byte> uniqueMaterials,
        ref int chunkStart,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices)
    {
        if (uniqueMaterials.Contains(triangles[index].mat))
        {
            return false;
        }

        if (uniqueMaterials.Count == 3)
        {
            EmitSplitMeshlet(new ReadOnlySpan<TempTri>(triangles, chunkStart, index - chunkStart), clusters, globalIndices);
            uniqueMaterials.Clear();
            chunkStart = index;
        }

        uniqueMaterials.Add(triangles[index].mat);
        return true;
    }

    private static void EmitRemainingMaterialChunk(
        TempTri[] triangles,
        int chunkStart,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices)
    {
        if (chunkStart < triangles.Length)
        {
            EmitSplitMeshlet(new ReadOnlySpan<TempTri>(triangles, chunkStart, triangles.Length - chunkStart), clusters, globalIndices);
        }
    }

    private readonly record struct MeshletScratch(
        MeshOptimizer.Meshlet[] Meshlets,
        uint[] Vertices,
        byte[] Triangles)
    {
        public void Return()
        {
            ArrayPool<MeshOptimizer.Meshlet>.Shared.Return(Meshlets);
            ArrayPool<uint>.Shared.Return(Vertices);
            ArrayPool<byte>.Shared.Return(Triangles);
        }
    }



    private static int Partition(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        List<int> pending,
        ReadOnlySpan<uint> remap,
        List<int> groupOffsets
    )
    {
        groupOffsets.Clear();
        if (pending.Count <= config.PartitionSize)
        {
            AddSinglePartition(pending, groupOffsets);
            return 1;
        }

        int totalIndexCount = TotalPartitionIndexCount(clusters, pending);
        PartitionScratch scratch = RentPartitionScratch(pending.Count, totalIndexCount);
        try
        {
            FillPartitionInputs(scratch, clusters, globalIndices, pending, remap);
            nuint partitionCount = ExecutePartition(config, positions, scratch, totalIndexCount, pending.Count);
            scratch.Remap = CreatePartitionRemap(config, positions, clusters, pending, scratch.Partitions, partitionCount);
            ApplyPartitionOrder(pending, groupOffsets, scratch, partitionCount);
            return (int)partitionCount;
        }
        finally
        {
            scratch.Return();
        }
    }

    private static void AddSinglePartition(List<int> pending, List<int> groupOffsets)
    {
        groupOffsets.Add(0);
        groupOffsets.Add(pending.Count);
    }

    private static int TotalPartitionIndexCount(List<BuilderMeshlet> clusters, List<int> pending)
    {
        int total = 0;
        for (int index = 0; index < pending.Count; index++)
        {
            total += clusters[pending[index]].IndicesCount;
        }

        return total;
    }

    private static PartitionScratch RentPartitionScratch(int pendingCount, int totalIndexCount)
    {
        return new PartitionScratch(
            ArrayPool<uint>.Shared.Rent(totalIndexCount),
            ArrayPool<uint>.Shared.Rent(pendingCount),
            ArrayPool<uint>.Shared.Rent(pendingCount),
            ArrayPool<int>.Shared.Rent(pendingCount));
    }

    private static void FillPartitionInputs(
        PartitionScratch scratch,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        List<int> pending,
        ReadOnlySpan<uint> remap)
    {
        int offset = 0;
        Span<uint> globalIndicesSpan = CollectionsMarshal.AsSpan(globalIndices);
        for (int index = 0; index < pending.Count; index++)
        {
            BuilderMeshlet cluster = clusters[pending[index]];
            scratch.Counts[index] = (uint)cluster.IndicesCount;
            offset = CopyPartitionIndices(cluster, globalIndicesSpan, remap, scratch.Indices, offset);
        }
    }

    private static int CopyPartitionIndices(
        BuilderMeshlet cluster,
        Span<uint> globalIndices,
        ReadOnlySpan<uint> remap,
        uint[] destination,
        int offset)
    {
        ReadOnlySpan<uint> source = globalIndices.Slice(cluster.IndicesOffset, cluster.IndicesCount);
        for (int index = 0; index < source.Length; index++)
        {
            destination[offset++] = remap[(int)source[index]];
        }

        return offset;
    }

    private static nuint ExecutePartition(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        PartitionScratch scratch,
        int totalIndexCount,
        int pendingCount)
    {
        ReadOnlySpan<float> positionSpan = MemoryMarshal.Cast<Vector3, float>(positions);
        return Meshopt.PartitionClusters(
            scratch.Partitions.AsSpan(0, pendingCount),
            scratch.Indices.AsSpan(0, totalIndexCount),
            scratch.Counts.AsSpan(0, pendingCount),
            config.PartitionSpatial ? positionSpan : default,
            (nuint)Unsafe.SizeOf<Vector3>(),
            (nuint)config.PartitionSize);
    }

    private static uint[]? CreatePartitionRemap(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        List<BuilderMeshlet> clusters,
        List<int> pending,
        uint[] clusterPart,
        nuint partitionCount)
    {
        if (!config.PartitionSort)
        {
            return null;
        }

        float[] partitionPoints = ArrayPool<float>.Shared.Rent((int)partitionCount * 3);
        uint[] partitionRemap = ArrayPool<uint>.Shared.Rent((int)partitionCount);
        try
        {
            FillPartitionPoints(partitionPoints, clusters, pending, clusterPart);
            Meshopt.SpatialSortRemap(
                partitionRemap.AsSpan(0, (int)partitionCount),
                partitionPoints.AsSpan(0, (int)partitionCount * 3),
                (nuint)Unsafe.SizeOf<Vector3>());
            return partitionRemap;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(partitionPoints);
        }
    }

    private static void FillPartitionPoints(
        float[] partitionPoints,
        List<BuilderMeshlet> clusters,
        List<int> pending,
        uint[] clusterPart)
    {
        for (int index = 0; index < pending.Count; index++)
        {
            Vector3 center = clusters[pending[index]].Center;
            int offset = (int)clusterPart[index] * 3;
            partitionPoints[offset + 0] = center.X;
            partitionPoints[offset + 1] = center.Y;
            partitionPoints[offset + 2] = center.Z;
        }
    }

    private static void ApplyPartitionOrder(
        List<int> pending,
        List<int> groupOffsets,
        PartitionScratch scratch,
        nuint partitionCount)
    {
        int count = (int)partitionCount;
        int[] partitionSizes = ArrayPool<int>.Shared.Rent(count);
        int[] offsets = ArrayPool<int>.Shared.Rent(count);
        try
        {
            Array.Clear(partitionSizes, 0, count);
            CountPartitionSizes(scratch, pending.Count, partitionSizes);
            FillPartitionOffsets(partitionSizes, count, offsets, groupOffsets);
            CopySortedPending(pending, scratch, offsets);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(partitionSizes);
            ArrayPool<int>.Shared.Return(offsets);
        }
    }

    private static void CountPartitionSizes(
        PartitionScratch scratch,
        int pendingCount,
        int[] partitionSizes)
    {
        for (int index = 0; index < pendingCount; index++)
        {
            uint partitionId = RemappedPartitionId(scratch, index);
            partitionSizes[(int)partitionId]++;
        }
    }

    private static uint RemappedPartitionId(PartitionScratch scratch, int index)
    {
        uint partitionId = scratch.Partitions[index];
        return scratch.Remap == null ? partitionId : scratch.Remap[partitionId];
    }

    private static void FillPartitionOffsets(
        int[] partitionSizes,
        int partitionCount,
        int[] offsets,
        List<int> groupOffsets)
    {
        int runningOffset = 0;
        groupOffsets.Add(0);
        for (int index = 0; index < partitionCount; index++)
        {
            offsets[index] = runningOffset;
            runningOffset += partitionSizes[index];
            groupOffsets.Add(runningOffset);
        }
    }

    private static void CopySortedPending(
        List<int> pending,
        PartitionScratch scratch,
        int[] offsets)
    {
        for (int index = 0; index < pending.Count; index++)
        {
            uint partitionId = RemappedPartitionId(scratch, index);
            int destination = offsets[partitionId]++;
            scratch.SortedPending[destination] = pending[index];
        }

        new Span<int>(scratch.SortedPending, 0, pending.Count).CopyTo(CollectionsMarshal.AsSpan(pending));
    }
}

public static partial class ClusterBuilder
{


    private sealed class PartitionScratch
    {
        public PartitionScratch(uint[] indices, uint[] counts, uint[] partitions, int[] sortedPending)
        {
            Indices = indices;
            Counts = counts;
            Partitions = partitions;
            SortedPending = sortedPending;
        }

        public uint[] Indices { get; }
        public uint[] Counts { get; }
        public uint[] Partitions { get; }
        public int[] SortedPending { get; }
        public uint[]? Remap { get; set; }

        public void Return()
        {
            ArrayPool<uint>.Shared.Return(Indices);
            ArrayPool<uint>.Shared.Return(Counts);
            ArrayPool<uint>.Shared.Return(Partitions);
            ArrayPool<int>.Shared.Return(SortedPending);
            if (Remap != null)
            {
                ArrayPool<uint>.Shared.Return(Remap);
            }
        }
    }



    private static void LockBoundary(
        Span<byte> locks,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        List<int> pending,
        List<int> groupOffsets,
        ReadOnlySpan<uint> remap
    )
    {
        const byte LockBit = 1 << 0;
        const byte SeenBit = 1 << 7;
        const byte SimplifyProtect = 2; // meshopt_SimplifyVertex_Protect

        for (int i = 0; i < locks.Length; i++)
            locks[i] &= unchecked((byte)~(LockBit | SeenBit));

        var globalIndicesSpan = CollectionsMarshal.AsSpan(globalIndices);

        for (int g = 0; g < groupOffsets.Count - 1; g++)
        {
            int start = groupOffsets[g];
            int count = groupOffsets[g + 1] - start;
            var group = CollectionsMarshal.AsSpan(pending).Slice(start, count);

            foreach (int clusterIdx in group)
            {
                var c = clusters[clusterIdx];
                var indices = globalIndicesSpan.Slice(c.IndicesOffset, c.IndicesCount);
                foreach (var v in indices)
                {
                    uint r = remap[(int)v];
                    locks[(int)r] |= (byte)((locks[(int)r] & SeenBit) >> 7);
                }
            }
            foreach (int clusterIdx in group)
            {
                var c = clusters[clusterIdx];
                var indices = globalIndicesSpan.Slice(c.IndicesOffset, c.IndicesCount);
                foreach (var v in indices)
                {
                    uint r = remap[(int)v];
                    locks[(int)r] |= SeenBit;
                }
            }
        }

        for (int i = 0; i < locks.Length; i++)
        {
            locks[i] = (byte)((locks[i] & LockBit) | (locks[i] & SimplifyProtect));
        }
    }

    private static void Simplify(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<byte> locks,
        int targetCount,
        out float error,
        List<uint> outputIndices
    )
    {
        if (targetCount >= indices.Length)
        {
            CopySimplifiedIndices(indices, outputIndices);
            error = 0;
            return;
        }

        uint[] simplified = ArrayPool<uint>.Shared.Rent(indices.Length);
        try
        {
            nuint newCount = SimplifyWithFallback(
                config,
                positions,
                indices,
                locks,
                targetCount,
                simplified,
                out error);
            error = ClampSimplifyError(config, positions, indices, error);
            CopySimplifiedIndices(simplified.AsSpan(0, (int)newCount), outputIndices);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(simplified);
        }
    }

    private static nuint SimplifyWithFallback(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<byte> locks,
        int targetCount,
        uint[] simplified,
        out float error)
    {
        ReadOnlySpan<float> positionSpan = MemoryMarshal.Cast<Vector3, float>(positions);
        nuint newCount = Meshopt.SimplifyWithAttributes(
            simplified.AsSpan(),
            indices,
            positionSpan,
            (nuint)Unsafe.SizeOf<Vector3>(),
            null,
            0,
            null,
            0,
            locks,
            (nuint)targetCount,
            float.MaxValue,
            SimplifyOptions(config),
            out error);
        return TrySloppySimplify(config, indices, positionSpan, targetCount, simplified, newCount, ref error);
    }

    private static SimplificationOptions SimplifyOptions(ClusterLodConfig config)
    {
        var options = SimplificationOptions.SimplifyLockBorder;
        options |= (SimplificationOptions)2;
        options |= (SimplificationOptions)4;
        if (config.SimplifyPermissive)
        {
            options |= (SimplificationOptions)32;
        }

        if (config.SimplifyRegularize)
        {
            options |= (SimplificationOptions)16;
        }

        return options;
    }

    private static nuint TrySloppySimplify(
        ClusterLodConfig config,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<float> positions,
        int targetCount,
        uint[] simplified,
        nuint newCount,
        ref float error)
    {
        if (newCount <= (nuint)targetCount || !config.SimplifyFallbackSloppy)
        {
            return newCount;
        }

        nuint fallbackCount = Meshopt.SimplifySloppy(
            simplified.AsSpan(),
            indices,
            positions,
            (nuint)Unsafe.SizeOf<Vector3>(),
            (nuint)targetCount,
            float.MaxValue,
            out error);
        error *= config.SimplifyErrorFactorSloppy;
        return fallbackCount;
    }

    private static float ClampSimplifyError(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        float error)
    {
        if (config.SimplifyErrorEdgeLimit <= 0)
        {
            return error;
        }

        float maxEdgeSq = MaxTriangleEdgeMetric(positions, indices);
        return Math.Min(error, (float)Math.Sqrt(maxEdgeSq) * config.SimplifyErrorEdgeLimit);
    }

    private static float MaxTriangleEdgeMetric(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices)
    {
        float maxEdgeSq = 0;
        for (int index = 0; index < indices.Length; index += 3)
        {
            maxEdgeSq = Math.Max(maxEdgeSq, TriangleEdgeMetric(positions, indices, index));
        }

        return maxEdgeSq;
    }

    private static float TriangleEdgeMetric(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        int index)
    {
        Vector3 va = positions[(int)indices[index + 0]];
        Vector3 vb = positions[(int)indices[index + 1]];
        Vector3 vc = positions[(int)indices[index + 2]];
        float eab = Vector3.DistanceSquared(va, vb);
        float eac = Vector3.DistanceSquared(va, vc);
        float ebc = Vector3.DistanceSquared(vb, vc);
        float maxEdge = Math.Max(Math.Max(eab, eac), ebc);
        float minEdge = Math.Min(Math.Min(eab, eac), ebc);
        return Math.Max(minEdge, maxEdge / 4.0f);
    }

    private static void CopySimplifiedIndices(
        ReadOnlySpan<uint> indices,
        List<uint> outputIndices)
    {
        foreach (uint index in indices)
        {
            outputIndices.Add(index);
        }
    }



    private static ClusterLodBounds BoundsCompute(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        float error
    )
    {
        var posSpan = MemoryMarshal.Cast<Vector3, float>(positions);
        var b = Meshopt.ComputeClusterBounds(indices, posSpan, (nuint)Unsafe.SizeOf<Vector3>());

        Vector3 center;
        unsafe
        {
            center = *(Vector3*)b.center;
        }

        return new ClusterLodBounds
        {
            Center = center,
            Radius = b.radius,
            Error = error,
        };
    }

    private static ClusterLodBounds BoundsMerge(
        List<BuilderMeshlet> clusters,
        ReadOnlySpan<int> group
    )
    {
        var centers = ArrayPool<float>.Shared.Rent(group.Length * 3);
        var radii = ArrayPool<float>.Shared.Rent(group.Length);

        try
        {
            float maxError = 0;
            for (int i = 0; i < group.Length; i++)
            {
                var c = clusters[group[i]];
                centers[i * 3 + 0] = c.SelfLodCenter.X;
                centers[i * 3 + 1] = c.SelfLodCenter.Y;
                centers[i * 3 + 2] = c.SelfLodCenter.Z;
                radii[i] = c.SelfLodRadius;
                maxError = Math.Max(maxError, c.Error);
            }

            var merged = Meshopt.ComputeSphereBounds(
                centers.AsSpan(0, group.Length * 3),
                sizeof(float) * 3,
                radii.AsSpan(0, group.Length),
                sizeof(float)
            );

            Vector3 mergedCenter;
            unsafe
            {
                mergedCenter = *(Vector3*)merged.center;
            }
            return new ClusterLodBounds
            {
                Center = mergedCenter,
                Radius = merged.radius,
                Error = maxError,
            };
        }
        finally
        {
            ArrayPool<float>.Shared.Return(centers);
            ArrayPool<float>.Shared.Return(radii);
        }
    }
}

