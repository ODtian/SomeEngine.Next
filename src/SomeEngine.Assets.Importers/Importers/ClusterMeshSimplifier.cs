using System.Numerics;

namespace SomeEngine.Assets.Importers;

/// <summary>Position pairs and attribute wedges used by the Nanite triangle simplifier.</summary>
internal sealed class ClusterMeshSimplifier
{
    internal readonly record struct Result(Vector3[] Positions, float[][] Attributes, uint[] Indices,
        float Error);
    private sealed class MeshPosition(Vector3 value, bool locked)
    {
        internal Vector3 Value = value;
        internal readonly HashSet<int> Triangles = [];
        internal bool Locked = locked;
        internal int Version;
    }
    private sealed class MeshVertex(int position, float[] attributes)
    {
        internal int Position = position;
        internal float[] Attributes = attributes;
    }
    private sealed class MeshTriangle(int a, int b, int c, uint material)
    {
        internal readonly int[] Vertices = [a, b, c];
        internal uint Material = material;
        internal bool Removed;
    }
    private readonly record struct PositionPair(int First, int Second, int FirstVersion, int SecondVersion);
    private sealed record VertexMergeData(float Error, float Priority, Vector3 Position, int[] Triangles,
        int[] Wedges, float[][] Attributes);

    private readonly List<MeshPosition> _positions = [];
    private readonly List<MeshVertex> _vertices = [];
    private readonly List<MeshTriangle> _triangles = [];
    private readonly IReadOnlyList<RawAttribute> _attributes;
    private readonly int[] _attributeOffsets;
    private readonly float[] _weights;
    private readonly Dictionary<(int First, int Second), (int FirstVersion, int SecondVersion)> _pairVersions = [];
    private readonly PriorityQueue<PositionPair, float> _pairs = new();
    private readonly float _positionScale;
    private readonly Vector3 _origin;
    private int _remainingTriangles;
    private readonly Result _backup;
    private readonly float _backupArea, _inputArea;

    internal ClusterMeshSimplifier(Vector3[] positions, IReadOnlyList<RawAttribute> attributes,
        ReadOnlySpan<uint> indices, ReadOnlySpan<byte> locks)
    {
        _attributes = attributes;
        _attributeOffsets = new int[attributes.Count];
        int components = 0;
        for (int attribute = 0; attribute < attributes.Count; attribute++)
        {
            _attributeOffsets[attribute] = components;
            components += attributes[attribute].Dimension;
        }
        _weights = new float[components];
        _origin = positions[indices[0]];
        double surfaceArea = 0;
        for (int triangle = 0; triangle < indices.Length; triangle += 3)
            surfaceArea += Vector3.Cross(positions[indices[triangle + 1]] - positions[indices[triangle]],
                positions[indices[triangle + 2]] - positions[indices[triangle]]).Length() * .5;
        float triangleSize = Math.Max((float)Math.Sqrt(surfaceArea / (indices.Length / 3)), 2e-5f);
        int exponent = Math.Clamp(Math.ILogB(.25f) - Math.ILogB(triangleSize), -126, 127);
        _positionScale = MathF.ScaleB(1, exponent);
        var positionIndices = new Dictionary<Vector3, int>();
        var vertices = new Dictionary<uint, int>();
        foreach (uint index in indices)
        {
            if (vertices.ContainsKey(index)) continue;
            Vector3 value = (positions[index] - _origin) * _positionScale;
            if (!positionIndices.TryGetValue(value, out int position))
            {
                positionIndices.Add(value, position = _positions.Count);
                _positions.Add(new MeshPosition(value, locks[(int)index] != 0));
            }
            else _positions[position].Locked |= locks[(int)index] != 0;
            float[] values = new float[components];
            for (int attribute = 0; attribute < attributes.Count; attribute++)
            {
                RawAttribute data = attributes[attribute];
                data.Data.AsSpan((int)index * data.Dimension, data.Dimension).CopyTo(values.AsSpan(_attributeOffsets[attribute]));
            }
            vertices.Add(index, _vertices.Count);
            _vertices.Add(new MeshVertex(position, values));
        }
        RawAttribute? material = attributes.FirstOrDefault(static attribute => attribute.Name == "_MATERIAL_INDEX");
        for (int triangle = 0; triangle < indices.Length; triangle += 3)
        {
            uint materialIndex = material is null ? 0u : (uint)material.Data[indices[triangle]];
            var data = new MeshTriangle(vertices[indices[triangle]], vertices[indices[triangle + 1]],
                vertices[indices[triangle + 2]], materialIndex);
            _triangles.Add(data);
            foreach (int vertex in data.Vertices) _positions[_vertices[vertex].Position].Triangles.Add(triangle / 3);
        }
        int backupTriangle = 0;
        float backupArea = 0, inputArea = 0;
        for (int index = 0; index < _triangles.Count; index++)
        {
            MeshTriangle triangle = _triangles[index];
            Vector3 a = _positions[_vertices[triangle.Vertices[0]].Position].Value;
            Vector3 b = _positions[_vertices[triangle.Vertices[1]].Position].Value;
            Vector3 c = _positions[_vertices[triangle.Vertices[2]].Position].Value;
            float area = Vector3.Cross(b - a, c - a).Length() * .5f;
            inputArea += area;
            if (area > backupArea) { backupTriangle = index; backupArea = area; }
        }
        MeshTriangle backup = _triangles[backupTriangle];
        Vector3[] backupPositions = backup.Vertices.Select(vertex => _positions[_vertices[vertex].Position].Value / _positionScale + _origin).ToArray();
        float[][] backupAttributes = _attributes.Select((data, attribute) => backup.Vertices.SelectMany(vertex =>
            _vertices[vertex].Attributes.AsSpan(_attributeOffsets[attribute], data.Dimension).ToArray()).ToArray()).ToArray();
        _backup = new Result(backupPositions, backupAttributes, [0, 1, 2], 0);
        _backupArea = backupArea; _inputArea = inputArea;
        SetAttributeWeights();
        RemoveDegenerateTriangles();
        _remainingTriangles = _triangles.Count(static triangle => !triangle.Removed);
        foreach (MeshTriangle triangle in _triangles)
            if (!triangle.Removed)
                for (int edge = 0; edge < 3; edge++) AddPair(_vertices[triangle.Vertices[edge]].Position,
                    _vertices[triangle.Vertices[(edge + 1) % 3]].Position);
    }

    private void SetAttributeWeights()
    {
        int uv = 0;
        for (int attribute = 0; attribute < _attributes.Count; attribute++)
        {
            RawAttribute data = _attributes[attribute];
            int offset = _attributeOffsets[attribute];
            float weight = data.Name switch
            {
                "NORMAL" => 1,
                "TANGENT" => .0625f,
                "_MATERIAL_INDEX" => 0,
                _ when data.Name.StartsWith("COLOR", StringComparison.Ordinal) => .0625f,
                _ when data.Name.StartsWith("JOINTS", StringComparison.Ordinal) ||
                    data.Name.StartsWith("WEIGHTS", StringComparison.Ordinal) => 0,
                _ => 1,
            };
            if (data.Name.StartsWith("TEXCOORD", StringComparison.Ordinal) && data.Dimension >= 2)
            {
                double area = 0;
                foreach (MeshTriangle triangle in _triangles)
                {
                    float[] a = _vertices[triangle.Vertices[0]].Attributes;
                    float[] b = _vertices[triangle.Vertices[1]].Attributes;
                    float[] c = _vertices[triangle.Vertices[2]].Attributes;
                    float signed = .5f * ((b[offset] - a[offset]) * (c[offset + 1] - a[offset + 1]) -
                        (b[offset + 1] - a[offset + 1]) * (c[offset] - a[offset]));
                    area += Math.Abs(signed);
                    if (uv < 8 && signed >= 0) triangle.Material |= 1u << (24 + uv);
                }
                weight = 1 / (128 * Math.Max((float)Math.Sqrt(area / _triangles.Count), 1f / 1024));
                uv++;
            }
            Array.Fill(_weights, weight, offset, data.Dimension);
            if (data.Name == "TANGENT" && data.Dimension >= 4) _weights[offset + 3] = .5f;
        }
    }

    internal Result Simplify(int targetTriangles)
    {
        float maxError = 0;
        while (_remainingTriangles > targetTriangles && _pairs.TryDequeue(out PositionPair pair, out _))
        {
            MeshPosition first = _positions[pair.First], second = _positions[pair.Second];
            if (first.Version != pair.FirstVersion || second.Version != pair.SecondVersion ||
                first.Triangles.Count == 0 || second.Triangles.Count == 0) continue;
            VertexMergeData mergeData = EvaluateMerge(pair.First, pair.Second);
            maxError = Math.Max(maxError, mergeData.Error);
            ApplyMerge(pair.First, pair.Second, mergeData);
        }
        // MeshSimplify::Simplify removes remaining triangles when regular edge pairs are exhausted.
        for (int triangle = 0; _remainingTriangles > targetTriangles && triangle < _triangles.Count; triangle++)
            RemoveTriangle(triangle);
        return Compact(maxError);
    }

    private void AddPair(int first, int second)
    {
        if (first == second) return;
        if (first > second) (first, second) = (second, first);
        var version = (_positions[first].Version, _positions[second].Version);
        if (_pairVersions.TryGetValue((first, second), out var previous) && previous == version) return;
        _pairVersions[(first, second)] = version;
        VertexMergeData mergeData = EvaluateMerge(first, second);
        _pairs.Enqueue(new PositionPair(first, second, version.Item1, version.Item2), mergeData.Priority);
    }

    private VertexMergeData EvaluateMerge(int first, int second)
    {
        MeshPosition a = _positions[first], b = _positions[second];
        int[] triangles = a.Triangles.Concat(b.Triangles).Distinct().Order().ToArray();
        int[] parents = Enumerable.Range(0, triangles.Length).ToArray();
        var wedgesByVertex = new Dictionary<int, int>();
        int degree = 0;
        for (int index = 0; index < triangles.Length; index++)
            foreach (int vertex in _triangles[triangles[index]].Vertices)
                if (_vertices[vertex].Position == first || _vertices[vertex].Position == second)
                {
                    degree++;
                    if (wedgesByVertex.TryGetValue(vertex, out int other)) Union(index, other);
                    else wedgesByVertex.Add(vertex, index);
                }
        int[] wedgeIndices = parents.Select((_, index) => Find(index)).Distinct().ToArray();
        var quadrics = wedgeIndices.Select(_ => new AttributeQuadric(_weights.Length)).ToArray();
        int[] wedgeByTriangle = new int[triangles.Length];
        Vector3 minimum = new(float.MaxValue), maximum = new(float.MinValue);
        for (int index = 0; index < triangles.Length; index++)
        {
            int wedge = Array.IndexOf(wedgeIndices, Find(index));
            wedgeByTriangle[index] = wedge;
            MeshTriangle triangle = _triangles[triangles[index]];
            MeshVertex v0 = _vertices[triangle.Vertices[0]], v1 = _vertices[triangle.Vertices[1]], v2 = _vertices[triangle.Vertices[2]];
            quadrics[wedge].AddTriangle(_positions[v0.Position].Value - a.Value,
                _positions[v1.Position].Value - a.Value, _positions[v2.Position].Value - a.Value,
                v0.Attributes, v1.Attributes, v2.Attributes, _weights);
            foreach (int vertex in triangle.Vertices)
            {
                Vector3 value = _positions[_vertices[vertex].Position].Value;
                minimum = Vector3.Min(minimum, value); maximum = Vector3.Max(maximum, value);
            }
        }
        var edges = new AttributeQuadric(0);
        foreach (int triangleIndex in triangles)
        {
            MeshTriangle triangle = _triangles[triangleIndex];
            for (int edge = 0; edge < 3; edge++)
            {
                int v0 = triangle.Vertices[edge], v1 = triangle.Vertices[(edge + 1) % 3];
                int p0 = _vertices[v0].Position, p1 = _vertices[v1].Position;
                if (p0 != first && p0 != second && p1 != first && p1 != second) continue;
                bool sameVertices = false, samePositions = false;
                foreach (int otherIndex in _positions[p1].Triangles)
                {
                    MeshTriangle other = _triangles[otherIndex];
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int o0 = other.Vertices[corner], o1 = other.Vertices[(corner + 1) % 3];
                        if (o0 == v1 && o1 == v0 && other.Material == triangle.Material) sameVertices = true;
                        if (_vertices[o0].Position == p1 && _vertices[o1].Position == p0) samePositions = true;
                    }
                }
                if (!sameVertices) edges.AddEdge(_positions[p0].Value - a.Value,
                    _positions[p1].Value - a.Value, samePositions ? 1 : 2);
            }
        }
        var optimizer = new AttributeQuadric(_weights.Length);
        foreach (AttributeQuadric quadric in quadrics) optimizer.AddPositionQuadric(quadric);
        optimizer.AddPositionQuadric(edges);
        Vector3 position;
        float penalty = degree > 24 ? .5f * (degree - 24) : 0;
        if (a.Locked && b.Locked) penalty += 1e8f;
        if (a.Locked != b.Locked)
        {
            position = a.Locked ? a.Value : b.Value;
            if (!ValidPosition(position)) penalty += 100;
        }
        else
        {
            bool valid = optimizer.Optimize(true, out position);
            position += a.Value;
            valid = valid && ValidPosition(position);
            if (!valid)
            {
                valid = optimizer.Optimize(false, out position); position += a.Value;
                valid = valid && ValidPosition(position);
            }
            if (!valid)
            {
                valid = optimizer.OptimizeLinear(b.Value - a.Value, out position); position += a.Value;
                valid = valid && ValidPosition(position);
            }
            if (!valid)
            {
                position = (a.Value + b.Value) * .5f;
                if (!ValidPosition(position)) penalty += 100;
            }
        }
        float[][] values = quadrics.Select(_ => new float[_weights.Length]).ToArray();
        int farthest = Vector3.DistanceSquared(position, a.Value) > Vector3.DistanceSquared(position, b.Value) ? first : second;
        foreach (int endpoint in new[] { farthest, farthest == first ? second : first })
            for (int index = 0; index < triangles.Length; index++)
                foreach (int vertex in _triangles[triangles[index]].Vertices)
                    if (_vertices[vertex].Position == endpoint)
                        _vertices[vertex].Attributes.CopyTo(values[wedgeByTriangle[index]], 0);
        double error = edges.Evaluate(position - a.Value, [], [], false), area = 0;
        for (int wedge = 0; wedge < quadrics.Length; wedge++)
        {
            AttributeQuadric quadric = quadrics[wedge];
            double wedgeError = quadric.Evaluate(position - a.Value, values[wedge], _weights, a.Locked == b.Locked);
            if (a.Locked == b.Locked) CorrectAttributes(values[wedge]);
            error += Math.Min(wedgeError, quadric.Area);
            area += quadric.Area;
        }
        error = Math.Min(error, area);
        if (triangles.Length == 1 || triangles.Length == 2 && degree == 4) error = area;
        return new VertexMergeData((float)error, (float)error + penalty, position, triangles, wedgeByTriangle, values);

        int Find(int index)
        {
            while (parents[index] != index) { parents[index] = parents[parents[index]]; index = parents[index]; }
            return index;
        }
        void Union(int one, int two) { one = Find(one); two = Find(two); if (one != two) parents[Math.Max(one, two)] = Math.Min(one, two); }
        bool ValidPosition(Vector3 value)
        {
            Vector3 distance = Vector3.Max(Vector3.Max(minimum - value, value - maximum), Vector3.Zero);
            if (distance.LengthSquared() > (maximum - minimum).LengthSquared() * 4) return false;
            foreach (int triangleIndex in triangles)
            {
                MeshTriangle triangle = _triangles[triangleIndex];
                int moved = -1;
                for (int corner = 0; corner < 3; corner++)
                {
                    int index = _vertices[triangle.Vertices[corner]].Position;
                    if (index != first && index != second) continue;
                    if (moved >= 0) { moved = -2; break; }
                    moved = corner;
                }
                if (moved < 0) continue;
                Vector3 p0 = _positions[_vertices[triangle.Vertices[moved]].Position].Value;
                Vector3 p1 = _positions[_vertices[triangle.Vertices[(moved + 1) % 3]].Position].Value;
                Vector3 p2 = _positions[_vertices[triangle.Vertices[(moved + 2) % 3]].Position].Value;
                if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), Vector3.Cross(p1 - value, p2 - value)) < 0) return false;
            }
            return true;
        }
    }

    private void CorrectAttributes(float[] values)
    {
        int normalIndex = _attributes.ToList().FindIndex(static attribute => attribute.Name == "NORMAL");
        Vector3 normal = Vector3.Zero;
        if (normalIndex >= 0)
        {
            int offset = _attributeOffsets[normalIndex];
            normal = new(values[offset], values[offset + 1], values[offset + 2]);
            if (normal.LengthSquared() > 1e-20f) normal = Vector3.Normalize(normal);
        }
        for (int attribute = 0; attribute < _attributes.Count; attribute++)
        {
            RawAttribute data = _attributes[attribute];
            int offset = _attributeOffsets[attribute];
            if ((data.Name == "NORMAL" || data.Name == "TANGENT") && data.Dimension >= 3)
            {
                Vector3 vector = new(values[offset], values[offset + 1], values[offset + 2]);
                if (data.Name == "TANGENT") vector -= Vector3.Dot(vector, normal) * normal;
                if (vector.LengthSquared() > 1e-20f) vector = Vector3.Normalize(vector);
                values[offset] = vector.X; values[offset + 1] = vector.Y; values[offset + 2] = vector.Z;
                if (data.Name == "TANGENT" && data.Dimension >= 4) values[offset + 3] = values[offset + 3] < 0 ? -1 : 1;
            }
            else if (data.Name.StartsWith("COLOR", StringComparison.Ordinal))
                for (int component = 0; component < data.Dimension; component++) values[offset + component] = Math.Clamp(values[offset + component], 0, 1);
        }
    }

    private void ApplyMerge(int first, int second, VertexMergeData mergeData)
    {
        var affectedPositions = new HashSet<int>();
        for (int index = 0; index < mergeData.Triangles.Length; index++)
            foreach (int vertex in _triangles[mergeData.Triangles[index]].Vertices)
            {
                MeshVertex data = _vertices[vertex];
                affectedPositions.Add(data.Position);
                if (data.Position != first && data.Position != second) continue;
                data.Position = first;
                data.Attributes = mergeData.Attributes[mergeData.Wedges[index]];
            }
        _positions[first].Value = mergeData.Position;
        _positions[first].Locked |= _positions[second].Locked;
        _positions[first].Triangles.UnionWith(_positions[second].Triangles);
        _positions[second].Triangles.Clear();
        foreach (int position in affectedPositions) _positions[position].Version++;
        foreach (int triangle in mergeData.Triangles) FixTriangle(triangle);
        var adjacent = new HashSet<int>();
        foreach (int position in affectedPositions) adjacent.UnionWith(_positions[position].Triangles);
        foreach (int triangle in adjacent)
            for (int edge = 0; edge < 3; edge++) AddPair(_vertices[_triangles[triangle].Vertices[edge]].Position,
                _vertices[_triangles[triangle].Vertices[(edge + 1) % 3]].Position);
    }

    private void RemoveDegenerateTriangles()
    {
        _remainingTriangles = _triangles.Count;
        for (int triangle = 0; triangle < _triangles.Count; triangle++) FixTriangle(triangle);
    }
    private void FixTriangle(int index)
    {
        MeshTriangle triangle = _triangles[index];
        if (triangle.Removed) return;
        int p0 = _vertices[triangle.Vertices[0]].Position, p1 = _vertices[triangle.Vertices[1]].Position, p2 = _vertices[triangle.Vertices[2]].Position;
        if (p0 == p1 || p1 == p2 || p2 == p0 || Vector3.Cross(_positions[p1].Value - _positions[p0].Value,
            _positions[p2].Value - _positions[p0].Value).LengthSquared() < 1e-24f) { RemoveTriangle(index); return; }
        // Equal attributes share one wedge before the next edge pair is evaluated.
        WeldTriangleVertices(triangle);
        foreach (int otherIndex in _positions[p0].Triangles.ToArray())
        {
            if (otherIndex >= index || _triangles[otherIndex].Removed) continue;
            MeshTriangle other = _triangles[otherIndex];
            for (int corner = 0; corner < 3; corner++)
            {
                bool duplicate = true;
                for (int vertex = 0; vertex < 3; vertex++)
                {
                    MeshVertex a = _vertices[triangle.Vertices[vertex]], b = _vertices[other.Vertices[(corner + vertex) % 3]];
                    duplicate &= a.Position == b.Position && a.Attributes.AsSpan().SequenceEqual(b.Attributes);
                }
                if (duplicate) { RemoveTriangle(index); return; }
            }
        }
    }
    private void WeldTriangleVertices(MeshTriangle triangle)
    {
        for (int corner = 0; corner < 3; corner++)
        {
            int vertex = triangle.Vertices[corner];
            MeshVertex data = _vertices[vertex];
            foreach (int adjacent in _positions[data.Position].Triangles)
            {
                MeshTriangle other = _triangles[adjacent];
                if (other.Removed) continue;
                foreach (int candidate in other.Vertices)
                    if (candidate < vertex && _vertices[candidate].Position == data.Position &&
                        data.Attributes.AsSpan().SequenceEqual(_vertices[candidate].Attributes))
                        vertex = candidate;
            }
            triangle.Vertices[corner] = vertex;
        }
    }
    private void RemoveTriangle(int index)
    {
        MeshTriangle triangle = _triangles[index];
        if (triangle.Removed) return;
        triangle.Removed = true; _remainingTriangles--;
        foreach (int vertex in triangle.Vertices) _positions[_vertices[vertex].Position].Triangles.Remove(index);
    }

    private Result Compact(float error)
    {
        var positions = new List<Vector3>();
        var values = _attributes.Select(_ => new List<float>()).ToArray();
        var indices = new List<uint>();
        var remap = new Dictionary<MeshVertex, uint>(new VertexComparer());
        foreach (MeshTriangle triangle in _triangles)
        {
            if (triangle.Removed) continue;
            foreach (int vertex in triangle.Vertices)
            {
                MeshVertex data = _vertices[vertex];
                if (!remap.TryGetValue(data, out uint output))
                {
                    remap.Add(data, output = (uint)positions.Count);
                    positions.Add(_positions[data.Position].Value / _positionScale + _origin);
                    for (int attribute = 0; attribute < _attributes.Count; attribute++)
                    {
                        int offset = _attributeOffsets[attribute];
                        for (int component = 0; component < _attributes[attribute].Dimension; component++) values[attribute].Add(data.Attributes[offset + component]);
                    }
                }
                indices.Add(output);
            }
        }
        if (indices.Count == 0)
            return _backup with { Error = MathF.Sqrt(Math.Max(error, _inputArea - _backupArea)) / _positionScale };
        return new Result(positions.ToArray(), values.Select(static value => value.ToArray()).ToArray(), indices.ToArray(),
            MathF.Sqrt(Math.Max(error, 0)) / _positionScale);
    }

    private sealed class VertexComparer : IEqualityComparer<MeshVertex>
    {
        public bool Equals(MeshVertex? first, MeshVertex? second) => ReferenceEquals(first, second) ||
            first is not null && second is not null && first.Position == second.Position && first.Attributes.AsSpan().SequenceEqual(second.Attributes);
        public int GetHashCode(MeshVertex vertex)
        {
            var hash = new HashCode(); hash.Add(vertex.Position);
            foreach (float value in vertex.Attributes) hash.Add(value);
            return hash.ToHashCode();
        }
    }

    // Area weighted position/appearance quadrics, volume constrained minimization, then linear/midpoint retry.
    private sealed class AttributeQuadric(int attributes)
    {
        internal readonly double[] Matrix = new double[9];
        internal readonly double[] Linear = new double[3];
        internal readonly double[] VolumeNormal = new double[3];
        internal readonly double[][] Gradients = Enumerable.Range(0, attributes).Select(_ => new double[3]).ToArray();
        internal readonly double[] Constants = new double[attributes];
        internal double Scalar, Area, VolumeDistance;

        internal void AddTriangle(Vector3 p0, Vector3 p1, Vector3 p2, float[] a0, float[] a1, float[] a2, float[] weights)
        {
            double[] first = [(double)p1.X - p0.X, (double)p1.Y - p0.Y, (double)p1.Z - p0.Z];
            double[] second = [(double)p2.X - p0.X, (double)p2.Y - p0.Y, (double)p2.Z - p0.Z];
            double[] normal = Cross(second, first), origin = [p0.X, p0.Y, p0.Z];
            double length = Math.Sqrt(Dot(normal, normal)), area = .5 * length;
            if (area < 1e-12) return;
            for (int axis = 0; axis < 3; axis++) VolumeNormal[axis] += normal[axis];
            VolumeDistance -= Dot(normal, origin);
            for (int axis = 0; axis < 3; axis++) normal[axis] /= length;
            double distance = -Dot(normal, origin);
            AddOuter(Matrix, normal, normal, area);
            for (int axis = 0; axis < 3; axis++) Linear[axis] += normal[axis] * distance * area;
            Scalar += distance * distance * area;
            double[] system = [.. first, .. second, .. normal];
            for (int attribute = 0; attribute < weights.Length; attribute++)
            {
                if (weights[attribute] == 0) continue;
                double zero = weights[attribute] * a0[attribute];
                double one = weights[attribute] * a1[attribute];
                double two = weights[attribute] * a2[attribute];
                zero = double.IsFinite(zero) ? zero : 0; one = double.IsFinite(one) ? one : 0; two = double.IsFinite(two) ? two : 0;
                Solve(system, [one - zero, two - zero, 0], out double[] gradient);
                double constant = zero - Dot(gradient, origin);
                AddOuter(Matrix, gradient, gradient, area);
                for (int axis = 0; axis < 3; axis++)
                {
                    Gradients[attribute][axis] += gradient[axis] * area;
                    Linear[axis] += gradient[axis] * constant * area;
                }
                Constants[attribute] += constant * area;
                Scalar += constant * constant * area;
            }
            Area += area;
        }
        internal void AddEdge(Vector3 first, Vector3 second, float weight)
        {
            double[] direction = [(double)second.X - first.X, (double)second.Y - first.Y, (double)second.Z - first.Z];
            double length = Math.Sqrt(Dot(direction, direction));
            if (length < 1e-8) return;
            for (int axis = 0; axis < 3; axis++) direction[axis] /= length;
            double[] matrix = new double[9], origin = [first.X, first.Y, first.Z];
            for (int axis = 0; axis < 3; axis++) matrix[axis * 3 + axis] = weight * length;
            AddOuter(matrix, direction, direction, -weight * length);
            for (int row = 0; row < 3; row++)
            {
                double product = 0;
                for (int column = 0; column < 3; column++) { Matrix[row * 3 + column] += matrix[row * 3 + column]; product += matrix[row * 3 + column] * origin[column]; }
                Linear[row] -= product; Scalar += origin[row] * product;
            }
        }
        private readonly double[] _gradientProducts = new double[9];
        private readonly double[] _gradientConstants = new double[3];
        internal void AddPositionQuadric(AttributeQuadric quadric)
        {
            for (int index = 0; index < 9; index++) Matrix[index] += quadric.Matrix[index];
            for (int axis = 0; axis < 3; axis++) { Linear[axis] += quadric.Linear[axis]; VolumeNormal[axis] += quadric.VolumeNormal[axis]; }
            Area += quadric.Area; VolumeDistance += quadric.VolumeDistance;
            for (int attribute = 0; attribute < quadric.Gradients.Length; attribute++)
            {
                double[] gradient = quadric.Gradients[attribute];
                AddOuter(_gradientProducts, gradient, gradient, 1);
                for (int axis = 0; axis < 3; axis++) _gradientConstants[axis] += gradient[axis] * quadric.Constants[attribute];
            }
        }
        internal bool Optimize(bool volume, out Vector3 position)
        {
            position = default;
            if (Area < 1e-12 || volume && Dot(VolumeNormal, VolumeNormal) <= 1e-12) return false;
            int size = volume ? 4 : 3;
            double[] matrix = new double[size * size], rhs = new double[size];
            for (int row = 0; row < 3; row++)
            {
                rhs[row] = _gradientConstants[row] / Area - Linear[row];
                for (int column = 0; column < 3; column++) matrix[row * size + column] = Matrix[row * 3 + column] - _gradientProducts[row * 3 + column] / Area;
                if (volume) { matrix[row * size + 3] = VolumeNormal[row]; matrix[3 * size + row] = VolumeNormal[row]; }
            }
            if (volume) rhs[3] = -VolumeDistance;
            bool success = Solve(matrix, rhs, out double[] value);
            position = new((float)value[0], (float)value[1], (float)value[2]);
            return success && float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
        }
        internal bool OptimizeLinear(Vector3 edge, out Vector3 position)
        {
            position = default;
            if (Area < 1e-12) return false;
            double[] direction = [edge.X, edge.Y, edge.Z], product = new double[3];
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++) product[row] +=
                    (Matrix[row * 3 + column] - _gradientProducts[row * 3 + column] / Area) * direction[column];
            // Solve the three position equations along the edge, including the volume equation.
            double squaredProduct = Dot(product, product);
            if (squaredProduct < 1e-16) return false;
            double[] rhs = new double[3];
            for (int axis = 0; axis < 3; axis++) rhs[axis] = _gradientConstants[axis] / Area - Linear[axis];
            double amount = Dot(product, rhs) / squaredProduct;
            double squaredNormal = Dot(VolumeNormal, VolumeNormal);
            if (squaredNormal > 1e-12)
            {
                double normalDirection = Dot(VolumeNormal, direction);
                double productNormal = Dot(product, VolumeNormal);
                double determinant = (squaredProduct + normalDirection * normalDirection) * squaredNormal - productNormal * productNormal;
                if (Math.Abs(determinant) > 1e-16)
                    amount = (squaredNormal * (Dot(product, rhs) - VolumeDistance * normalDirection) -
                        productNormal * Dot(VolumeNormal, rhs)) / determinant;
            }
            amount = Math.Clamp(amount, 0, 1);
            position = new((float)(edge.X * amount), (float)(edge.Y * amount), (float)(edge.Z * amount));
            return true;
        }
        internal double Evaluate(Vector3 position, float[] attributes, float[] weights, bool calculate)
        {
            double[] value = [position.X, position.Y, position.Z];
            double error = Scalar + 2 * Dot(value, Linear);
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++) error += value[row] * Matrix[row * 3 + column] * value[column];
            if (Area > 1e-8)
                for (int attribute = 0; attribute < weights.Length; attribute++)
                {
                    if (weights[attribute] == 0) continue;
                    double pgd = Dot(value, Gradients[attribute]) + Constants[attribute];
                    double weighted = calculate ? pgd / Area : weights[attribute] * attributes[attribute];
                    if (calculate) attributes[attribute] = (float)(weighted / weights[attribute]);
                    error += Area * weighted * weighted - 2 * weighted * pgd;
                }
            return double.IsFinite(error) ? Math.Max(error, 0) : 0;
        }
        private static void AddOuter(double[] matrix, double[] first, double[] second, double scale)
        {
            for (int row = 0; row < 3; row++) for (int column = 0; column < 3; column++) matrix[row * 3 + column] += first[row] * second[column] * scale;
        }
        private static double[] Cross(double[] first, double[] second) => [first[1] * second[2] - first[2] * second[1], first[2] * second[0] - first[0] * second[2], first[0] * second[1] - first[1] * second[0]];
        private static double Dot(double[] first, double[] second) => first[0] * second[0] + first[1] * second[1] + first[2] * second[2];
        private static bool Solve(double[] input, double[] rhs, out double[] result)
        {
            int size = rhs.Length;
            double[] matrix = (double[])input.Clone();
            result = (double[])rhs.Clone();
            for (int column = 0; column < size; column++)
            {
                int pivot = column;
                for (int row = column + 1; row < size; row++) if (Math.Abs(matrix[row * size + column]) > Math.Abs(matrix[pivot * size + column])) pivot = row;
                if (Math.Abs(matrix[pivot * size + column]) <= 1e-12) { Array.Clear(result); return false; }
                for (int entry = column; entry < size; entry++) (matrix[column * size + entry], matrix[pivot * size + entry]) = (matrix[pivot * size + entry], matrix[column * size + entry]);
                (result[column], result[pivot]) = (result[pivot], result[column]);
                for (int row = column + 1; row < size; row++)
                {
                    double factor = matrix[row * size + column] / matrix[column * size + column];
                    for (int entry = column; entry < size; entry++) matrix[row * size + entry] -= factor * matrix[column * size + entry];
                    result[row] -= factor * result[column];
                }
            }
            for (int row = size - 1; row >= 0; row--)
            {
                for (int column = row + 1; column < size; column++) result[row] -= matrix[row * size + column] * result[column];
                result[row] /= matrix[row * size + row];
            }
            return true;
        }
    }
}
