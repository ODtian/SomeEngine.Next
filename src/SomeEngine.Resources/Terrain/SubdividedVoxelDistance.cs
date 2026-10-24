using System.Numerics;

namespace SomeEngine.Resources;

/// <summary>Bounds distance to the fixed trilinear zero surface by subdividing source cells.</summary>
public readonly record struct SubdividedVoxelDistance(int MaxSubdivisions) : IVoxelDistanceCpu
{
    public SubdividedVoxelDistance() : this(10) { }

    public float Rebuild(VoxelFieldRead source, VoxelDistanceSettings settings, Span<float> distances)
    {
        VoxelRegion read = settings.ReadRegion;
        var cells = new List<DistanceCell>();
        for (int z = read.MinZ; z + 1 < read.MaxZ; z++)
        for (int y = read.MinY; y + 1 < read.MaxY; y++)
        for (int x = read.MinX; x + 1 < read.MaxX; x++)
        {
            var cell = new DistanceCell(x, y, z,
                source[x, y, z], source[x + 1, y, z], source[x, y + 1, z], source[x + 1, y + 1, z],
                source[x, y, z + 1], source[x + 1, y, z + 1], source[x, y + 1, z + 1], source[x + 1, y + 1, z + 1]);
            if (cell.HasZero) cells.Add(cell);
        }

        float maximumError = 0f;
        float spacing = source.Grid.SampleSpacing;
        float tolerance = settings.Precision.DistanceTolerance / spacing;
        int maxDepth = Math.Min(MaxSubdivisions, 20);
        VoxelRegion range = settings.Range;
        int index = 0;
        for (int z = range.MinZ; z < range.MaxZ; z++)
        for (int y = range.MinY; y < range.MaxY; y++)
        for (int x = range.MinX; x < range.MaxX; x++)
        {
            float value = source[x, y, z];
            if (value == 0f) { distances[index++] = 0f; continue; }
            float upper = float.PositiveInfinity, lower = float.PositiveInfinity;
            foreach (DistanceCell cell in cells)
                Refine(cell, new(cell.X - x, cell.Y - y, cell.Z - z), tolerance, maxDepth, ref lower, ref upper);
            float error = float.IsFinite(upper) ? MathF.Max(0f, upper - MathF.Min(lower, upper)) * spacing : float.PositiveInfinity;
            maximumError = MathF.Max(maximumError, error);
            distances[index++] = MathF.CopySign(upper * spacing, value);
        }
        return maximumError;
    }

    private static void Refine(DistanceCell cell, Vector3 origin, float tolerance, int maxDepth,
        ref float lower, ref float upper)
    {
        Span<float> values = stackalloc float[8];
        int level = 0, x = 0, y = 0, z = 0;
        while (true)
        {
            float width = 1f / (1 << level);
            Vector3 fraction = new Vector3(x, y, z) * width;
            Vector3 minimum = origin + fraction;
            Vector3 maximum = minimum + new Vector3(width);
            float bound = Vector3.Max(Vector3.Max(minimum, -maximum), Vector3.Zero).Length();
            bool split = false;
            if (bound >= upper - tolerance)
                lower = MathF.Min(lower, bound);
            else
            {
                float low = float.PositiveInfinity, high = float.NegativeInfinity;
                for (int corner = 0; corner < 8; corner++)
                {
                    float value = cell.Sample(fraction + Corner(corner) * width);
                    values[corner] = value;
                    low = MathF.Min(low, value);
                    high = MathF.Max(high, value);
                }
                if (low <= 0f && high >= 0f)
                {
                    // A box bounds the nearest zero from below; its edge zeroes provide candidate upper bounds.
                    ReadOnlySpan<int> edges = [0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7];
                    for (int edge = 0; edge < edges.Length; edge += 2)
                    {
                        int first = edges[edge], second = edges[edge + 1];
                        float a = values[first], b = values[second];
                        Vector3 start = minimum + Corner(first) * width;
                        if (a == 0f) upper = MathF.Min(upper, start.Length());
                        if (b == 0f) upper = MathF.Min(upper, (minimum + Corner(second) * width).Length());
                        if (a < 0f && b > 0f || a > 0f && b < 0f)
                        {
                            Vector3 point = start + (Corner(second) - Corner(first)) * (width * a / (a - b));
                            upper = MathF.Min(upper, point.Length());
                        }
                    }
                    split = bound < upper - tolerance && level < maxDepth;
                    if (!split) lower = MathF.Min(lower, bound);
                }
            }
            if (split)
            {
                level++;
                x *= 2; y *= 2; z *= 2;
                continue;
            }
            while (level > 0 && ((x & 1) | (y & 1) << 1 | (z & 1) << 2) == 7)
            {
                x >>= 1; y >>= 1; z >>= 1;
                level--;
            }
            if (level == 0) break;
            int next = ((x & 1) | (y & 1) << 1 | (z & 1) << 2) + 1;
            x = (x & ~1) | (next & 1);
            y = (y & ~1) | (next >> 1 & 1);
            z = (z & ~1) | (next >> 2 & 1);
        }
    }

    private static Vector3 Corner(int index) => new(index & 1, index >> 1 & 1, index >> 2);

    private readonly record struct DistanceCell(int X, int Y, int Z,
        float Corner0, float Corner1, float Corner2, float Corner3, float Corner4, float Corner5, float Corner6, float Corner7)
    {
        internal bool HasZero => MathF.Min(MathF.Min(MathF.Min(Corner0, Corner1), MathF.Min(Corner2, Corner3)),
            MathF.Min(MathF.Min(Corner4, Corner5), MathF.Min(Corner6, Corner7))) <= 0f
            && MathF.Max(MathF.Max(MathF.Max(Corner0, Corner1), MathF.Max(Corner2, Corner3)),
                MathF.Max(MathF.Max(Corner4, Corner5), MathF.Max(Corner6, Corner7))) >= 0f;

        internal float Sample(Vector3 position) => float.Lerp(
            float.Lerp(float.Lerp(Corner0, Corner1, position.X), float.Lerp(Corner2, Corner3, position.X), position.Y),
            float.Lerp(float.Lerp(Corner4, Corner5, position.X), float.Lerp(Corner6, Corner7, position.X), position.Y), position.Z);
    }
}
