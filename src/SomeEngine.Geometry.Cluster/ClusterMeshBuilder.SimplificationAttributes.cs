using System.Numerics;

namespace SomeEngine.Geometry.Cluster;

public static partial class ClusterMeshBuilder
{
    private sealed class ClusterSimplificationAttributes
    {
        private readonly RawAttribute[] _attributes;
        private readonly int[] _offsets;
        private readonly int _components;
        internal readonly float[] Values;
        internal bool CanSimplify => _components <= 32;

        internal ClusterSimplificationAttributes(IReadOnlyList<RawAttribute> attributes, int vertexCount)
        {
            _attributes = attributes.Where(static attribute => attribute.Name != "_MATERIAL_INDEX" &&
                !attribute.Name.StartsWith("JOINTS", StringComparison.Ordinal) &&
                !attribute.Name.StartsWith("WEIGHTS", StringComparison.Ordinal)).ToArray();
            _offsets = new int[_attributes.Length];
            for (int attribute = 0; attribute < _attributes.Length; attribute++)
            {
                _offsets[attribute] = _components;
                _components += _attributes[attribute].Dimension;
            }
            if (!CanSimplify) { Values = []; return; }
            Values = new float[checked(vertexCount * _components)];
            for (int vertex = 0; vertex < vertexCount; vertex++)
                for (int attribute = 0; attribute < _attributes.Length; attribute++)
                {
                    RawAttribute data = _attributes[attribute];
                    data.Data.AsSpan(vertex * data.Dimension, data.Dimension)
                        .CopyTo(Values.AsSpan(vertex * _components + _offsets[attribute]));
                }
        }

        internal float[] GroupWeights(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices)
        {
            var weights = new float[_components];
            if (_components == 0) return weights;
            Vector3 minimum = new(float.MaxValue), maximum = new(float.MinValue);
            double surfaceArea = 0;
            for (int triangle = 0; triangle < indices.Length; triangle += 3)
            {
                Vector3 a = positions[(int)indices[triangle]];
                Vector3 b = positions[(int)indices[triangle + 1]];
                Vector3 c = positions[(int)indices[triangle + 2]];
                minimum = Vector3.Min(minimum, Vector3.Min(a, Vector3.Min(b, c)));
                maximum = Vector3.Max(maximum, Vector3.Max(a, Vector3.Max(b, c)));
                surfaceArea += Vector3.Cross(b - a, c - a).Length() * .5;
            }
            float triangleSize = Math.Max((float)Math.Sqrt(surfaceArea / (indices.Length / 3)), 2e-5f);
            int exponent = Math.Clamp(Math.ILogB(.25f) - Math.ILogB(triangleSize), -126, 127);
            Vector3 extent = maximum - minimum;
            float positionExtent = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
            if (positionExtent == 0) positionExtent = 1;
            // UE weights use losslessly scaled positions. Meshopt's sparse groups
            // normalize positions by their extent, so bring attributes into that unit.
            float attributeScale = 1 / (MathF.ScaleB(1, exponent) * positionExtent);
            for (int attribute = 0; attribute < _attributes.Length; attribute++)
            {
                RawAttribute data = _attributes[attribute];
                float weight = data.Name switch
                {
                    "TANGENT" => .0625f,
                    _ when data.Name.StartsWith("COLOR", StringComparison.Ordinal) => .0625f,
                    _ => 1,
                };
                if (data.Name.StartsWith("TEXCOORD", StringComparison.Ordinal) && data.Dimension >= 2)
                {
                    double area = 0;
                    for (int triangle = 0; triangle < indices.Length; triangle += 3)
                    {
                        int a = (int)indices[triangle] * data.Dimension;
                        int b = (int)indices[triangle + 1] * data.Dimension;
                        int c = (int)indices[triangle + 2] * data.Dimension;
                        area += Math.Abs(.5 * ((data.Data[b] - data.Data[a]) * (data.Data[c + 1] - data.Data[a + 1]) -
                            (data.Data[b + 1] - data.Data[a + 1]) * (data.Data[c] - data.Data[a])));
                    }
                    weight = 1 / (128 * Math.Max((float)Math.Sqrt(area / (indices.Length / 3)), 1f / 1024));
                }
                Array.Fill(weights, weight * attributeScale, _offsets[attribute], data.Dimension);
                if (data.Name == "TANGENT" && data.Dimension >= 4)
                    weights[_offsets[attribute] + 3] = .5f * attributeScale;
            }
            return weights;
        }
    }
}
