using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace SomeEngine.Clothing.Demo;

public readonly record struct BoneInfluence(int Bone, float Weight);
public readonly record struct CharacterBone(string Name, int Parent, Vector3 Head, Vector3 Tail);

public sealed class CharacterSurface
{
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required int[][] Faces { get; init; }
    public required int[] Triangles { get; init; }
    public required BoneInfluence[][] BoneWeights { get; init; }
    public required Vector2[] TextureCoordinates { get; init; }
    public required int[][] TextureFaces { get; init; }
    public required int[] FaceMaterials { get; init; }
}

public sealed class CharacterAsset
{
    public CharacterBone[] Bones { get; }
    public CharacterSurface Body { get; }
    public CharacterSurface Clothing { get; }
    public CharacterSurface Hair { get; }
    public CharacterSurface Eyes { get; }
    public bool[] BodyFaceVisibility { get; }
    public int HeadBone { get; }
    public float WaistHeight { get; }
    public float CrotchHeight { get; }
    public int LeftThigh { get; }
    public int RightThigh { get; }
    public int LeftShin { get; }
    public int RightShin { get; }
    public int LeftFoot { get; }
    public int RightFoot { get; }

    public CharacterAsset()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Assets", "MakeHuman");
        List<Vector3> positions = [];
        List<Vector2> textureCoordinates = [];
        Dictionary<string, List<int[]>> groups = [];
        Dictionary<string, List<int[]>> textureGroups = [];
        string group = "";
        foreach (string line in File.ReadLines(Path.Combine(directory, "base.obj")))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts[0] == "v") positions.Add(new(Number(parts[1]), Number(parts[2]), Number(parts[3])));
            else if (parts[0] == "vt") textureCoordinates.Add(new(Number(parts[1]), Number(parts[2])));
            else if (parts[0] == "g")
            {
                group = parts[1];
                groups.TryAdd(group, []);
                textureGroups.TryAdd(group, []);
            }
            else if (parts[0] == "f")
            {
                groups[group].Add(parts.Skip(1).Select(part => int.Parse(part.Split('/')[0]) - 1).ToArray());
                textureGroups[group].Add(parts.Skip(1).Select(part => int.Parse(part.Split('/')[1]) - 1).ToArray());
            }
        }
        foreach (string line in File.ReadLines(Path.Combine(directory, "adult-female.target")))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            positions[int.Parse(parts[0])] += new Vector3(Number(parts[1]), Number(parts[2]), Number(parts[3]));
        }
        int[] bodyVertices = groups["body"].SelectMany(face => face).Distinct().ToArray();
        float floor = bodyVertices.Min(index => positions[index].Y);
        float scale = 1.7f / (bodyVertices.Max(index => positions[index].Y) - floor);
        for (int i = 0; i < positions.Count; i++)
            positions[i] = (positions[i] - new Vector3(0, floor, 0)) * scale;

        using JsonDocument skeleton = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "default.mhskel")));
        JsonElement boneData = skeleton.RootElement.GetProperty("bones");
        JsonElement jointData = skeleton.RootElement.GetProperty("joints");
        List<CharacterBone> bones = [];
        Dictionary<string, int> boneIndices = [];
        Vector3 Joint(string name)
        {
            int[] indices = jointData.GetProperty(name).EnumerateArray().Select(value => value.GetInt32()).ToArray();
            return indices.Aggregate(Vector3.Zero, (sum, index) => sum + positions[index]) / indices.Length;
        }
        int AddBone(string name)
        {
            if (boneIndices.TryGetValue(name, out int index)) return index;
            JsonElement data = boneData.GetProperty(name);
            string? parentName = data.GetProperty("parent").GetString();
            int parent = parentName is null ? -1 : AddBone(parentName);
            index = bones.Count;
            bones.Add(new(name, parent, Joint(data.GetProperty("head").GetString()!), Joint(data.GetProperty("tail").GetString()!)));
            boneIndices.Add(name, index);
            return index;
        }
        foreach (JsonProperty bone in boneData.EnumerateObject()) AddBone(bone.Name);
        Bones = bones.ToArray();

        List<BoneInfluence>[] weights = Enumerable.Range(0, positions.Count).Select(_ => new List<BoneInfluence>()).ToArray();
        using JsonDocument weightData = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "default_weights.mhw")));
        foreach (JsonProperty bone in weightData.RootElement.GetProperty("weights").EnumerateObject())
        foreach (JsonElement influence in bone.Value.EnumerateArray())
            weights[influence[0].GetInt32()].Add(new(boneIndices[bone.Name], influence[1].GetSingle()));
        BoneInfluence[][] normalizedWeights = weights.Select(values =>
        {
            BoneInfluence[] strongest = values.OrderByDescending(value => value.Weight).ToArray();
            if (strongest.Length == 0) return new[] { new BoneInfluence(boneIndices["root"], 1) };
            float sum = strongest.Sum(value => value.Weight);
            return strongest.Select(value => value with { Weight = value.Weight / sum }).ToArray();
        }).ToArray();

        CharacterSurface Surface(string name)
        {
            int[] indices = groups[name].SelectMany(face => face).Distinct().Order().ToArray();
            Dictionary<int, int> remap = indices.Select((index, order) => (index, order)).ToDictionary(pair => pair.index, pair => pair.order);
            Vector3[] points = indices.Select(index => positions[index]).ToArray();
            int[][] faces = groups[name].Select(face => face.Select(index => remap[index]).ToArray()).ToArray();
            int[] triangles = Triangulate(faces);
            return new()
            {
                Positions = points, Normals = CalculateNormals(points, triangles), Faces = faces, Triangles = triangles,
                BoneWeights = indices.Select(index => normalizedWeights[index]).ToArray(),
                TextureCoordinates = textureCoordinates.ToArray(), TextureFaces = textureGroups[name].ToArray(),
                FaceMaterials = new int[faces.Length],
            };
        }
        Body = Surface("body");
        Clothing = Surface("helper-tights");
        HeadBone = boneIndices["head"];
        Hair = ReadAccessory(directory, "bob01", positions, normalizedWeights);
        Eyes = ReadAccessory(directory, "eyes", positions, normalizedWeights);
        LeftThigh = boneIndices["upperleg01.L"]; RightThigh = boneIndices["upperleg01.R"];
        LeftShin = boneIndices["lowerleg01.L"]; RightShin = boneIndices["lowerleg01.R"];
        LeftFoot = boneIndices["foot.L"]; RightFoot = boneIndices["foot.R"];
        WaistHeight = Bones[boneIndices["spine03"]].Head.Y + .025f;
        CrotchHeight = Clothing.Positions.Where(point => MathF.Abs(point.X) < .00001f && point.Y < WaistHeight)
            .Min(point => point.Y);
        CharacterSurface top = ReadAccessory(directory, "sportstop", positions, normalizedWeights);
        int[] topFaces = Enumerable.Range(0, top.Faces.Length)
            .Where(index => top.Faces[index].Average(vertex => top.Positions[vertex].Y) > WaistHeight + .045f).ToArray();
        string[] coveredRanges = File.ReadAllText(Path.Combine(directory, "sportstop.mhclo"))
            .Split("delete_verts", StringSplitOptions.None)[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        HashSet<int> covered = [];
        for (int i = 0; i < coveredRanges.Length; i++)
        {
            int first = int.Parse(coveredRanges[i]), last = first;
            if (i + 1 < coveredRanges.Length && coveredRanges[i + 1] == "-")
            {
                last = int.Parse(coveredRanges[i + 2]);
                i += 2;
            }
            for (int vertex = first; vertex <= last; vertex++) covered.Add(vertex);
        }
        int[] bodySources = bodyVertices.Order().ToArray();
        bool[] bodyVisibility = Body.Faces.Select(face => !face.All(vertex => covered.Contains(bodySources[vertex])
            && Body.Positions[vertex].Y > WaistHeight + .06f)).ToArray();
        for (int subdivision = 0; subdivision < 2; subdivision++)
            (Body, bodyVisibility) = SurfaceSubdivision.Refine(Body, bodyVisibility);
        BodyFaceVisibility = bodyVisibility.Concat(Enumerable.Repeat(true, topFaces.Length)).ToArray();
        Body = AddTop(Body, top, topFaces);
    }

    public static int[][] FindLargestSurface(int[][] faces)
    {
        Dictionary<int, List<int>> connections = [];
        for (int face = 0; face < faces.Length; face++)
        foreach (int vertex in faces[face])
        {
            if (!connections.TryGetValue(vertex, out List<int>? adjacent)) connections.Add(vertex, adjacent = []);
            adjacent.Add(face);
        }
        HashSet<int> visited = [];
        List<int> largest = [];
        for (int first = 0; first < faces.Length; first++)
        {
            if (!visited.Add(first)) continue;
            List<int> component = [first];
            for (int i = 0; i < component.Count; i++)
            foreach (int vertex in faces[component[i]])
            foreach (int adjacent in connections[vertex])
                if (visited.Add(adjacent)) component.Add(adjacent);
            if (component.Count > largest.Count) largest = component;
        }
        return largest.Select(index => faces[index]).ToArray();
    }

    private static CharacterSurface ReadAccessory(string directory, string name, List<Vector3> reference, BoneInfluence[][] referenceWeights)
    {
        List<Vector2> uvs = [];
        List<int[]> faces = [], textureFaces = [];
        foreach (string line in File.ReadLines(Path.Combine(directory, name + ".obj")))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts[0] == "vt") uvs.Add(new(Number(parts[1]), Number(parts[2])));
            else if (parts[0] == "f")
            {
                faces.Add(parts.Skip(1).Select(part => int.Parse(part.Split('/')[0]) - 1).ToArray());
                textureFaces.Add(parts.Skip(1).Select(part => int.Parse(part.Split('/')[1]) - 1).ToArray());
            }
        }
        List<Vector3> positions = [];
        List<BoneInfluence[]> weights = [];
        Vector3 scale = Vector3.One;
        bool vertices = false;
        foreach (string line in File.ReadLines(Path.Combine(directory, name + ".mhclo")))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0][0] == '#') continue;
            if (parts[0].EndsWith("_scale", StringComparison.Ordinal))
            {
                int axis = parts[0][0] == 'x' ? 0 : parts[0][0] == 'y' ? 1 : 2;
                scale[axis] = MathF.Abs(reference[int.Parse(parts[1])][axis] - reference[int.Parse(parts[2])][axis]) / Number(parts[3]);
            }
            if (parts[0] == "verts") vertices = true;
            if (parts[0] == "delete_verts") vertices = false;
            if (!vertices || !char.IsDigit(parts[0][0])) continue;
            if (parts.Length == 1)
            {
                int index = int.Parse(parts[0]);
                positions.Add(reference[index]); weights.Add(referenceWeights[index]);
                continue;
            }
            Vector3 position = Vector3.Zero;
            Dictionary<int, float> blend = [];
            for (int corner = 0; corner < 3; corner++)
            {
                int index = int.Parse(parts[corner]);
                float amount = Number(parts[corner + 3]);
                position += reference[index] * amount;
                foreach (BoneInfluence influence in referenceWeights[index])
                    blend[influence.Bone] = blend.GetValueOrDefault(influence.Bone) + influence.Weight * amount;
            }
            position += new Vector3(Number(parts[6]), Number(parts[7]), Number(parts[8])) * scale;
            positions.Add(position);
            BoneInfluence[] strongest = blend.OrderByDescending(pair => pair.Value).Take(4)
                .Select(pair => new BoneInfluence(pair.Key, pair.Value)).ToArray();
            float total = strongest.Sum(value => value.Weight);
            weights.Add(strongest.Select(value => value with { Weight = value.Weight / total }).ToArray());
        }
        Vector3[] points = positions.ToArray();
        int[] triangles = Triangulate(faces);
        return new()
        {
            Positions = points, Normals = CalculateNormals(points, triangles), Faces = faces.ToArray(), Triangles = triangles,
            BoneWeights = weights.ToArray(), TextureCoordinates = uvs.ToArray(), TextureFaces = textureFaces.ToArray(),
            FaceMaterials = new int[faces.Count],
        };
    }

    private static CharacterSurface AddTop(CharacterSurface body, CharacterSurface top, int[] faceIndices)
    {
        int[] vertices = faceIndices.SelectMany(index => top.Faces[index]).Distinct().ToArray();
        Dictionary<int, int> remap = vertices.Select((index, order) => (index, order))
            .ToDictionary(pair => pair.index, pair => pair.order + body.Positions.Length);
        int[][] faces = body.Faces.Concat(faceIndices.Select(index => top.Faces[index].Select(vertex => remap[vertex]).ToArray())).ToArray();
        return new()
        {
            Positions = body.Positions.Concat(vertices.Select(index => top.Positions[index])).ToArray(),
            Normals = body.Normals.Concat(vertices.Select(index => top.Normals[index])).ToArray(),
            BoneWeights = body.BoneWeights.Concat(vertices.Select(index => top.BoneWeights[index])).ToArray(),
            Faces = faces, Triangles = Triangulate(faces),
            TextureCoordinates = body.TextureCoordinates.Concat(top.TextureCoordinates).ToArray(),
            TextureFaces = body.TextureFaces.Concat(faceIndices.Select(index => top.TextureFaces[index]
                .Select(vertex => vertex + body.TextureCoordinates.Length).ToArray())).ToArray(),
            FaceMaterials = body.FaceMaterials.Concat(Enumerable.Repeat(1, faceIndices.Length)).ToArray(),
        };
    }

    private static float Number(string value) => float.Parse(value, CultureInfo.InvariantCulture);

    public static int[] Triangulate(IEnumerable<int[]> faces)
        => faces.SelectMany(face => Enumerable.Range(1, face.Length - 2)
            .SelectMany(index => new[] { face[0], face[index], face[index + 1] })).ToArray();

    public static Vector3[] CalculateNormals(Vector3[] positions, int[] triangles)
    {
        Vector3[] normals = new Vector3[positions.Length];
        for (int i = 0; i < triangles.Length; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            Vector3 normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            normals[a] += normal; normals[b] += normal; normals[c] += normal;
        }
        for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.Normalize(normals[i]);
        return normals;
    }
}
