using System.Numerics;
using System.Runtime.InteropServices;
using SharpGLTF.Schema2;
using StbImageSharp;

namespace SomeEngine.Render.Cluster.Tests;

internal sealed class RayShadowScene
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal readonly record struct Vertex(Vector3 Position, Vector3 Normal, Vector4 Tangent, Vector2 TexCoord);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal readonly record struct Geometry(uint VertexStart, uint IndexStart,
        uint AlphaStart, uint AlphaWidth, uint AlphaHeight,
        uint NormalStart, uint NormalWidth, uint NormalHeight, float AlphaCutoff, float NormalScale);

    internal sealed record Mesh(GeometryRange[] Geometries);
    internal readonly record struct GeometryRange(uint VertexStart, uint VertexCount,
        uint IndexStart, uint IndexCount, bool Opaque);
    internal readonly record struct Instance(int Mesh, Matrix4x4 Transform, uint GeometryStart);

    internal Vertex[] Vertices { get; }
    internal uint[] Indices { get; }
    internal Geometry[] Geometries { get; }
    internal uint[] Texels { get; }
    internal Mesh[] Meshes { get; }
    internal Instance[] Instances { get; }

    internal RayShadowScene(string source, bool sphereGrid)
    {
        ModelRoot model = ModelRoot.Load(source);
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        var geometries = new List<Geometry>();
        var texels = new List<uint> { 0xffff8080, 0xffffffff };
        var images = new Dictionary<SharpGLTF.Schema2.Image, (uint Start, uint Width, uint Height)>();
        var meshes = new List<Mesh>();
        var meshStarts = new List<uint>();
        foreach (SharpGLTF.Schema2.Mesh mesh in model.LogicalMeshes)
        {
            meshStarts.Add((uint)geometries.Count);
            var ranges = new List<GeometryRange>();
            foreach (MeshPrimitive primitive in mesh.Primitives)
            {
                var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
                var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var tangents = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array();
                var uv = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                uint vertexStart = (uint)vertices.Count, indexStart = (uint)indices.Count;
                for (int vertex = 0; vertex < positions.Count; vertex++)
                    vertices.Add(new(positions[vertex], normals?[vertex] ?? Vector3.UnitY,
                        tangents?[vertex] ?? new Vector4(1, 0, 0, 1), uv?[vertex] ?? Vector2.Zero));
                if (primitive.GetIndexAccessor() is { } accessor)
                    indices.AddRange(accessor.AsIndicesArray());
                else indices.AddRange(Enumerable.Range(0, positions.Count).Select(value => (uint)value));

                var alpha = primitive.Material?.FindChannel("BaseColor");
                var normal = primitive.Material?.FindChannel("Normal");
                var alphaImage = ReadImage(alpha?.Texture?.PrimaryImage, (1u, 1u, 1u));
                var normalImage = sphereGrid ? (0u, 1u, 1u) : ReadImage(normal?.Texture?.PrimaryImage, (0u, 1u, 1u));
                float cutoff = primitive.Material?.Alpha == AlphaMode.MASK
                    ? primitive.Material.AlphaCutoff / Math.Max(alpha?.Color.W ?? 1, 1e-8f) : -1;
                geometries.Add(new(vertexStart, indexStart, alphaImage.Item1, alphaImage.Item2, alphaImage.Item3,
                    normalImage.Item1, normalImage.Item2, normalImage.Item3, cutoff,
                    sphereGrid ? 1 : normal?.GetFactor("NormalScale") ?? 1));
                ranges.Add(new(vertexStart, (uint)positions.Count, indexStart,
                    (uint)indices.Count - indexStart, cutoff < 0));
            }
            meshes.Add(new(ranges.ToArray()));
        }

        Meshes = meshes.ToArray();
        Vertices = vertices.ToArray();
        Indices = indices.ToArray();
        Geometries = geometries.ToArray();
        Texels = texels.ToArray();
        Instances = sphereGrid
            ? ShadowReferenceScene.Meshes("directional-grid", true).Select(value => new Instance(0,
                Matrix4x4.CreateScale(value.Scale) * Matrix4x4.CreateTranslation(value.Position), meshStarts[0])).ToArray()
            : Node.Flatten(model.DefaultScene).Where(node => node.Mesh is not null).Select(node =>
            {
                // The scene importer expresses each source node through QVVS.
                Matrix4x4.Decompose(node.WorldMatrix, out Vector3 scale, out Quaternion rotation, out Vector3 position);
                return new Instance(node.Mesh!.LogicalIndex,
                    Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position),
                    meshStarts[node.Mesh.LogicalIndex]);
            }).ToArray();

        (uint, uint, uint) ReadImage(SharpGLTF.Schema2.Image? sourceImage, (uint, uint, uint) fallback)
        {
            if (sourceImage is null) return fallback;
            if (images.TryGetValue(sourceImage, out var existing)) return existing;
            byte[] encoded = string.IsNullOrWhiteSpace(sourceImage.Content.SourcePath)
                ? sourceImage.Content.Content.ToArray() : File.ReadAllBytes(sourceImage.Content.SourcePath);
            ImageResult image = ImageResult.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha);
            var result = ((uint)texels.Count, (uint)image.Width, (uint)image.Height);
            texels.AddRange(MemoryMarshal.Cast<byte, uint>(image.Data.AsSpan()).ToArray());
            images.Add(sourceImage, result);
            return result;
        }
    }
}
