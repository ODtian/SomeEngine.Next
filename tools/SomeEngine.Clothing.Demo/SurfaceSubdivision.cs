using System.Numerics;

namespace SomeEngine.Clothing.Demo;

internal static class SurfaceSubdivision
{
    private readonly record struct Vertex(Vector3 Position, BoneInfluence[] Weights);

    public static (CharacterSurface Surface, bool[] Visibility) Refine(CharacterSurface surface, bool[] visibility)
    {
        List<Vertex> vertices = surface.Positions.Select((point, index) => new Vertex(point, surface.BoneWeights[index])).ToList();
        int vertexCount = vertices.Count;
        List<int>[] vertexFaces = Enumerable.Range(0, vertexCount).Select(_ => new List<int>()).ToArray();
        HashSet<int>[] neighbours = Enumerable.Range(0, vertexCount).Select(_ => new HashSet<int>()).ToArray();
        Dictionary<(int, int), List<int>> edgeFaces = [];
        static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);
        Vertex Blend(IEnumerable<(int Vertex, float Weight)> sources)
        {
            Vector3 position = Vector3.Zero;
            Dictionary<int, float> bones = [];
            foreach ((int index, float weight) in sources)
            {
                Vertex vertex = vertices[index];
                position += vertex.Position * weight;
                foreach (BoneInfluence influence in vertex.Weights)
                    bones[influence.Bone] = bones.GetValueOrDefault(influence.Bone) + influence.Weight * weight;
            }
            float total = bones.Values.Sum();
            BoneInfluence[] weights = bones.OrderByDescending(pair => pair.Value)
                .Select(pair => new BoneInfluence(pair.Key, pair.Value / total)).ToArray();
            return new(position, weights);
        }
        int[] centers = new int[surface.Faces.Length];
        for (int faceIndex = 0; faceIndex < surface.Faces.Length; faceIndex++)
        {
            int[] face = surface.Faces[faceIndex];
            centers[faceIndex] = vertices.Count;
            vertices.Add(Blend(face.Select(vertex => (vertex, 1f / face.Length))));
            for (int corner = 0; corner < face.Length; corner++)
            {
                int a = face[corner], b = face[(corner + 1) % face.Length];
                if (!edgeFaces.TryGetValue(Edge(a, b), out List<int>? adjacent)) edgeFaces.Add(Edge(a, b), adjacent = []);
                adjacent.Add(faceIndex);
                vertexFaces[a].Add(faceIndex);
                neighbours[a].Add(b); neighbours[b].Add(a);
            }
        }
        Dictionary<(int, int), int> midpoints = [];
        foreach (((int a, int b) edge, List<int> adjacent) in edgeFaces)
        {
            midpoints.Add(edge, vertices.Count);
            vertices.Add(adjacent.Count == 2
                ? Blend([(edge.a, .25f), (edge.b, .25f), (centers[adjacent[0]], .25f), (centers[adjacent[1]], .25f)])
                : Blend([(edge.a, .5f), (edge.b, .5f)]));
        }
        Vertex[] corners = new Vertex[vertexCount];
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            int[] boundary = neighbours[vertex].Where(other => edgeFaces[Edge(vertex, other)].Count == 1).ToArray();
            if (boundary.Length == 2)
            {
                corners[vertex] = Blend([(vertex, .75f), (boundary[0], .125f), (boundary[1], .125f)]);
                continue;
            }
            int count = neighbours[vertex].Count;
            List<(int, float)> sources = [(vertex, (float)(count - 2) / count)];
            sources.AddRange(neighbours[vertex].Select(other => (other, 1f / (count * count))));
            sources.AddRange(vertexFaces[vertex].Select(face => (centers[face], 1f / (count * vertexFaces[vertex].Count))));
            corners[vertex] = Blend(sources);
        }
        for (int vertex = 0; vertex < vertexCount; vertex++) vertices[vertex] = corners[vertex];

        List<Vector2> coordinates = surface.TextureCoordinates.ToList();
        Dictionary<(int, int), int> textureMidpoints = [];
        int TextureMidpoint(int a, int b)
        {
            var edge = Edge(a, b);
            if (textureMidpoints.TryGetValue(edge, out int index)) return index;
            index = coordinates.Count;
            textureMidpoints.Add(edge, index);
            coordinates.Add((coordinates[a] + coordinates[b]) * .5f);
            return index;
        }
        List<int[]> faces = [], textureFaces = [];
        List<int> materials = [];
        List<bool> visible = [];
        for (int faceIndex = 0; faceIndex < surface.Faces.Length; faceIndex++)
        {
            int[] face = surface.Faces[faceIndex], texture = surface.TextureFaces[faceIndex];
            int textureCenter = coordinates.Count;
            coordinates.Add(texture.Aggregate(Vector2.Zero, (sum, vertex) => sum + coordinates[vertex]) / texture.Length);
            for (int corner = 0; corner < face.Length; corner++)
            {
                int next = (corner + 1) % face.Length, previous = (corner + face.Length - 1) % face.Length;
                faces.Add([face[corner], midpoints[Edge(face[corner], face[next])], centers[faceIndex], midpoints[Edge(face[previous], face[corner])]]);
                textureFaces.Add([texture[corner], TextureMidpoint(texture[corner], texture[next]), textureCenter,
                    TextureMidpoint(texture[previous], texture[corner])]);
                materials.Add(surface.FaceMaterials[faceIndex]); visible.Add(visibility[faceIndex]);
            }
        }
        Vector3[] positions = vertices.Select(vertex => vertex.Position).ToArray();
        int[] triangles = CharacterAsset.Triangulate(faces);
        return (new CharacterSurface
        {
            Positions = positions, Normals = CharacterAsset.CalculateNormals(positions, triangles),
            BoneWeights = vertices.Select(vertex => vertex.Weights).ToArray(),
            Faces = faces.ToArray(), Triangles = triangles, TextureCoordinates = coordinates.ToArray(),
            TextureFaces = textureFaces.ToArray(), FaceMaterials = materials.ToArray(),
        }, visible.ToArray());
    }
}
