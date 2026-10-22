using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

/// <summary>Recast-style triangle clipping and solid-span merging, partitioned by spatial tile.</summary>
public static class NavigationSurfaceBuilder
{
    public static NavigationBuild<NavigationSurfaceBuildState> Schedule(
        NavigationBuildInput input, JobHandle dependency = default)
    {
        float tileWidth = input.Settings.TileSize * input.Settings.CellSize;
        int width = (int)MathF.Ceiling((input.Region.Maximum.X - input.Region.Minimum.X) / tileWidth);
        int depth = (int)MathF.Ceiling((input.Region.Maximum.Z - input.Region.Minimum.Z) / tileWidth);
        var state = new NavigationSurfaceBuildState(input, width * depth);
        var handles = new JobHandle[state.Tiles.Length];
        var reads = new List<JobResourceAccess> { JobResourceAccess.Read(input.Sources) };
        foreach (NavigationGeometrySource source in input.Sources.Span)
        {
            reads.Add(JobResourceAccess.Read(source.Geometry.Positions));
            reads.Add(JobResourceAccess.Read(source.Geometry.Triangles));
        }
        var accesses = new JobResourceAccess[reads.Count + 2];
        reads.CopyTo(accesses);
        for (int index = 0; index < handles.Length; index++)
        {
            accesses[^2] = JobResourceAccess.Write(state.Tiles, index, 1);
            accesses[^1] = JobResourceAccess.Write(state.Columns, index, 1);
            handles[index] = JobSystem.Schedule(new BuildTile(state, index, width), accesses, dependency);
        }
        JobHandle complete = JobSystem.Schedule(new CreateSurface(state),
            JobResourceAccess.Read(state.Tiles), JobSystem.CombineDependencies(handles));
        return new(state, complete);
    }

    private readonly struct CreateSurface(NavigationSurfaceBuildState state) : IJob
    {
        public void Execute() => state.Surface = new(state.Input.Region, state.Input.Settings, state.Tiles);
    }

    private readonly struct BuildTile(NavigationSurfaceBuildState state, int index, int tileColumns) : IJob
    {
        public void Execute()
        {
            NavigationBuildInput input = state.Input;
            NavigationSurfaceSettings settings = input.Settings;
            var coordinate = new NavigationTileCoordinate(index % tileColumns, index / tileColumns);
            float tileWidth = settings.TileSize * settings.CellSize;
            Vector3 minimum = input.Region.Minimum + new Vector3(coordinate.X * tileWidth, 0, coordinate.Z * tileWidth);
            Vector3 maximum = Vector3.Min(minimum + new Vector3(tileWidth,
                input.Region.Maximum.Y - input.Region.Minimum.Y, tileWidth), input.Region.Maximum);
            int border = settings.BorderCells;
            int width = (int)MathF.Ceiling((maximum.X - minimum.X) / settings.CellSize) + 2 * border;
            int depth = (int)MathF.Ceiling((maximum.Z - minimum.Z) / settings.CellSize) + 2 * border;
            Vector2 origin = new(minimum.X - border * settings.CellSize, minimum.Z - border * settings.CellSize);
            var columns = new List<NavigationSurfaceSpan>?[width * depth];
            state.Columns[index] = columns;
            foreach (NavigationGeometrySource source in input.Sources.Span)
            {
                ReadOnlySpan<Vector3> positions = source.Geometry.Positions.Span;
                bool mirrored = source.Transform.GetDeterminant() < 0;
                foreach (NavigationTriangle triangle in source.Geometry.Triangles.Span)
                {
                    Vector3 a = Vector3.Transform(positions[triangle.A], source.Transform);
                    Vector3 b = Vector3.Transform(positions[triangle.B], source.Transform);
                    Vector3 c = Vector3.Transform(positions[triangle.C], source.Transform);
                    if (mirrored) (b, c) = (c, b);
                    Rasterize(columns, width, depth, origin, settings, input.Region, a, b, c, triangle.Area);
                }
            }
            var descriptions = new NavigationSurfaceColumn[columns.Length];
            var spans = new List<NavigationSurfaceSpan>();
            for (int columnIndex = 0; columnIndex < columns.Length; columnIndex++)
            {
                int start = spans.Count;
                if (columns[columnIndex] is { } values)
                {
                    values.Sort(static (left, right) => left.MinimumHeight.CompareTo(right.MinimumHeight));
                    foreach (NavigationSurfaceSpan value in values)
                    {
                        if (spans.Count == start || value.MinimumHeight > spans[^1].MaximumHeight)
                            spans.Add(value);
                        else
                        {
                            NavigationSurfaceSpan previous = spans[^1];
                            NavigationSurfaceSpan top = value.Height > previous.Height ? value : previous;
                            spans[^1] = top with
                            {
                                MinimumHeight = previous.MinimumHeight,
                                MaximumHeight = MathF.Max(previous.MaximumHeight, value.MaximumHeight),
                            };
                        }
                    }
                }
                descriptions[columnIndex] = new(start, spans.Count - start);
            }
            state.Tiles[index] = new(coordinate, new(minimum, maximum), origin, settings.CellSize,
                width, depth, descriptions, spans.ToArray());
            state.Columns[index] = [];
        }
    }

    private static void Rasterize(List<NavigationSurfaceSpan>?[] columns, int width, int depth,
        Vector2 origin, NavigationSurfaceSettings settings, NavigationBounds region,
        Vector3 a, Vector3 b, Vector3 c, int area)
    {
        Vector3 lower = Vector3.Min(a, Vector3.Min(b, c));
        Vector3 upper = Vector3.Max(a, Vector3.Max(b, c));
        if (upper.Y < region.Minimum.Y || lower.Y > region.Maximum.Y) return;
        int minX = Math.Max(0, (int)MathF.Floor((lower.X - origin.X) / settings.CellSize));
        int maxX = Math.Min(width - 1, (int)MathF.Floor((upper.X - origin.X) / settings.CellSize));
        int minZ = Math.Max(0, (int)MathF.Floor((lower.Z - origin.Y) / settings.CellSize));
        int maxZ = Math.Min(depth - 1, (int)MathF.Floor((upper.Z - origin.Y) / settings.CellSize));
        Vector3 cross = Vector3.Cross(b - a, c - a);
        Vector3 normal = cross.LengthSquared() > 0 ? Vector3.Normalize(cross) : Vector3.Zero;
        Span<Vector3> first = stackalloc Vector3[12];
        Span<Vector3> second = stackalloc Vector3[12];
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            first[0] = a; first[1] = b; first[2] = c;
            float left = origin.X + x * settings.CellSize;
            float back = origin.Y + z * settings.CellSize;
            int count = Clip(first[..3], second, 0, left, true);
            count = Clip(second[..count], first, 0, left + settings.CellSize, false);
            count = Clip(first[..count], second, 2, back, true);
            count = Clip(second[..count], first, 2, back + settings.CellSize, false);
            if (count < 3) continue;
            float low = first[0].Y, high = low;
            for (int vertex = 1; vertex < count; vertex++)
            {
                low = MathF.Min(low, first[vertex].Y);
                high = MathF.Max(high, first[vertex].Y);
            }
            if (high < region.Minimum.Y || low > region.Maximum.Y) continue;
            low = MathF.Max(region.Minimum.Y, low);
            high = MathF.Min(region.Maximum.Y, high);
            float floor = MathF.Floor((low - region.Minimum.Y) / settings.CellHeight) * settings.CellHeight + region.Minimum.Y;
            float ceiling = MathF.Ceiling((high - region.Minimum.Y) / settings.CellHeight) * settings.CellHeight + region.Minimum.Y;
            ceiling = MathF.Max(floor + settings.CellHeight, ceiling);
            (columns[z * width + x] ??= []).Add(new(floor, ceiling, high, normal, area));
        }
    }

    private static int Clip(ReadOnlySpan<Vector3> input, Span<Vector3> output, int axis, float boundary, bool greater)
    {
        if (input.IsEmpty) return 0;
        int count = 0;
        Vector3 previous = input[^1];
        float previousDistance = (previous[axis] - boundary) * (greater ? 1 : -1);
        foreach (Vector3 current in input)
        {
            float currentDistance = (current[axis] - boundary) * (greater ? 1 : -1);
            if ((currentDistance >= 0) != (previousDistance >= 0))
                output[count++] = Vector3.Lerp(previous, current, previousDistance / (previousDistance - currentDistance));
            if (currentDistance >= 0) output[count++] = current;
            previous = current;
            previousDistance = currentDistance;
        }
        return count;
    }
}
