using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MeshOptimizer;

namespace SomeEngine.Geometry.Cluster;

public static partial class ClusterMeshBuilder
{
    private static void SimplifyLodGroup(ClusterLodConfig config, ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices, ReadOnlySpan<byte> locks, ReadOnlySpan<uint> materialIndices, int targetIndexCount,
        ReadOnlySpan<float> attributes, ReadOnlySpan<float> attributeWeights,
        out float error, List<uint> outputIndices)
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
            SimplificationOptions options = SimplificationOptions.SimplifyLockBorder |
                SimplificationOptions.meshopt_SimplifySparse | SimplificationOptions.meshopt_SimplifyErrorAbsolute;
            if (config.SimplifyPermissive) options |= (SimplificationOptions)(1 << 5);
            if (config.SimplifyRegularize) options |= (SimplificationOptions)(1 << 4);
            int count = checked((int)Meshopt.SimplifyWithAttributes(reducedIndices, indices,
                MemoryMarshal.Cast<Vector3, float>(positions), (nuint)Unsafe.SizeOf<Vector3>(),
                attributes, (nuint)(attributeWeights.Length * sizeof(float)), attributeWeights,
                (nuint)attributeWeights.Length, locks, (nuint)targetIndexCount, float.MaxValue, options, out error));
            if (count > targetIndexCount && config.SimplifyFallbackSloppy)
                count = SimplifySloppyLodGroup(positions, indices, locks, materialIndices, targetIndexCount,
                    reducedIndices, count, config.SimplifyErrorFactorSloppy, ref error);
            if (config.SimplifyErrorEdgeLimit > 0)
                error = Math.Min(error, ComputeEdgeErrorLimit(positions, indices, config.SimplifyErrorEdgeLimit));
            outputIndices.AddRange(reducedIndices.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(reducedIndices);
        }
    }

    private static int SimplifySloppyLodGroup(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices,
        ReadOnlySpan<byte> locks, ReadOnlySpan<uint> materialIndices, int targetIndexCount, uint[] reducedIndices, int count,
        float errorFactor, ref float error)
    {
        var materialTriangles = new Dictionary<uint, List<uint>>();
        for (int triangle = 0; triangle < indices.Length; triangle += 3)
        {
            uint material = materialIndices[(int)indices[triangle]];
            if (!materialTriangles.TryGetValue(material, out List<uint>? triangles))
                materialTriangles.Add(material, triangles = []);
            triangles.AddRange(indices.Slice(triangle, 3));
        }
        uint[] fallbackIndices = ArrayPool<uint>.Shared.Rent(indices.Length);
        try
        {
            int fallbackCount = 0;
            float fallbackError = 0;
            foreach (List<uint> triangles in materialTriangles.Values)
            {
                int target = Math.Max(3, (int)((long)targetIndexCount * triangles.Count / indices.Length / 3 * 3));
                fallbackCount += SimplifySloppyMaterialGroup(positions, CollectionsMarshal.AsSpan(triangles),
                    locks, target, fallbackIndices.AsSpan(fallbackCount), out float materialError);
                fallbackError = Math.Max(fallbackError, materialError);
            }
            if (fallbackCount < count)
            {
                fallbackIndices.AsSpan(0, fallbackCount).CopyTo(reducedIndices);
                error = fallbackError * errorFactor;
                return fallbackCount;
            }
            return count;
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(fallbackIndices);
        }
    }

    private static unsafe int SimplifySloppyMaterialGroup(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices,
        ReadOnlySpan<byte> locks, int targetIndexCount, Span<uint> outputIndices, out float error)
    {
        // Sloppy simplification uses a dense subset and relative error. Keep its vertices
        // mapped to the source identities and convert the result to the QEM error unit.
        var vertexRemap = new Dictionary<uint, uint>();
        var sourceVertices = new List<uint>();
        var subsetPositions = new List<Vector3>();
        var subsetIndices = new uint[indices.Length];
        var positionLocks = new Dictionary<Vector3, byte>();
        for (int index = 0; index < indices.Length; index++)
        {
            uint source = indices[index];
            if (!vertexRemap.TryGetValue(source, out uint vertex))
            {
                vertex = (uint)sourceVertices.Count;
                vertexRemap.Add(source, vertex);
                sourceVertices.Add(source);
                Vector3 position = positions[(int)source];
                subsetPositions.Add(position);
                // Protect flags and distinct identities at one position preserve seams
                // through the position-only fallback by fixing those vertices.
                byte locked = locks[(int)source] != 0 ? (byte)1 : (byte)0;
                if (!positionLocks.TryAdd(position, locked)) positionLocks[position] = 1;
            }
            subsetIndices[index] = vertex;
        }

        var edges = new HashSet<(uint First, uint Second)>();
        for (int triangle = 0; triangle < indices.Length; triangle += 3)
            for (int corner = 0; corner < 3; corner++)
                edges.Add((indices[triangle + corner], indices[triangle + (corner + 1) % 3]));
        foreach (var edge in edges)
            if (!edges.Contains((edge.Second, edge.First)))
            {
                positionLocks[positions[(int)edge.First]] = 1;
                positionLocks[positions[(int)edge.Second]] = 1;
            }
        var subsetLocks = subsetPositions.Select(position => positionLocks[position]).ToArray();
        ReadOnlySpan<float> subsetValues = MemoryMarshal.Cast<Vector3, float>(CollectionsMarshal.AsSpan(subsetPositions));
        int count;
        fixed (byte* vertexLocks = subsetLocks)
            count = checked((int)Meshopt.SimplifySloppy(outputIndices, subsetIndices, subsetValues,
                12, vertexLocks, (nuint)targetIndexCount, float.MaxValue, out error));
        error *= Meshopt.SimplifyScale(subsetValues, 12);
        for (int index = 0; index < count; index++)
            outputIndices[index] = sourceVertices[(int)outputIndices[index]];
        if (count == 0)
        {
            // Each source material retains geometry in the coarser representation.
            indices[..3].CopyTo(outputIndices);
            count = 3;
        }
        return count;
    }

    private static float ComputeEdgeErrorLimit(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, float factor)
    {
        float maximum = 0;
        for (int triangle = 0; triangle < indices.Length; triangle += 3)
        {
            Vector3 first = positions[(int)indices[triangle]];
            Vector3 second = positions[(int)indices[triangle + 1]];
            Vector3 third = positions[(int)indices[triangle + 2]];
            float a = Vector3.DistanceSquared(first, second);
            float b = Vector3.DistanceSquared(first, third);
            float c = Vector3.DistanceSquared(second, third);
            maximum = Math.Max(maximum, Math.Max(Math.Min(a, Math.Min(b, c)), Math.Max(a, Math.Max(b, c)) / 4));
        }
        return MathF.Sqrt(maximum) * factor;
    }
}
