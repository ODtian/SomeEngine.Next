using System.Collections.ObjectModel;
using System.Numerics;

namespace SomeEngine.Assets.Importers;

public sealed partial class GltfVertexStreamsImporter
{
    private static void GenerateMorphNormals(Dictionary<string, VertexStream> attributes,
        ref IReadOnlyDictionary<string, VertexStream>[] targets)
    {
        VertexStream positions = attributes["POSITION"], normals = attributes["NORMAL"];
        for (int index = 0; index < targets.Length; index++)
        {
            IReadOnlyDictionary<string, VertexStream> target = targets[index];
            if (target.ContainsKey("NORMAL")) continue;
            VertexStream moved = AddDelta(positions, target.GetValueOrDefault("POSITION"));
            VertexStream updated = GenerateFlatNormals(moved);
            var streams = target.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            streams["NORMAL"] = SubtractValues(updated, normals);
            targets[index] = new ReadOnlyDictionary<string, VertexStream>(streams);
        }
    }

    private static VertexStream?[] GenerateMorphTangents(Dictionary<string, VertexStream> attributes,
        ref IReadOnlyDictionary<string, VertexStream>[] targets, string coordinates, uint[] indices)
    {
        VertexStream positions = attributes["POSITION"], normals = attributes["NORMAL"], uv = attributes[coordinates];
        VertexStream tangents = attributes["TANGENT"];
        VertexStream?[] originalTangents = new VertexStream?[targets.Length];
        IReadOnlyDictionary<string, VertexStream>[] updatedTargets = new IReadOnlyDictionary<string, VertexStream>[targets.Length];
        for (int index = 0; index < targets.Length; index++)
        {
            IReadOnlyDictionary<string, VertexStream> target = targets[index];
            VertexStream moved = AddDelta(positions, target.GetValueOrDefault("POSITION"));
            VertexStream normalDelta = AddDelta(normals, target.GetValueOrDefault("NORMAL"));
            VertexStream updatedNormals = target.ContainsKey("NORMAL") ? NormalizeNormals(normalDelta) : normals;
            VertexStream updatedUv = AddDelta(uv, target.GetValueOrDefault(coordinates));
            VertexStream updated = GenerateTangents(moved, updatedNormals, updatedUv, indices);
            var streams = target.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            originalTangents[index] = streams.GetValueOrDefault("TANGENT");
            double[] deltas = new double[checked(positions.Count * 3)];
            for (int vertex = 0; vertex < positions.Count; vertex++)
            for (int component = 0; component < 3; component++)
                deltas[vertex * 3 + component] = (float)updated.Values.Span[vertex * 4 + component]
                    - (float)tangents.Values.Span[vertex * 4 + component];
            streams["TANGENT"] = new(3, deltas);
            updatedTargets[index] = new ReadOnlyDictionary<string, VertexStream>(streams);
        }
        targets = updatedTargets;
        return originalTangents;
    }

    private static VertexStream AddDelta(VertexStream basis, VertexStream? delta)
    {
        if (delta is null) return basis;
        if (basis.ComponentCount != delta.ComponentCount)
            throw new InvalidDataException("Morph accessor components must match their base stream.");
        double[] values = new double[basis.Values.Length];
        for (int index = 0; index < values.Length; index++)
            values[index] = (float)basis.Values.Span[index] + (float)delta.Values.Span[index];
        return new(basis.ComponentCount, values);
    }

    private static VertexStream SubtractValues(VertexStream updated, VertexStream basis)
    {
        double[] values = new double[basis.Values.Length];
        for (int index = 0; index < values.Length; index++)
            values[index] = (float)updated.Values.Span[index] - (float)basis.Values.Span[index];
        return new(basis.ComponentCount, values);
    }

    private static VertexStream NormalizeNormals(VertexStream stream)
    {
        double[] values = new double[stream.Values.Length];
        for (int vertex = 0; vertex < stream.Count; vertex++)
        {
            Vector3 value = ReadVector(stream, vertex);
            if (value.LengthSquared() > 0) value = Vector3.Normalize(value);
            int start = vertex * 3;
            values[start] = value.X;
            values[start + 1] = value.Y;
            values[start + 2] = value.Z;
        }
        return new(3, values);
    }
}
