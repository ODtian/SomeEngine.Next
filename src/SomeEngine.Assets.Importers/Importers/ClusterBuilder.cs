using SomeEngine.Geometry.Cluster;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

public static partial class ClusterBuilder
{
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

