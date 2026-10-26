using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Job;

namespace SomeEngine.Transparency.Research;

public enum BlendEffect { Xray, Decals }
public enum BlendMethod { Forward, Tile, Cluster, ClusterCached }
public enum BlendScene { DepthBands, Assembly, DecalLayers, SparseDecals }

public sealed class BlendEffectData
{
    public const int TileSize = 16;
    public const int DepthSlices = 32;
    public const float NearDepth = 1;
    public const float FarDepth = 128;

    public BlendEffectData(BlendEffect effect, int count, int width, int height, bool spheres = false, float depthOffset = 0,
        BlendScene scene = BlendScene.DepthBands)
    {
        Effect = effect;
        Scene = scene;
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
                float radius = .36f;
                if (scene is BlendScene.DecalLayers or BlendScene.SparseDecals)
                {
                    int patch = scene == BlendScene.DecalLayers ? index / 8 : index;
                    x = -.8f + 1.6f * ((patch * 37 % 97) / 96f);
                    y = -.7f + 1.4f * ((patch * 53 % 89) / 88f);
                    radius = scene == BlendScene.DecalLayers ? .20f : .07f;
                    float receiverDepth = x < 0 ? 12 : 36;
                    z = depthOffset + receiverDepth - .75f + (index % 4) * 6;
                }
                float thickness = scene == BlendScene.DepthBands ? 4 : 1.5f;
                bounds.Add(new(new(x - radius, y - radius, x + radius, y + radius), new(z, z + thickness), (uint)index, 0));
            }
        }
        else
        {
            for (int model = 0; model < count; model++)
            {
                float depth = depthOffset + 2 + (model + .5f) * 62 / count;
                bool assembly = scene == BlendScene.Assembly;
                Vector2 center = assembly ? new(-.78f + (model % 8) * .22f, -.66f + (model / 8) * .19f)
                    : new(.12f * MathF.Sin(model), .12f * MathF.Cos(model));
                float radius = assembly ? .09f : .42f;
                if (assembly) depth = depthOffset + 14 + (model % 8 / 2) * 6 + (model / 8 % 3) * .4f;
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
                        return Vertex(new(center.X + normal.X * radius,
                            center.Y + normal.Y * radius, depth + normal.Z), normal, u, v);
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
        var tileCoverage = new TileCoverage[Bounds.Length];
        Scheduler jobs = JobSystem.Current;
        jobs.ExecuteParallel(new PrepareCoverage(Bounds, width, height, TileColumns, TileRows, tileCoverage),
            Bounds.Length, JobScheduleOptions.AutomaticBatchSize,
            [JobResourceAccess.Read(jobs, Bounds), JobResourceAccess.Write(jobs, tileCoverage)]);
        var references = new TileReferenceData(tileCoverage, TileColumns, TileRows);
        References = references.References;
        TileWords = new BlendRange[references.Ranges.Length];
        uint wordCount = 0;
        for (int tile = 0; tile < TileWords.Length; tile++)
        {
            uint countForTile = (references.Ranges[tile].TriangleCount + 31) / 32;
            TileWords[tile] = new(wordCount, countForTile);
            wordCount += countForTile;
        }
        Words = new BlendWord[wordCount];
        jobs.ExecuteParallel(new FillWords(references.Ranges, TileWords, Words), TileWords.Length,
            JobScheduleOptions.AutomaticBatchSize,
            [JobResourceAccess.Read(jobs, references.Ranges), JobResourceAccess.Read(jobs, TileWords), JobResourceAccess.Write(jobs, Words)]);
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

    private readonly struct PrepareCoverage(BlendBounds[] bounds, int width, int height, int columns, int rows,
        TileCoverage[] output) : IJobParallelFor
    {
        public void Execute(int index)
        {
            Vector4 rectangle = bounds[index].Rectangle;
            int left = Math.Clamp((int)MathF.Floor((rectangle.X + 1) * .5f * width / TileSize), 0, columns - 1);
            int right = Math.Clamp((int)MathF.Floor((rectangle.Z + 1) * .5f * width / TileSize), 0, columns - 1);
            int top = Math.Clamp((int)MathF.Floor((1 - rectangle.W) * .5f * height / TileSize), 0, rows - 1);
            int bottom = Math.Clamp((int)MathF.Floor((1 - rectangle.Y) * .5f * height / TileSize), 0, rows - 1);
            output[index] = new(left, top, right, bottom);
        }
    }

    private readonly struct FillWords(TileRange[] references, BlendRange[] tiles, BlendWord[] output) : IJobParallelFor
    {
        public void Execute(int tile)
        {
            TileRange range = references[tile];
            BlendRange words = tiles[tile];
            for (uint word = 0; word < words.WordCount; word++)
            {
                uint first = word * 32;
                output[words.FirstWord + word] = new(range.FirstTriangle + first, Math.Min(32u, range.TriangleCount - first));
            }
        }
    }

    public BlendEffect Effect { get; }
    public BlendScene Scene { get; }
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
        values[7] = (uint)Scene;
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
