using SomeEngine.Job;

namespace SomeEngine.Resources;

/// <summary>Metre tolerances for sample distances and grid-edge zero crossings.</summary>
public readonly record struct VoxelDistancePrecision(float DistanceTolerance, float SurfaceTolerance)
{
    internal void Check(float distanceError, float surfaceShift)
    {
        if (!float.IsFinite(distanceError) || !float.IsFinite(surfaceShift)
            || distanceError > DistanceTolerance || surfaceShift > SurfaceTolerance)
            throw new VoxelDistancePrecisionException(distanceError, surfaceShift, this);
    }
}

/// <summary>The written samples and the source neighborhood searched for their nearest zero surface.</summary>
public readonly record struct VoxelDistanceSettings(VoxelRegion Range, int ReadBorder, VoxelDistancePrecision Precision)
{
    public VoxelRegion ReadRegion => Range.Expand(ReadBorder);
}

/// <summary>Reports the measured errors when a reconstruction exceeds its requested precision.</summary>
public sealed class VoxelDistancePrecisionException(float distanceError, float surfaceShift, VoxelDistancePrecision precision)
    : Exception($"Terrain distance error {distanceError} m and zero-surface shift {surfaceShift} m "
        + $"exceed the requested {precision.DistanceTolerance} m and {precision.SurfaceTolerance} m.")
{
    public float DistanceError { get; } = distanceError;
    public float SurfaceShift { get; } = surfaceShift;
    public VoxelDistancePrecision Precision { get; } = precision;
}

/// <summary>Writes X-fastest signed distances for Settings.Range and returns their maximum distance error bound.</summary>
public interface IVoxelDistanceCpu
{
    float Rebuild(VoxelFieldRead source, VoxelDistanceSettings settings, Span<float> distances);
}

public static class VoxelDistance
{
    public static TerrainEdit RebuildDistanceCpu(this VoxelField field, VoxelDistanceSettings settings,
        JobHandle dependency = default) => field.RebuildDistanceCpu(settings, new SubdividedVoxelDistance(), dependency);

    /// <summary>Captures the source, rebuilds one region, checks its precision, then completes the new field version.</summary>
    public static TerrainEdit RebuildDistanceCpu<TDistance>(this VoxelField field, VoxelDistanceSettings settings,
        TDistance algorithm, JobHandle dependency = default) where TDistance : struct, IVoxelDistanceCpu
    {
        Terrain terrain = field.Terrain;
        TerrainEdit edit;
        lock (terrain.Gate)
        {
            VoxelFieldCpuRead source = field.CaptureCpuRead(settings.ReadRegion);
            Scheduler scheduler = terrain.Editing.Scheduler;
            VoxelRegion range = settings.Range;
            var distances = new float[range.Width * range.Height * range.Depth];
            JobHandle input = scheduler.CombineDependencies([source.Completion, dependency]);
            JobHandle calculation = scheduler.Schedule(new DistanceWork<TDistance>(source.Samples, settings, algorithm, distances),
                [.. source.Accesses, JobResourceAccess.Write(scheduler, distances)], input);
            edit = terrain.Editing.EditCpu(field, new DistanceValues(range, distances), calculation,
                [JobResourceAccess.Read(scheduler, distances)]);
        }
        terrain.NotifyChange(new VoxelFieldChange(field, settings.Range, settings.ReadBorder, edit));
        return edit;
    }

    private readonly struct DistanceWork<TDistance>(VoxelFieldRead source, VoxelDistanceSettings settings,
        TDistance algorithm, float[] distances) : IJob where TDistance : struct, IVoxelDistanceCpu
    {
        public void Execute()
        {
            float error = algorithm.Rebuild(source, settings, distances);
            float shift = GetSurfaceShift(source, settings.Range, distances);
            settings.Precision.Check(error, shift);
        }
    }

    private readonly record struct DistanceValues(VoxelRegion Range, float[] Values) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z) =>
            Values[((z - Range.MinZ) * Range.Height + y - Range.MinY) * Range.Width + x - Range.MinX];
    }

    internal static float GetSurfaceShift(VoxelFieldRead source, VoxelRegion range, ReadOnlySpan<float> distances)
    {
        float maximum = 0f;
        for (int z = range.MinZ; z < range.MaxZ; z++)
        for (int y = range.MinY; y < range.MaxY; y++)
        for (int x = range.MinX; x < range.MaxX; x++)
        {
            int index = ((z - range.MinZ) * range.Height + y - range.MinY) * range.Width + x - range.MinX;
            float previous = source[x, y, z], current = distances[index];
            if (!float.IsFinite(current) || MathF.Sign(previous) != MathF.Sign(current)) return float.PositiveInfinity;
            if (x + 1 < range.MaxX)
                maximum = MathF.Max(maximum, GetCrossingShift(previous, source[x + 1, y, z], current, distances[index + 1]));
            if (y + 1 < range.MaxY)
                maximum = MathF.Max(maximum, GetCrossingShift(previous, source[x, y + 1, z], current, distances[index + range.Width]));
            if (z + 1 < range.MaxZ)
                maximum = MathF.Max(maximum, GetCrossingShift(previous, source[x, y, z + 1], current,
                    distances[index + range.Width * range.Height]));
        }
        return maximum * source.Grid.SampleSpacing;
    }

    private static float GetCrossingShift(float first, float second, float resultFirst, float resultSecond) =>
        (first < 0f && second > 0f || first > 0f && second < 0f)
            ? MathF.Abs(first / (first - second) - resultFirst / (resultFirst - resultSecond)) : 0f;
}
