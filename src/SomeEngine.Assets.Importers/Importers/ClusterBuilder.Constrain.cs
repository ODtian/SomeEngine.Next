using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Assets.Importers;

public static partial class ClusterBuilder
{
    private static readonly int[] VertexCacheWeights =
    [577, 616, 641, 512, 614, 635, 478, 651, 65, 213, 719, 490, 213, 726, 863, 745,
        172, 939, 805, 885, 958, 1208, 1319, 1318, 1475, 1779, 2342, 159, 2307, 1998, 1211, 932];

    private static void ConstrainMeshlets(ClusterMeshData mesh, List<BuilderMeshlet> clusters, List<uint> indices)
    {
        BuilderMeshlet[] source = clusters.ToArray();
        clusters.Clear();
        foreach (BuilderMeshlet cluster in source)
        {
            var triangles = new TempTri[cluster.IndicesCount / 3];
            float[] materials = ExtractMaterialIndices(mesh);
            for (int index = 0; index < triangles.Length; index++)
            {
                int first = cluster.IndicesOffset + index * 3;
                triangles[index] = new TempTri { v0 = indices[first], v1 = indices[first + 1], v2 = indices[first + 2],
                    mat = (byte)materials[indices[first]] };
            }
            Constrain(cluster, triangles);
        }

        void Constrain(BuilderMeshlet cluster, TempTri[] triangles)
        {
            SortTrianglesByMaterial(triangles);
            (TempTri[] ordered, uint[] vertices) = OrderClusterVertices(triangles);
            if (vertices.Length > MaxVerticesPerMeshlet)
            {
                // NaniteEncodeConstrain::BuildClusterFromClusterTriangleRange, repeated for the 64-vertex capacity.
                for (int index = 0; index < ordered.Length; index++)
                {
                    ordered[index].v0 = vertices[ordered[index].v0]; ordered[index].v1 = vertices[ordered[index].v1];
                    ordered[index].v2 = vertices[ordered[index].v2];
                }
                int half = ordered.Length / 2;
                Constrain(cluster, ordered[..half]);
                Constrain(cluster, ordered[half..]);
                return;
            }
            var originalVertices = new HashSet<uint>();
            for (int index = 0; index < vertices.Length; index++)
                if (!originalVertices.Add(vertices[index])) vertices[index] = mesh.CopyVertex(vertices[index]);
            int first = indices.Count;
            for (int index = 0; index < ordered.Length; index++)
            {
                ordered[index].v0 = vertices[ordered[index].v0];
                ordered[index].v1 = vertices[ordered[index].v1];
                ordered[index].v2 = vertices[ordered[index].v2];
                indices.Add(ordered[index].v0); indices.Add(ordered[index].v1); indices.Add(ordered[index].v2);
            }
            VertReuseBatchData batches = BuildVertReuseBatchInfo(ordered);
            cluster.IndicesOffset = first; cluster.IndicesCount = ordered.Length * 3;
            cluster.VertexCount = vertices.Length;
            cluster.MaterialRanges = batches.MaterialRanges; cluster.VertReuseBatchWords = batches.Words;
            clusters.Add(cluster);
        }
    }

    private static (TempTri[] Triangles, uint[] Vertices) OrderClusterVertices(TempTri[] triangles)
    {
        var adjacent = new Dictionary<uint, List<int>>();
        for (int index = 0; index < triangles.Length; index++)
            foreach (uint vertex in new[] { triangles[index].v0, triangles[index].v1, triangles[index].v2 })
            {
                if (!adjacent.TryGetValue(vertex, out List<int>? list)) adjacent.Add(vertex, list = []);
                list.Add(index);
            }
        bool[] touched = new bool[triangles.Length], used = new bool[triangles.Length];
        var oldToNew = new Dictionary<uint, uint>();
        var vertices = new List<uint>();
        var output = new List<TempTri>(triangles.Length);
        for (int first = 0; first < triangles.Length;)
        {
            int end = first + 1;
            while (end < triangles.Length && triangles[end].mat == triangles[first].mat) end++;
            for (int count = first; count < end; count++)
            {
                int next = -1, bestScore = 0;
                for (int triangle = first; triangle < end; triangle++)
                {
                    if (used[triangle] || !touched[triangle]) continue;
                    TempTri data = triangles[triangle];
                    int score = Score(data.v0) + Score(data.v1) + Score(data.v2);
                    if (score > bestScore) { next = triangle; bestScore = score; }
                }
                if (next < 0)
                    for (int triangle = first; triangle < end; triangle++)
                        if (!used[triangle]) { next = triangle; break; }
                TempTri selected = triangles[next];
                uint[] old = [selected.v0, selected.v1, selected.v2];
                uint[] mapped = old.Select(vertex => oldToNew.GetValueOrDefault(vertex, uint.MaxValue)).ToArray();
                int testCount = vertices.Count + mapped.Count(static value => value == uint.MaxValue);
                bool repeat;
                do
                {
                    repeat = false;
                    for (int corner = 0; corner < 3; corner++)
                        if (mapped[corner] != uint.MaxValue && testCount - mapped[corner] >= 32)
                        {
                            mapped[corner] = uint.MaxValue; testCount++; repeat = true; break;
                        }
                } while (repeat);
                for (int corner = 0; corner < 3; corner++)
                {
                    if (mapped[corner] == uint.MaxValue)
                    {
                        mapped[corner] = (uint)vertices.Count; vertices.Add(old[corner]);
                    }
                    oldToNew[old[corner]] = mapped[corner];
                    foreach (int triangle in adjacent[old[corner]]) touched[triangle] = true;
                }
                output.Add(new TempTri { v0 = mapped[0], v1 = mapped[1], v2 = mapped[2], mat = selected.mat });
                used[next] = true;
            }
            first = end;
        }
        return (output.ToArray(), vertices.ToArray());

        int Score(uint vertex)
        {
            if (!oldToNew.TryGetValue(vertex, out uint value)) return 0;
            uint position = (uint)vertices.Count - 1 - value;
            return position < 32 ? VertexCacheWeights[position] : 0;
        }
    }
}
