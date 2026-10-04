using System.Diagnostics;
using System.Numerics;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Mikktspace.NET;
using SharpGLTF.Schema2;
using SomeEngine.Assets.Importing;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace SomeEngine.Assets.Importers;

/// <summary>Expands glTF triangle primitives into shared import data.</summary>
[AssetImporter("2f22d8f8-1176-42db-a35e-eae03b06a15c", typeof(VertexStreamsBlock),
    SettingsProvider = typeof(GltfMeshSettingsProvider),
    VersionInputs = new[] { typeof(ModelRoot), typeof(MikkGenerator), typeof(MeshOptimizer.Meshopt) })]
public sealed partial class GltfVertexStreamsImporter : IAssetImporter<GltfMesh, GltfMeshSettings>
{
    public ValueTask ImportAsync(GltfMesh source, GltfMeshSettings settings, ImportContext context, ImportOutput output,
        CancellationToken cancellationToken)
    {
        JsonNode document = JsonNode.Parse(source.LogicalParent.GetJsonPreview())!;
        var accessors = new GltfAccessorStreams(source.LogicalParent, document);
        List<VertexPrimitive> primitives = [];
        List<VertexImportDiagnostic> diagnostics = [];
        for (int index = 0; index < source.Primitives.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MeshPrimitive primitive = source.Primitives[index];
            if (primitive.DrawPrimitiveType is not (PrimitiveType.TRIANGLES or PrimitiveType.TRIANGLE_STRIP or PrimitiveType.TRIANGLE_FAN))
            {
                Report(diagnostics, index, VertexImportDiagnosticKind.UnsupportedTopology,
                    $"Skipped {primitive.DrawPrimitiveType}; mesh vertex streams use triangles.");
                continue;
            }
            JsonNode description = document["meshes"]![source.LogicalIndex]!["primitives"]![index]!;
            if (description["extensions"]?["KHR_draco_mesh_compression"] is not null)
            {
                Report(diagnostics, index, VertexImportDiagnosticKind.UnsupportedCompression,
                    "Skipped Draco primitive; SharpGLTF 1.0.6 provides no Draco decoder.");
                continue;
            }
            try { primitives.Add(ReadPrimitive(primitive, index, accessors, diagnostics, cancellationToken)); }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
            {
                Report(diagnostics, index, VertexImportDiagnosticKind.UnsupportedCompression, $"Skipped primitive: {error.Message}");
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
            {
                Report(diagnostics, index, VertexImportDiagnosticKind.InvalidPrimitive, $"Skipped primitive: {error.Message}");
            }
        }
        var block = new VertexStreamsBlock(primitives.ToArray(), diagnostics.ToArray());
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
        return ValueTask.CompletedTask;
    }

    private static VertexPrimitive ReadPrimitive(MeshPrimitive source, int index, GltfAccessorStreams accessors,
        List<VertexImportDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        Dictionary<string, VertexStream> attributes = ReadAttributes(source.VertexAccessors, accessors);
        if (!attributes.TryGetValue("POSITION", out VertexStream? positions) || positions.ComponentCount != 3)
            throw new InvalidDataException("A triangle primitive needs a POSITION stream with three components.");
        int count = positions.Count;
        foreach (VertexStream stream in attributes.Values)
            if (stream.Count != count) throw new InvalidDataException("Vertex accessor counts differ.");
        IReadOnlyDictionary<string, VertexStream>[] targets = new IReadOnlyDictionary<string, VertexStream>[source.MorphTargetsCount];
        for (int target = 0; target < targets.Length; target++)
        {
            Dictionary<string, VertexStream> streams = ReadAttributes(source.GetMorphTargetAccessors(target), accessors, morph: true);
            if (streams.Values.Any(stream => stream.Count != count)) throw new InvalidDataException("Morph accessor counts differ from vertex accessors.");
            targets[target] = new ReadOnlyDictionary<string, VertexStream>(streams);
        }
        uint[] indices = ReadTriangles(source, accessors, count);
        uint[] sourceVertices = Enumerable.Range(0, count).Select(static value => (uint)value).ToArray();
        VertexStream? sourceTangents = null;
        VertexStream?[] sourceMorphTangents = [];
        bool expanded = false;
        if (!attributes.ContainsKey("NORMAL"))
        {
            Expand(ref attributes, ref targets, ref sourceVertices, ref indices);
            expanded = true;
            attributes["NORMAL"] = GenerateFlatNormals(attributes["POSITION"]);
            GenerateMorphNormals(attributes, ref targets);
            if (attributes.Remove("TANGENT", out VertexStream? tangents)) sourceTangents = tangents;
        }
        MaterialChannel? normalTexture = source.Material?.FindChannel("Normal");
        if (!attributes.ContainsKey("TANGENT") && normalTexture?.Texture is not null && indices.Length > 0)
        {
            int coordinates = normalTexture.Value.TextureCoordinate;
            string name = $"TEXCOORD_{coordinates}";
            if (!attributes.TryGetValue(name, out VertexStream? uv))
                Report(diagnostics, index, VertexImportDiagnosticKind.MissingTextureCoordinates,
                    $"Normal texture requests {name}; tangent generation needs this source stream.");
            else
            {
                try
                {
                    VertexStream tangents = GenerateTangents(attributes["POSITION"], attributes["NORMAL"], uv, indices);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!expanded) Expand(ref attributes, ref targets, ref sourceVertices, ref indices);
                    attributes["TANGENT"] = tangents;
                    sourceMorphTangents = GenerateMorphTangents(attributes, ref targets, name, indices);
                }
                catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidDataException)
                {
                    Report(diagnostics, index, VertexImportDiagnosticKind.TangentGenerationFailed, error.Message);
                }
            }
        }
        return new(index, source.Material?.LogicalIndex ?? -1, indices, sourceVertices, attributes, targets, sourceTangents, sourceMorphTangents);
    }

    private static Dictionary<string, VertexStream> ReadAttributes(IReadOnlyDictionary<string, Accessor> source,
        GltfAccessorStreams accessors, bool morph = false)
        => source.ToDictionary(static pair => pair.Key, pair => accessors.Read(pair.Key, pair.Value, morph), StringComparer.Ordinal);

    private static uint[] ReadTriangles(MeshPrimitive primitive, GltfAccessorStreams accessors, int count)
    {
        uint[] source;
        if (primitive.IndexAccessor is Accessor indices)
        {
            if (indices.Dimensions != DimensionType.SCALAR
                || indices.Encoding is not (EncodingType.UNSIGNED_BYTE or EncodingType.UNSIGNED_SHORT or EncodingType.UNSIGNED_INT))
                throw new InvalidDataException("Triangle indices require an unsigned scalar accessor.");
            source = accessors.Read("indices", indices).Values.ToArray().Select(static value => checked((uint)value)).ToArray();
        }
        else source = Enumerable.Range(0, count).Select(static value => (uint)value).ToArray();
        if (source.Any(vertex => vertex >= count)) throw new InvalidDataException("Triangle index exceeds the vertex count.");
        if (primitive.DrawPrimitiveType == PrimitiveType.TRIANGLES)
        {
            if (source.Length % 3 != 0) throw new InvalidDataException("Triangle list index count must be a multiple of three.");
            return source;
        }
        if (source.Length < 3) return [];
        uint[] triangles = new uint[checked((source.Length - 2) * 3)];
        for (int triangle = 0; triangle < source.Length - 2; triangle++)
        {
            int start = triangle * 3;
            if (primitive.DrawPrimitiveType == PrimitiveType.TRIANGLE_FAN)
            {
                triangles[start] = source[0]; triangles[start + 1] = source[triangle + 1];
            }
            else
            {
                triangles[start] = source[triangle + (triangle & 1)];
                triangles[start + 1] = source[triangle + 1 - (triangle & 1)];
            }
            triangles[start + 2] = source[triangle + 2];
        }
        return triangles;
    }

    private static void Expand(ref Dictionary<string, VertexStream> attributes,
        ref IReadOnlyDictionary<string, VertexStream>[] targets, ref uint[] sourceVertices, ref uint[] indices)
    {
        uint[] triangles = indices;
        attributes = attributes.ToDictionary(static pair => pair.Key, pair => ExpandStream(pair.Value, triangles), StringComparer.Ordinal);
        targets = targets.Select(target => (IReadOnlyDictionary<string, VertexStream>)new ReadOnlyDictionary<string, VertexStream>(
            target.ToDictionary(static pair => pair.Key, pair => ExpandStream(pair.Value, triangles), StringComparer.Ordinal))).ToArray();
        uint[] originalVertices = sourceVertices;
        sourceVertices = triangles.Select(vertex => originalVertices[vertex]).ToArray();
        indices = Enumerable.Range(0, triangles.Length).Select(static value => (uint)value).ToArray();
    }

    private static VertexStream ExpandStream(VertexStream stream, uint[] indices)
    {
        int components = stream.ComponentCount;
        double[] values = new double[checked(indices.Length * components)];
        for (int index = 0; index < indices.Length; index++)
            stream.Values.Span.Slice(checked((int)indices[index] * components), components).CopyTo(values.AsSpan(index * components, components));
        return new(components, values);
    }

    private static VertexStream GenerateFlatNormals(VertexStream positions)
    {
        double[] normals = new double[positions.Values.Length];
        for (int vertex = 0; vertex < positions.Count; vertex += 3)
        {
            Vector3 a = ReadVector(positions, vertex), b = ReadVector(positions, vertex + 1), c = ReadVector(positions, vertex + 2);
            Vector3 cross = Vector3.Cross(b - a, c - a);
            Vector3 normal = cross.LengthSquared() > 0 ? Vector3.Normalize(cross) : Vector3.Zero;
            for (int corner = 0; corner < 3; corner++)
            {
                int start = (vertex + corner) * 3;
                normals[start] = normal.X; normals[start + 1] = normal.Y; normals[start + 2] = normal.Z;
            }
        }
        return new(3, normals);
    }

    private static VertexStream GenerateTangents(VertexStream positions, VertexStream normals, VertexStream uv, uint[] indices)
    {
        if (normals.ComponentCount != 3 || uv.ComponentCount != 2)
            throw new InvalidDataException("Tangent generation needs NORMAL VEC3 and texture coordinates VEC2.");
        double[] values = new double[checked(indices.Length * 4)];
        int GetVertexIndex(int face, int corner) => checked((int)indices[face * 3 + corner]);
        void ReadPosition(int face, int corner, out float x, out float y, out float z)
        {
            Vector3 value = ReadVector(positions, GetVertexIndex(face, corner));
            x = value.X;
            y = value.Y;
            z = value.Z;
        }
        void ReadNormal(int face, int corner, out float x, out float y, out float z)
        {
            Vector3 value = ReadVector(normals, GetVertexIndex(face, corner));
            x = value.X;
            y = value.Y;
            z = value.Z;
        }
        void ReadCoordinates(int face, int corner, out float u, out float v)
        {
            int start = GetVertexIndex(face, corner) * 2;
            u = (float)uv.Values.Span[start];
            v = (float)uv.Values.Span[start + 1];
        }
        void WriteTangent(int face, int corner, float x, float y, float z, float sign)
        {
            int start = (face * 3 + corner) * 4;
            values[start] = x;
            values[start + 1] = y;
            values[start + 2] = z;
            values[start + 3] = sign;
        }
        if (!MikkGenerator.GenerateTangentSpace(indices.Length / 3, static _ => 3, ReadPosition, ReadNormal, ReadCoordinates, WriteTangent))
            throw new InvalidDataException("MikkTSpace tangent generation failed.");
        return new(4, values);
    }

    private static Vector3 ReadVector(VertexStream stream, int vertex)
    {
        ReadOnlySpan<double> values = stream.Values.Span.Slice(vertex * 3, 3);
        return new((float)values[0], (float)values[1], (float)values[2]);
    }

    private static void Report(List<VertexImportDiagnostic> diagnostics, int primitive, VertexImportDiagnosticKind kind, string message)
    {
        diagnostics.Add(new(primitive, kind, message));
        Trace.TraceWarning($"glTF primitive {primitive}: {message}");
    }
}
