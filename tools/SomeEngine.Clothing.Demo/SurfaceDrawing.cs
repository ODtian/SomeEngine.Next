using System.Numerics;

namespace SomeEngine.Clothing.Demo;

public readonly record struct SurfaceMaterial(int Start, int Count, int MaterialIndex);

public sealed class SurfaceDrawing
{
    public int[] SourceVertices { get; }
    public float[] TextureCoordinates { get; }
    public int[] Triangles { get; }
    public SurfaceMaterial[] Materials { get; }

    public SurfaceDrawing(CharacterSurface surface, bool[]? visibleFaces = null)
    {
        Dictionary<(int, int), int> vertices = [];
        List<int> sources = [], triangles = [];
        List<float> coordinates = [];
        List<SurfaceMaterial> materials = [];
        for (int face = 0; face < surface.Faces.Length; face++)
        {
            if (visibleFaces is not null && !visibleFaces[face]) continue;
            int[] polygon = new int[surface.Faces[face].Length];
            for (int corner = 0; corner < polygon.Length; corner++)
            {
                int vertex = surface.Faces[face][corner], uv = surface.TextureFaces[face][corner];
                if (!vertices.TryGetValue((vertex, uv), out int index))
                {
                    index = sources.Count;
                    vertices.Add((vertex, uv), index);
                    sources.Add(vertex);
                    Vector2 point = surface.TextureCoordinates[uv];
                    coordinates.Add(point.X); coordinates.Add(point.Y);
                }
                polygon[corner] = index;
            }
            int material = surface.FaceMaterials[face], count = (polygon.Length - 2) * 3;
            if (materials.Count == 0 || materials[^1].MaterialIndex != material)
                materials.Add(new(triangles.Count, count, material));
            else materials[^1] = materials[^1] with { Count = materials[^1].Count + count };
            for (int i = 1; i < polygon.Length - 1; i++) triangles.AddRange([polygon[0], polygon[i], polygon[i + 1]]);
        }
        SourceVertices = sources.ToArray(); TextureCoordinates = coordinates.ToArray();
        Triangles = triangles.ToArray(); Materials = materials.ToArray();
    }

    public object Describe(CharacterSurface surface) => new
    {
        Positions = SourceVertices.SelectMany(index => new[] { surface.Positions[index].X, surface.Positions[index].Y, surface.Positions[index].Z }).ToArray(),
        Normals = SourceVertices.SelectMany(index => new[] { surface.Normals[index].X, surface.Normals[index].Y, surface.Normals[index].Z }).ToArray(),
        TextureCoordinates, Triangles,
    };
}
