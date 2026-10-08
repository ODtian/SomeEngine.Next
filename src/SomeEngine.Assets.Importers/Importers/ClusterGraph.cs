using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Assets.Importers;

internal sealed class ClusterGraph
{
    internal readonly Dictionary<int, int>[] Edges;
    internal readonly Vector3[] Centers;
    internal readonly int[] Groups;
    private int[] _order;
    private Vector3 _minimum = new(float.MaxValue), _maximum = new(float.MinValue);
    internal void Include(Vector3 point) { _minimum = Vector3.Min(_minimum, point); _maximum = Vector3.Max(_maximum, point); }

    internal ClusterGraph(int count)
    {
        Edges = new Dictionary<int, int>[count];
        Centers = new Vector3[count];
        Groups = new int[count];
        _order = Enumerable.Range(0, count).ToArray();
        for (int index = 0; index < count; index++) Edges[index] = [];
    }

    internal void Connect(int first, int second, int weight)
    {
        if (first == second) return;
        Edges[first][second] = Edges[first].GetValueOrDefault(second) + weight;
        Edges[second][first] = Edges[second].GetValueOrDefault(first) + weight;
    }

    internal static void AddTriangleEdges(Dictionary<(Vector3 First, Vector3 Second), List<int>> edges,
        ReadOnlySpan<uint> indices, ReadOnlySpan<Vector3> positions, int element, Action<int>? connectOpposite = null)
    {
        for (int corner = 0; corner < indices.Length; corner++)
        {
            Vector3 first = positions[checked((int)indices[corner])];
            Vector3 second = positions[checked((int)indices[corner - corner % 3 + (corner + 1) % 3])];
            if (connectOpposite is not null && edges.TryGetValue((second, first), out List<int>? opposite))
                foreach (int neighbor in opposite) connectOpposite(neighbor);
            if (!edges.TryGetValue((first, second), out List<int>? elements))
                edges.Add((first, second), elements = []);
            elements.Add(element);
        }
    }

    internal static ClusterGraph BuildTriangleGraph(ReadOnlySpan<uint> indices, ReadOnlySpan<Vector3> positions,
        float[] materialIndices)
    {
        var graph = new ClusterGraph(indices.Length / 3);
        var edgeTriangles = new Dictionary<(Vector3 First, Vector3 Second), List<int>>();
        for (int triangle = 0; triangle < graph.Edges.Length; triangle++)
        {
            Vector3 first = positions[checked((int)indices[triangle * 3])];
            Vector3 second = positions[checked((int)indices[triangle * 3 + 1])];
            Vector3 third = positions[checked((int)indices[triangle * 3 + 2])];
            graph.Include(first); graph.Include(second); graph.Include(third);
            graph.Centers[triangle] = (first + second + third) / 3;
            graph.Groups[triangle] = (int)materialIndices[indices[triangle * 3]];
            AddTriangleEdges(edgeTriangles, indices.Slice(triangle * 3, 3), positions, triangle,
                neighbor => graph.Connect(triangle, neighbor, 4 * 65));
        }
        graph.ConnectNearbyComponents();
        return graph;

    }

    internal void ConnectNearbyComponents()
    {
        int[] components = Enumerable.Repeat(-1, Edges.Length).ToArray();
        var representatives = new List<int>();
        var stack = new Stack<int>();
        for (int index = 0; index < Edges.Length; index++)
        {
            if (components[index] >= 0) continue;
            int component = representatives.Count;
            representatives.Add(index);
            stack.Push(index);
            components[index] = component;
            while (stack.TryPop(out int current))
                foreach (int neighbor in Edges[current].Keys)
                    if (components[neighbor] < 0)
                    {
                        components[neighbor] = component;
                        stack.Push(neighbor);
                    }
        }
        Vector3 size = _maximum - _minimum;
        float scale = 1023f / Math.Max(Math.Max(size.X, size.Y), Math.Max(size.Z, 1e-20f));
        int[] order = Enumerable.Range(0, Centers.Length).OrderBy(index => Morton(Centers[index])).ToArray();
        _order = order;
        int[] runStarts = new int[order.Length], runEnds = new int[order.Length];
        for (int first = 0; first < order.Length;)
        {
            int last = first + 1;
            while (last < order.Length && components[order[last]] == components[order[first]]) last++;
            for (int index = first; index < last; index++) { runStarts[index] = first; runEnds[index] = last; }
            first = last;
        }
        var links = new HashSet<(int First, int Second)>();
        for (int index = 0; index < order.Length; index++)
        {
            if (runEnds[index] - runStarts[index] >= 128) continue;
            int element = order[index];
            var closest = new List<(float Distance, int Index)>(5);
            foreach (int step in new[] { -1, 1 })
            {
                int other = index;
                for (int iteration = 0; iteration < 16; iteration++)
                {
                    other += step;
                    if (other < 0 || other >= order.Length) break;
                    int neighbor = order[other];
                    if (components[neighbor] == components[element] || Groups[neighbor] != Groups[element])
                    {
                        other = step > 0 ? runEnds[other] - 1 : runStarts[other];
                        continue;
                    }
                    closest.Add((Vector3.DistanceSquared(Centers[element], Centers[neighbor]), neighbor));
                    closest.Sort(static (first, second) => first.Distance.CompareTo(second.Distance));
                    if (closest.Count > 5) closest.RemoveAt(5);
                }
            }
            foreach ((float _, int neighbor) in closest)
                links.Add((Math.Min(element, neighbor), Math.Max(element, neighbor)));
        }
        foreach ((int first, int second) in links) Connect(first, second, 1);

        uint Morton(Vector3 center)
        {
            Vector3 local = (center - _minimum) * scale;
            return Spread((uint)local.X) | (Spread((uint)local.Y) << 1) | (Spread((uint)local.Z) << 2);
        }
        static uint Spread(uint value)
        {
            value &= 1023u;
            value = (value | (value << 16)) & 0x030000FFu;
            value = (value | (value << 8)) & 0x0300F00Fu;
            value = (value | (value << 4)) & 0x030C30C3u;
            value = (value | (value << 2)) & 0x09249249u;
            return value;
        }
    }

    internal List<int[]> Partition(int minimumSize, int maximumSize)
    {
        var ranges = new List<int[]>();
        Split(_order);
        return ranges;

        void Split(int[] elements)
        {
            if (elements.Length <= maximumSize)
            {
                if (elements.Length != 0) ranges.Add(elements);
                return;
            }
            int targetSize = (minimumSize + maximumSize) / 2;
            int targetParts = Math.Max(2, (elements.Length + targetSize / 2) / targetSize);
            int[] localIndices = Enumerable.Repeat(-1, Edges.Length).ToArray();
            for (int index = 0; index < elements.Length; index++) localIndices[elements[index]] = index;
            int[] offsets = new int[elements.Length + 1];
            var neighbors = new List<int>();
            var weights = new List<int>();
            for (int index = 0; index < elements.Length; index++)
            {
                offsets[index] = neighbors.Count;
                foreach ((int neighbor, int weight) in Edges[elements[index]])
                    if (localIndices[neighbor] >= 0)
                    {
                        neighbors.Add(localIndices[neighbor]);
                        weights.Add(weight);
                    }
            }
            offsets[^1] = neighbors.Count;
            int[] parts = ClusterGraphPartitioner.Bisect(offsets, neighbors.ToArray(), weights.ToArray(),
                (float)(targetParts / 2) / targetParts, targetParts >= 128 || maximumSize / minimumSize > 1);
            int[] reordered = (int[])elements.Clone();
            int front = 0, back = elements.Length - 1;
            while (front <= back)
            {
                while (front <= back && parts[front] == 0) front++;
                while (front <= back && parts[back] == 1) back--;
                if (front < back) { (reordered[front], reordered[back]) = (reordered[back], reordered[front]); front++; back--; }
            }
            int[] first = reordered[..front], second = reordered[front..];
            if (first.Length == 0 || second.Length == 0)
                throw new InvalidDataException("A graph partition has an empty side.");
            Split(first);
            Split(second);
        }
    }
}

internal static partial class ClusterGraphPartitioner
{
    [LibraryImport("metis", EntryPoint = "METIS_SetDefaultOptions")]
    private static partial int SetDefaultOptions(ref int options);

    [LibraryImport("metis", EntryPoint = "METIS_PartGraphRecursive")]
    private static unsafe partial int PartitionGraph(int* vertices, int* constraints, int* offsets,
        int* neighbors, int* vertexWeights, int* vertexSizes, int* edgeWeights, int* parts,
        float* weights, float* imbalance, int* options, int* cut, int* result);

    internal static unsafe int[] Bisect(int[] offsets, int[] neighbors, int[] edgeWeights, float firstWeight, bool loose)
    {
        int[] options = new int[40];
        SetDefaultOptions(ref options[0]);
        options[16] = loose ? 200 : 1;
        int vertices = offsets.Length - 1, constraints = 1, count = 2, cut = 0;
        int[] result = new int[vertices];
        float* weights = stackalloc float[2] { firstWeight, 1 - firstWeight };
        fixed (int* offsetData = offsets, neighborData = neighbors, edgeData = edgeWeights,
            optionData = options, resultData = result)
        {
            int status = PartitionGraph(&vertices, &constraints, offsetData, neighborData, null, null,
                edgeData, &count, weights, null, optionData, &cut, resultData);
            if (status != 1) throw new InvalidDataException($"Geometry graph partition failed with code {status}.");
        }
        return result;
    }
}
