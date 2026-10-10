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
using SomeEngine.Resources.Geometry;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using GltfMesh = SharpGLTF.Schema2.Mesh;
using MeshAsset = SomeEngine.Assets.Schema.MeshAsset;
using ValueType = SomeEngine.Resources.Geometry.ValueType;

namespace SomeEngine.Assets.Importers;

public sealed class ClusterBuilderOptions
{
    public bool GenerateMissingTangents { get; init; }
    public IClusterVertexEncoder VertexEncoder { get; init; } = StandardClusterVertexEncoder.Instance;
}

public readonly record struct ClusterLodConfig(int MaxVertices, int MaxTriangles)
{
    public static ClusterLodConfig GetDefault(int maxTriangles = 124) => new(64, maxTriangles);
}

public static partial class ClusterBuilder
{
    private const int MaxVerticesPerMeshlet = 64;
    private const int MaxTrianglesPerMeshlet = 124;
    private const int PageSize = 128 * 1024; // 128KB
    private const int PageHeaderSize = MeshPageHeader.Size;

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

        public uint[] MaterialRanges;
        public uint[] VertReuseBatchWords;
    }

    private struct ClusterLodBounds
    {
        public Vector3 Center;
        public float Radius;
        public float Error;
    }

    private struct ClusterInfo
    {
        public Vector3 BoundMin;
        public Vector3 BoundMax;
        public Vector4 LODSphere; // xyz: center, w: radius
        public float LODError;
        public float OwnError;
        public int Level;
        public uint PageIndex;
        public uint ClusterStart;
        public int GroupId;
        public int ParentGroupId;
    }

    private sealed class MeshAssemblyState
    {
        public List<Vector3> Positions { get; } = [];
        public List<uint> Indices { get; } = [];
        public Dictionary<string, List<float>> Attributes { get; } = [];
        public List<float> MaterialIndices { get; } = [];
        public List<MeshMaterialSlot> MaterialSlots { get; } = [];
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


}

public static partial class ClusterBuilder
{


    public static MeshAsset Process(string filePath, Func<string, MeshMaterialSlot> materialResolver)
    {
        ArgumentNullException.ThrowIfNull(materialResolver);
        var model = ModelRoot.Load(filePath);
        var mesh = model.LogicalMeshes[0];
        IReadOnlyList<MeshMaterialSlot> materialSlots = mesh.Primitives
            .Select(primitive => materialResolver(primitive.Material?.Name ?? string.Empty))
            .ToArray();
        return ProcessMesh(mesh, materialSlots, mesh.Name ?? "Unnamed");
    }

    public static MeshAsset ProcessMesh(
        GltfMesh mesh,
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
            name,
            options?.VertexEncoder ?? StandardClusterVertexEncoder.Instance);
    }

    private static MeshProcessingData BuildMeshProcessingData(
        GltfMesh mesh,
        IReadOnlyList<MeshMaterialSlot> materialSlots)
    {
        AttributeDefinition[] definitions = ReadAttributeDefinitions(mesh);
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

    private static AttributeDefinition[] ReadAttributeDefinitions(GltfMesh mesh)
    {
        var definitions = new Dictionary<string, AttributeDefinition>(StringComparer.Ordinal);
        foreach (MeshPrimitive primitive in mesh.Primitives)
        foreach ((string key, Accessor accessor) in primitive.VertexAccessors)
        {
            if (key == "POSITION")
            {
                continue;
            }

            AttributeDefinition definition = CreateAttributeDefinition(key, accessor);
            if (definitions.TryGetValue(key, out AttributeDefinition previous))
            {
                if (previous.Dimension != definition.Dimension)
                    throw new NotSupportedException($"Mesh '{mesh.Name}' uses different dimensions for '{key}' across primitives.");
            }
            else definitions.Add(key, definition);
        }

        return definitions.Values.ToArray();
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
                : default);
        AddMaterialIndices(state.MaterialIndices, primitiveIndex, positions.Length);
        return vertexOffset + (uint)positions.Length;
    }

    private static void AppendPrimitiveIndices(
        List<uint> indices,
        MeshPrimitive primitive,
        uint vertexOffset)
    {
        if (primitive.DrawPrimitiveType is not (PrimitiveType.TRIANGLES or PrimitiveType.TRIANGLE_STRIP or PrimitiveType.TRIANGLE_FAN))
            throw new NotSupportedException($"Cluster geometry requires triangles, received {primitive.DrawPrimitiveType}.");
        foreach (var (a, b, c) in primitive.GetTriangleIndices())
        {
            indices.Add(checked((uint)a + vertexOffset));
            indices.Add(checked((uint)b + vertexOffset));
            indices.Add(checked((uint)c + vertexOffset));
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
        string name,
        IClusterVertexEncoder? vertexEncoder = null)
    {
        return ProcessRawCore(
            rawPos,
            rawAttributes,
            rawIndices,
            materialSlots,
            name,
            vertexEncoder ?? StandardClusterVertexEncoder.Instance);
    }

    private static void BuildClusterLod(ClusterLodConfig config, ClusterMeshData mesh,
        ReadOnlySpan<uint> indices, List<BuilderMeshlet> clusters, List<uint> globalIndices)
    {
        var attributes = new ClusterSimplificationAttributes(mesh);
        HashSet<Vector3> materialBoundaries = FindMaterialBoundaries(mesh.Positions.AsSpan(0, mesh.VertexCount),
            ExtractMaterialIndices(mesh));
        Clusterize(config, indices, mesh.Positions.AsSpan(0, mesh.VertexCount), ExtractMaterialIndices(mesh), clusters, globalIndices);
        int nextGroupId = InitializeLeafClusters(mesh.Positions.AsSpan(0, mesh.VertexCount), clusters, globalIndices);
        List<int> current = Enumerable.Range(0, clusters.Count).ToList();
        var mergedIndices = new List<uint>();
        while (current.Count > 1)
        {
            List<int[]> groups = PartitionClusterGroups(mesh.Positions.AsSpan(0, mesh.VertexCount), clusters, globalIndices, current);
            byte[] locks = LockGroupBoundaries(mesh.Positions, mesh.VertexCount, clusters, globalIndices, current,
                groups, materialBoundaries);
            var next = new List<int>();
            foreach (int[] groupIndices in groups)
            {
                int[] group = groupIndices.Select(index => current[index]).ToArray();
                if (group.Length == 1) { next.Add(group[0]); continue; }
                MergeGroupIndices(globalIndices, clusters, group, mergedIndices);
                ClusterLodBounds groupBounds = BoundsMerge(clusters, group);
                int parentCount = Math.Max(1, (mergedIndices.Count / 3 + config.MaxTriangles * 2 - 1) / (config.MaxTriangles * 2));
                int targetClusterSize = config.MaxTriangles - 2;
                float error = groupBounds.Error;
                var parents = new List<BuilderMeshlet>();
                var parentIndices = new List<uint>();
                var reducedIndices = new List<uint>();
                float[] attributeWeights = attributes.GroupWeights(mesh.Positions.AsSpan(0, mesh.VertexCount),
                    CollectionsMarshal.AsSpan(mergedIndices));
                do
                {
                    Simplify(mesh.Positions.AsSpan(0, mesh.VertexCount),
                        CollectionsMarshal.AsSpan(mergedIndices), locks, parentCount * targetClusterSize * 3,
                        attributes.Values, attributeWeights, out float reductionError, reducedIndices);
                    error = Math.Max(error, reductionError);
                    parents.Clear();
                    parentIndices.Clear();
                    Clusterize(config, CollectionsMarshal.AsSpan(reducedIndices),
                        mesh.Positions.AsSpan(0, mesh.VertexCount), ExtractMaterialIndices(mesh), parents, parentIndices);
                    if (parents.Count <= parentCount) break;
                    targetClusterSize -= 2;
                } while (targetClusterSize > config.MaxTriangles / 2);
                if (parents.Count >= group.Length)
                {
                    foreach (int index in group)
                    {
                        BuilderMeshlet root = clusters[index];
                        root.ParentError = float.MaxValue;
                        clusters[index] = root;
                    }
                    continue;
                }
                int groupId = nextGroupId++;
                foreach (int index in group)
                {
                    BuilderMeshlet child = clusters[index];
                    child.ParentError = error; child.ParentGroupId = groupId;
                    child.LodCenter = groupBounds.Center; child.LodRadius = groupBounds.Radius;
                    clusters[index] = child;
                }
                int firstParent = clusters.Count;
                int firstIndex = globalIndices.Count;
                foreach (uint index in parentIndices)
                    globalIndices.Add(index);
                foreach (BuilderMeshlet data in parents)
                {
                    BuilderMeshlet parent = data;
                    parent.IndicesOffset += firstIndex;
                    clusters.Add(parent);
                }
                int level = group.Max(index => clusters[index].Level) + 1;
                for (int index = firstParent; index < clusters.Count; index++)
                {
                    BuilderMeshlet parent = clusters[index];
                    ClusterLodBounds bounds = BoundsCompute(mesh.Positions.AsSpan(0, mesh.VertexCount),
                        CollectionsMarshal.AsSpan(globalIndices).Slice(parent.IndicesOffset, parent.IndicesCount), 0);
                    parent.Level = level; parent.Error = error; parent.GroupId = groupId; parent.ParentGroupId = -1;
                    parent.Center = bounds.Center; parent.Radius = bounds.Radius;
                    parent.LodCenter = parent.SelfLodCenter = groupBounds.Center;
                    parent.LodRadius = parent.SelfLodRadius = groupBounds.Radius;
                    clusters[index] = parent;
                    next.Add(index);
                }
            }
            current = next;
        }
        foreach (int index in current)
        {
            BuilderMeshlet root = clusters[index]; root.ParentError = float.MaxValue; root.ParentGroupId = -1;
            clusters[index] = root;
        }
    }

    private static int InitializeLeafClusters(ReadOnlySpan<Vector3> positions,
        List<BuilderMeshlet> clusters, List<uint> globalIndices)
    {
        for (int index = 0; index < clusters.Count; index++)
        {
            BuilderMeshlet cluster = clusters[index];
            ClusterLodBounds bounds = BoundsCompute(positions, CollectionsMarshal.AsSpan(globalIndices)
                .Slice(cluster.IndicesOffset, cluster.IndicesCount), 0);
            cluster.Center = cluster.LodCenter = cluster.SelfLodCenter = bounds.Center;
            cluster.Radius = cluster.LodRadius = cluster.SelfLodRadius = bounds.Radius;
            cluster.Error = 0; cluster.Level = 0; cluster.GroupId = index; cluster.ParentGroupId = -1;
            clusters[index] = cluster;
        }
        return clusters.Count;
    }

    private static void MergeGroupIndices(List<uint> globalIndices, List<BuilderMeshlet> clusters,
        ReadOnlySpan<int> group, List<uint> destination)
    {
        destination.Clear();
        foreach (int cluster in group)
            destination.AddRange(CollectionsMarshal.AsSpan(globalIndices).Slice(clusters[cluster].IndicesOffset, clusters[cluster].IndicesCount));
    }

    private static void Simplify(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices,
        ReadOnlySpan<byte> locks, int targetIndexCount, ReadOnlySpan<float> attributes,
        ReadOnlySpan<float> attributeWeights, out float error, List<uint> outputIndices)
    {
        outputIndices.Clear();
        if (targetIndexCount >= indices.Length)
        {
            outputIndices.AddRange(indices);
            error = 0;
            return;
        }

        uint[] reducedIndices = ArrayPool<uint>.Shared.Rent(indices.Length);
        try
        {
            const SimplificationOptions permissive = (SimplificationOptions)(1 << 5);
            int count = checked((int)Meshopt.SimplifyWithAttributes(reducedIndices, indices,
                MemoryMarshal.Cast<Vector3, float>(positions), (nuint)Unsafe.SizeOf<Vector3>(),
                attributes, (nuint)(attributeWeights.Length * sizeof(float)), attributeWeights,
                (nuint)attributeWeights.Length, locks, (nuint)targetIndexCount, float.MaxValue,
                SimplificationOptions.SimplifyLockBorder | SimplificationOptions.meshopt_SimplifySparse |
                SimplificationOptions.meshopt_SimplifyErrorAbsolute | permissive, out error));
            outputIndices.AddRange(reducedIndices.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(reducedIndices);
        }
    }

}

public static partial class ClusterBuilder
{
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

    private readonly record struct VertReuseBatchData(uint[] MaterialRanges, uint[] Words);

    private static void EmitSplitMeshlet(
        ReadOnlySpan<TempTri> tris,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices
    )
    {
        VertReuseBatchData vertReuseBatches = BuildVertReuseBatchInfo(tris);

        int startIndex = globalIndices.Count;

        var uniqueVerts = new HashSet<uint>();

        for (int i = 0; i < tris.Length; i++)
        {
            ref readonly var t = ref tris[i];
            globalIndices.Add(t.v0);
            globalIndices.Add(t.v1);
            globalIndices.Add(t.v2);
            uniqueVerts.Add(t.v0);
            uniqueVerts.Add(t.v1);
            uniqueVerts.Add(t.v2);
        }


        clusters.Add(
            new BuilderMeshlet
            {
                IndicesOffset = startIndex,
                IndicesCount = tris.Length * 3,
                VertexCount = uniqueVerts.Count,
                GroupId = -1,
                ParentGroupId = -1,
                MaterialRanges = vertReuseBatches.MaterialRanges,
                VertReuseBatchWords = vertReuseBatches.Words,
            }
        );
    }
}

public static partial class ClusterBuilder
{



    private static VertReuseBatchData BuildVertReuseBatchInfo(ReadOnlySpan<TempTri> triangles)
    {
        var ranges = new List<uint>();
        var batches = new List<List<byte>>();
        int rangeStart = 0;

        while (rangeStart < triangles.Length)
        {
            byte material = triangles[rangeStart].mat;
            int rangeEnd = rangeStart + 1;
            while (rangeEnd < triangles.Length && triangles[rangeEnd].mat == material)
                rangeEnd++;

            List<byte> batchTriangleCounts = BuildVertReuseBatches(
                triangles.Slice(rangeStart, rangeEnd - rangeStart));
            if (batchTriangleCounts.Count == 0 || batchTriangleCounts.Count >= 16)
                throw new InvalidDataException("A cluster material range has an invalid vertex-reuse batch count.");

            ranges.Add((uint)rangeStart | ((uint)(rangeEnd - rangeStart) << 8) | ((uint)material << 16));
            batches.Add(batchTriangleCounts);
            rangeStart = rangeEnd;
        }
        int countBits = Math.Max(ranges.Count, 3) * 4;
        int batchCount = batches.Sum(static range => range.Count);
        uint[] words = new uint[Math.Max(4, (countBits + batchCount * 5 + 31) / 32)];
        int triangleBitOffset = countBits;
        for (int rangeIndex = 0; rangeIndex < batches.Count; rangeIndex++)
        {
            WriteVertReuseBits(words, rangeIndex * 4, (uint)batches[rangeIndex].Count, 4);
            foreach (byte triangleCount in batches[rangeIndex])
            {
                WriteVertReuseBits(words, triangleBitOffset, (uint)(triangleCount - 1), 5);
                triangleBitOffset += 5;
            }
        }
        return new VertReuseBatchData(ranges.ToArray(), words);
    }

    private static List<byte> BuildVertReuseBatches(ReadOnlySpan<TempTri> triangles)
    {
        var batchTriangleCounts = new List<byte>();
        var usedVertices = new HashSet<uint>();
        int triangleCount = 0;

        for (int index = 0; index < triangles.Length; index++)
        {
            ref readonly TempTri triangle = ref triangles[index];
            int newVertexCount = VertReuseVertexCountAfterAdd(usedVertices, triangle);
            if (newVertexCount > 32)
            {
                if (triangleCount == 0)
                    throw new InvalidDataException("A triangle exceeds the 32-vertex reuse-batch limit.");
                CloseVertReuseBatch(batchTriangleCounts, usedVertices, ref triangleCount);
                index--;
                continue;
            }

            AddVertReuseTriangleVertices(usedVertices, triangle);
            triangleCount++;
            if (triangleCount == 32)
                CloseVertReuseBatch(batchTriangleCounts, usedVertices, ref triangleCount);
        }

        CloseVertReuseBatch(batchTriangleCounts, usedVertices, ref triangleCount);
        return batchTriangleCounts;
    }

    private static int VertReuseVertexCountAfterAdd(HashSet<uint> usedVertices, TempTri triangle)
    {
        int count = usedVertices.Count;
        if (!usedVertices.Contains(triangle.v0)) count++;
        if (!usedVertices.Contains(triangle.v1)) count++;
        if (!usedVertices.Contains(triangle.v2)) count++;
        return count;
    }

    private static void AddVertReuseTriangleVertices(HashSet<uint> usedVertices, TempTri triangle)
    {
        usedVertices.Add(triangle.v0);
        usedVertices.Add(triangle.v1);
        usedVertices.Add(triangle.v2);
    }

    private static void CloseVertReuseBatch(
        List<byte> batchTriangleCounts,
        HashSet<uint> usedVertices,
        ref int triangleCount)
    {
        if (triangleCount == 0)
            return;

        batchTriangleCounts.Add(checked((byte)triangleCount));
        triangleCount = 0;
        usedVertices.Clear();
    }

    private static void WriteVertReuseBits(Span<uint> words, int bitOffset, uint value, int bitCount)
    {
        if (bitOffset < 0 || bitCount <= 0 || bitCount > 31 || bitOffset + bitCount > words.Length * 32)
            throw new InvalidDataException("Vertex-reuse batch metadata exceeds its inline encoding.");

        uint mask = (1u << bitCount) - 1u;
        if ((value & ~mask) != 0)
            throw new InvalidDataException("Vertex-reuse batch metadata value exceeds its bit field.");

        int wordIndex = bitOffset >> 5;
        int wordBit = bitOffset & 31;
        words[wordIndex] |= value << wordBit;
        int lowBits = Math.Min(bitCount, 32 - wordBit);
        if (lowBits < bitCount)
            words[wordIndex + 1] |= value >> lowBits;
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

        if (indices.Length % 3 != 0)
            throw new InvalidDataException("Cluster triangle indices are not divisible by three.");

        int capacity = checked((int)Meshopt.BuildMeshletsBound((nuint)indices.Length,
            (nuint)config.MaxVertices, (nuint)config.MaxTriangles));
        MeshOptimizer.Meshlet[] meshlets = ArrayPool<MeshOptimizer.Meshlet>.Shared.Rent(capacity);
        uint[] vertices = ArrayPool<uint>.Shared.Rent(checked(capacity * config.MaxVertices));
        byte[] triangleIndices = ArrayPool<byte>.Shared.Rent(checked(capacity * config.MaxTriangles * 3));
        try
        {
            int count = checked((int)Meshopt.BuildMeshletsSpatial(meshlets, vertices, triangleIndices,
                indices, MemoryMarshal.Cast<Vector3, float>(positions), (nuint)Unsafe.SizeOf<Vector3>(),
                (nuint)config.MaxVertices, (nuint)(config.MaxTriangles / 3), (nuint)config.MaxTriangles, .5f));
            for (int cluster = 0; cluster < count; cluster++)
            {
                MeshOptimizer.Meshlet meshlet = meshlets[cluster];
                Span<uint> clusterVertices = vertices.AsSpan((int)meshlet.vertex_offset, (int)meshlet.vertex_count);
                Span<byte> clusterTriangles = triangleIndices.AsSpan((int)meshlet.triangle_offset, (int)meshlet.triangle_count * 3);
                Meshopt.OptimizeMeshlet(clusterVertices, clusterTriangles, meshlet.triangle_count, meshlet.vertex_count);
                var triangles = new TempTri[meshlet.triangle_count];
                for (int triangle = 0; triangle < triangles.Length; triangle++)
                {
                    uint first = clusterVertices[clusterTriangles[triangle * 3]];
                    uint second = clusterVertices[clusterTriangles[triangle * 3 + 1]];
                    uint third = clusterVertices[clusterTriangles[triangle * 3 + 2]];
                    triangles[triangle] = new TempTri { v0 = first, v1 = second, v2 = third,
                        mat = checked((byte)materialIndicesArray[first]) };
                }
                SortTrianglesByMaterial(triangles);
                EmitMaterialChunks(triangles, clusters, globalIndices);
            }
        }
        finally
        {
            ArrayPool<MeshOptimizer.Meshlet>.Shared.Return(meshlets);
            ArrayPool<uint>.Shared.Return(vertices);
            ArrayPool<byte>.Shared.Return(triangleIndices);
        }
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
        foreach (int material in Enumerable.Range(0, buckets.Length)
            .Where(material => buckets[material] is not null)
            .OrderByDescending(material => buckets[material].Count).ThenBy(static material => material))
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
            if (TryStartMaterialChunk(
                    triangles,
                    index,
                    uniqueMaterials,
                    ref chunkStart,
                    clusters,
                    globalIndices))
            {
                continue;
            }
        }

        EmitRemainingMaterialChunk(
            triangles,
            chunkStart,
            clusters,
            globalIndices);
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

        if (uniqueMaterials.Count == 64)
        {
            EmitSplitMeshlet(
                new ReadOnlySpan<TempTri>(triangles, chunkStart, index - chunkStart),
                clusters,
                globalIndices);
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
            EmitSplitMeshlet(
                new ReadOnlySpan<TempTri>(triangles, chunkStart, triangles.Length - chunkStart),
                clusters,
                globalIndices);
        }
    }

    private static ClusterLodBounds BoundsCompute(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, float error)
    {
        var used = new HashSet<uint>();
        var points = new List<Vector3>();
        foreach (uint index in indices) if (used.Add(index)) points.Add(positions[(int)index]);
        Vector4 sphere = ClusterSphere.FromPoints(CollectionsMarshal.AsSpan(points));
        return new ClusterLodBounds { Center = new(sphere.X, sphere.Y, sphere.Z), Radius = sphere.W, Error = error };
    }

    private static ClusterLodBounds BoundsMerge(List<BuilderMeshlet> clusters, ReadOnlySpan<int> group)
    {
        var spheres = new Vector4[group.Length];
        float maximumError = 0;
        for (int index = 0; index < group.Length; index++)
        {
            BuilderMeshlet cluster = clusters[group[index]];
            spheres[index] = new(cluster.SelfLodCenter, cluster.SelfLodRadius);
            maximumError = Math.Max(maximumError, cluster.Error);
        }
        Vector4 sphere = ClusterSphere.FromSpheres(spheres);
        return new ClusterLodBounds { Center = new(sphere.X, sphere.Y, sphere.Z), Radius = sphere.W, Error = maximumError };
    }
}
