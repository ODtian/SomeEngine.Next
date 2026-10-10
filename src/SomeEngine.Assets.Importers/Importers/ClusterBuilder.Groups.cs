using System.Numerics;
using System.Runtime.InteropServices;
using MeshOptimizer;

namespace SomeEngine.Assets.Importers;

public static partial class ClusterBuilder
{
    private static List<int[]> PartitionClusterGroups(ReadOnlySpan<Vector3> positions, List<BuilderMeshlet> clusters,
        List<uint> indices, List<int> current)
    {
        if (current.Count <= 16) return [Enumerable.Range(0, current.Count).ToArray()];
        var remap = new uint[positions.Length];
        ReadOnlySpan<float> positionValues = MemoryMarshal.Cast<Vector3, float>(positions);
        Meshopt.GeneratePositionRemap(remap, positionValues, 12);
        var indexCounts = new uint[current.Count];
        var clusterIndices = new uint[current.Sum(index => clusters[index].IndicesCount)];
        int offset = 0;
        for (int index = 0; index < current.Count; index++)
        {
            BuilderMeshlet cluster = clusters[current[index]];
            ReadOnlySpan<uint> triangles = CollectionsMarshal.AsSpan(indices).Slice(cluster.IndicesOffset, cluster.IndicesCount);
            indexCounts[index] = (uint)triangles.Length;
            foreach (uint vertex in triangles)
                clusterIndices[offset++] = remap[vertex];
        }
        var groupIndices = new uint[current.Count];
        int groupCount = checked((int)Meshopt.PartitionClusters(groupIndices, clusterIndices,
            indexCounts, positionValues, 12, 16));
        var groups = Enumerable.Range(0, groupCount).Select(_ => new List<int>()).ToArray();
        for (int index = 0; index < current.Count; index++)
            groups[groupIndices[index]].Add(index);
        return groups.Select(static group => group.ToArray()).ToList();
    }

    private static HashSet<Vector3> FindMaterialBoundaries(ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<float> materials)
    {
        var firstMaterials = new Dictionary<Vector3, float>();
        var boundaries = new HashSet<Vector3>();
        for (int vertex = 0; vertex < positions.Length; vertex++)
        {
            Vector3 position = positions[vertex];
            if (!firstMaterials.TryAdd(position, materials[vertex]) && firstMaterials[position] != materials[vertex])
                boundaries.Add(position);
        }
        return boundaries;
    }

    private static byte[] LockGroupBoundaries(Vector3[] positions, int vertexCount, List<BuilderMeshlet> clusters,
        List<uint> indices, List<int> current, List<int[]> groups, HashSet<Vector3> materialBoundaries)
    {
        int[] clusterGroups = new int[current.Count];
        for (int group = 0; group < groups.Count; group++)
            foreach (int cluster in groups[group]) clusterGroups[cluster] = group;
        var edges = new Dictionary<(Vector3 First, Vector3 Second), List<int>>();
        for (int cluster = 0; cluster < current.Count; cluster++)
        {
            BuilderMeshlet data = clusters[current[cluster]];
            ReadOnlySpan<uint> triangles = CollectionsMarshal.AsSpan(indices).Slice(data.IndicesOffset, data.IndicesCount);
            for (int corner = 0; corner < triangles.Length; corner++)
            {
                Vector3 first = positions[triangles[corner]];
                Vector3 second = positions[triangles[corner - corner % 3 + (corner + 1) % 3]];
                if (!edges.TryGetValue((first, second), out List<int>? groupsForEdge))
                    edges.Add((first, second), groupsForEdge = []);
                groupsForEdge.Add(clusterGroups[cluster]);
            }
        }
        var locked = new HashSet<Vector3>();
        foreach (var pair in edges)
            if (edges.TryGetValue((pair.Key.Second, pair.Key.First), out List<int>? opposite) &&
                pair.Value.Any(group => opposite.Any(other => other != group)))
            {
                locked.Add(pair.Key.First); locked.Add(pair.Key.Second);
            }
        var result = new byte[vertexCount];
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            // Meshoptimizer's Lock bit fixes group borders; Protect preserves a material
            // discontinuity while permissive simplification can still simplify along it.
            byte flags = locked.Contains(positions[vertex]) ? (byte)1 : (byte)0;
            if (materialBoundaries.Contains(positions[vertex])) flags |= 2;
            result[vertex] = flags;
        }
        return result;
    }
}
