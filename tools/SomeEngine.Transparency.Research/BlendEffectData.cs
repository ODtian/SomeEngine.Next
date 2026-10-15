using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Transparency.Research;

public enum BlendEffect { Xray, Decals }
public enum BlendMethod { Forward, Tile, Cluster, ClusterCached }

public sealed class BlendEffectData
{
    public const int TileSize = 16;
    public const int DepthSlices = 32;
    public const float NearDepth = 1;
    public const float FarDepth = 128;

    public BlendEffectData(BlendEffect effect, int count, int width, int height, bool spheres = false, float depthOffset = 0)
    {
        Effect = effect;
        Width = width;
        Height = height;
        TileColumns = (width + TileSize - 1) / TileSize;
        TileRows = (height + TileSize - 1) / TileSize;
        var bounds = new List<BlendBounds>();
        var triangles = new List<BlendTriangle>();
        var coverage = new List<TriangleCoverage>();
        var random = new Random(74123);
        if (effect == BlendEffect.Decals)
        {
            for (int index = 0; index < count; index++)
            {
                float x = (float)random.NextDouble() * 1.5f - .75f;
                float y = (float)random.NextDouble() * 1.5f - .75f;
                float z = depthOffset + 2 + (index + .5f) * 62 / count;
                bounds.Add(new(new(x - .36f, y - .36f, x + .36f, y + .36f), new(z, z + 4), (uint)index, 0));
            }
        }
        else
        {
            for (int model = 0; model < count; model++)
            {
                float depth = depthOffset + 2 + (model + .5f) * 62 / count;
                if (spheres)
                {
                    for (int latitude = 0; latitude < 8; latitude++)
                    for (int longitude = 0; longitude < 12; longitude++)
                    {
                        BlendVertex a = Sphere(latitude, longitude), b = Sphere(latitude, longitude + 1);
                        BlendVertex c = Sphere(latitude + 1, longitude + 1), d = Sphere(latitude + 1, longitude);
                        if (latitude > 0) Add(a, b, c, (uint)model);
                        if (latitude < 7) Add(a, c, d, (uint)model);
                    }
                    BlendVertex Sphere(int latitude, int longitude)
                    {
                        float u = longitude / 12f, v = latitude / 8f;
                        float theta = v * MathF.PI, phi = u * MathF.Tau;
                        Vector3 normal = new(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
                        return Vertex(new(.12f * MathF.Sin(model) + normal.X * .42f,
                            .12f * MathF.Cos(model) + normal.Y * .42f, depth + normal.Z), normal, u, v);
                    }
                }
                else
                {
                    float angle = model * .173f;
                    Vector3 normal = Vector3.Normalize(new(.2f * MathF.Sin(angle), .2f * MathF.Cos(angle), -1));
                    BlendVertex Point(float x, float y, float u, float v) => Vertex(new(
                        x * MathF.Cos(angle) - y * MathF.Sin(angle), x * MathF.Sin(angle) + y * MathF.Cos(angle), depth), normal, u, v);
                    BlendVertex a = Point(-.68f, -.68f, 0, 0), b = Point(.68f, -.68f, 1, 0);
                    BlendVertex c = Point(.68f, .68f, 1, 1), d = Point(-.68f, .68f, 0, 1);
                    Add(a, b, c, (uint)model);
                    Add(a, c, d, (uint)model);
                }
            }
        }
        Bounds = bounds.ToArray();
        Triangles = triangles.ToArray();
        Coverage = coverage.ToArray();
        var tiles = Enumerable.Range(0, TileColumns * TileRows).Select(static _ => new List<uint>()).ToArray();
        for (int index = 0; index < Bounds.Length; index++)
        {
            Vector4 rectangle = Bounds[index].Rectangle;
            int left = Math.Clamp((int)MathF.Floor((rectangle.X + 1) * .5f * width / TileSize), 0, TileColumns - 1);
            int right = Math.Clamp((int)MathF.Floor((rectangle.Z + 1) * .5f * width / TileSize), 0, TileColumns - 1);
            int top = Math.Clamp((int)MathF.Floor((1 - rectangle.W) * .5f * height / TileSize), 0, TileRows - 1);
            int bottom = Math.Clamp((int)MathF.Floor((1 - rectangle.Y) * .5f * height / TileSize), 0, TileRows - 1);
            for (int row = top; row <= bottom; row++)
            for (int column = left; column <= right; column++) tiles[row * TileColumns + column].Add((uint)index);
        }
        References = tiles.SelectMany(static tile => tile).ToArray();
        var words = new List<BlendWord>();
        TileWords = new BlendRange[tiles.Length];
        int firstReference = 0;
        for (int index = 0; index < tiles.Length; index++)
        {
            int firstWord = words.Count;
            for (int first = 0; first < tiles[index].Count; first += 32)
                words.Add(new((uint)(firstReference + first), (uint)Math.Min(32, tiles[index].Count - first)));
            TileWords[index] = new((uint)firstWord, (uint)(words.Count - firstWord));
            firstReference += tiles[index].Count;
        }
        Words = words.ToArray();
        Pattern = new Vector4[64 * 64];
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            float checker = ((x / 8 + y / 8) & 1) == 0 ? .35f : 1;
            Pattern[y * 64 + x] = new(checker, .3f + .7f * x / 63, .3f + .7f * y / 63, .3f + .6f * checker);
        }

        BlendVertex Vertex(Vector3 position, Vector3 normal, float u, float v) => new(new(
            MathF.Round((position.X + 1) * width * 128) / (width * 128) - 1,
            1 - MathF.Round((1 - position.Y) * height * 128) / (height * 128), position.Z), u, normal, v);

        void Add(BlendVertex a, BlendVertex b, BlendVertex c, uint material)
        {
            uint index = (uint)triangles.Count;
            triangles.Add(new(a, b, c));
            Vector3 minimum = Vector3.Min(a.Position, Vector3.Min(b.Position, c.Position));
            Vector3 maximum = Vector3.Max(a.Position, Vector3.Max(b.Position, c.Position));
            bounds.Add(new(new(minimum.X, minimum.Y, maximum.X, maximum.Y), new(minimum.Z, maximum.Z), material, 0));
            coverage.Add(TriangleCoverage.Create(new(new(a.Position, 1), new(b.Position, 1), new(c.Position, 1), Vector4.One, index), material));
        }
    }

    public BlendEffect Effect { get; }
    public int Width { get; }
    public int Height { get; }
    public int TileColumns { get; }
    public int TileRows { get; }
    public BlendBounds[] Bounds { get; }
    public BlendTriangle[] Triangles { get; }
    public TriangleCoverage[] Coverage { get; }
    public uint[] References { get; }
    public BlendRange[] TileWords { get; }
    public BlendWord[] Words { get; }
    public Vector4[] Pattern { get; }

    public uint[] CreateSettings(Matrix4x4 projection, float xrayDistance)
    {
        var values = new uint[64];
        values[0] = (uint)Width; values[1] = (uint)Height;
        values[2] = (uint)TileColumns; values[3] = (uint)TileRows;
        values[4] = (uint)Bounds.Length; values[5] = (uint)Words.Length;
        values[6] = DepthSlices;
        values[8] = BitConverter.SingleToUInt32Bits(projection.M33);
        values[9] = BitConverter.SingleToUInt32Bits(projection.M34);
        values[10] = BitConverter.SingleToUInt32Bits(projection.M43);
        values[11] = BitConverter.SingleToUInt32Bits(projection.M44);
        values[12] = BitConverter.SingleToUInt32Bits(NearDepth);
        values[13] = BitConverter.SingleToUInt32Bits(FarDepth);
        values[14] = BitConverter.SingleToUInt32Bits(xrayDistance);
        values[15] = BitConverter.SingleToUInt32Bits(.3f);
        return values;
    }

    public static Matrix4x4 Projection => new(1, 0, 0, 0, 0, 1, 0, 0,
        0, 0, 1f / 127, -1, 0, 0, 128f / 127, 0);
}

[StructLayout(LayoutKind.Sequential)]
public readonly record struct BlendVertex(Vector3 Position, float U, Vector3 Normal, float V);
[StructLayout(LayoutKind.Sequential)]
public readonly record struct BlendTriangle(BlendVertex A, BlendVertex B, BlendVertex C);
[StructLayout(LayoutKind.Sequential)]
public readonly record struct BlendBounds(Vector4 Rectangle, Vector2 DepthRange, uint MaterialIndex, uint Padding);
[StructLayout(LayoutKind.Sequential)]
public readonly record struct BlendRange(uint FirstWord, uint WordCount);
[StructLayout(LayoutKind.Sequential)]
public readonly record struct BlendWord(uint FirstReference, uint ReferenceCount);
