using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Resources;

/// <summary>A terrain-local volume in metres.</summary>
public readonly record struct TerrainBounds(Vector3 Min, Vector3 Max)
{
    public TerrainBounds Expand(float distance) => new(Min - new Vector3(distance), Max + new Vector3(distance));
    public TerrainBounds Union(TerrainBounds other) => new(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));
}

public readonly record struct TerrainHeightRange(float Min, float Max);

/// <summary>A scalar surface source belonging to a Terrain.</summary>
public abstract class TerrainSurface
{
    public abstract Terrain Terrain { get; }
    public abstract TerrainSurface Duplicate(Terrain terrain);
    public virtual bool Uses(HeightField field) => false;
    public virtual bool Uses(VoxelField field) => false;

    public ValueTask<TerrainSurfaceRead> ReadCpuAsync(TerrainBounds bounds)
    {
        lock (Terrain.Gate) return CompleteReadAsync(CaptureCpu(bounds));
    }

    /// <summary>Captures one version and runs parallel work after its samples are ready.</summary>
    public TerrainSurfaceJob ScheduleRead<TWork>(TerrainBounds bounds, in TWork work, int length, int batchSize = 1)
        where TWork : struct, ITerrainSurfaceWork
    {
        lock (Terrain.Gate)
        {
            CpuSurfaceData data = CaptureCpu(bounds);
            Scheduler scheduler = Terrain.Editing.Scheduler;
            JobHandle completion = scheduler.ScheduleParallel(new SurfaceReadWork<TWork>(data.Samples, work),
                length, batchSize, data.Samples.GetReadAccesses(scheduler), data.Completion);
            return new(data.Samples.Revision, completion);
        }
    }

    protected abstract CpuSurfaceData CaptureCpu(TerrainBounds bounds);
    protected readonly record struct CpuSurfaceData(TerrainSurfaceRead Samples, JobHandle Completion);

    private static async ValueTask<TerrainSurfaceRead> CompleteReadAsync(CpuSurfaceData data)
    {
        await data.Completion.WaitAsync().ConfigureAwait(false);
        return data.Samples;
    }

    private readonly struct SurfaceReadWork<TWork>(TerrainSurfaceRead samples, TWork work) : IJobParallelFor
        where TWork : struct, ITerrainSurfaceWork
    {
        public void Execute(int index) => work.Execute(samples, index);
    }
}

public interface ITerrainSurfaceWork
{
    void Execute(TerrainSurfaceRead surface, int index);
}

public readonly record struct TerrainSurfaceJob(ulong Revision, JobHandle Completion)
{
    public ValueTask WaitAsync() => Completion.WaitAsync();
}

/// <summary>Ready, fixed-version scalar data. Negative values are solid and zero is the surface.</summary>
public abstract class TerrainSurfaceRead(ulong revision)
{
    public ulong Revision { get; } = revision;
    public abstract float Sample(Vector3 position);
    public virtual TerrainHeightRange? GetHeightRange(TerrainBounds bounds) => null;
    public virtual JobResourceAccess[] GetReadAccesses(Scheduler scheduler) => [];

    public Vector3 GetNormal(Vector3 position, float distance)
    {
        Vector3 x = new(distance, 0, 0), y = new(0, distance, 0), z = new(0, 0, distance);
        return Vector3.Normalize(new Vector3(
            Sample(position + x) - Sample(position - x),
            Sample(position + y) - Sample(position - y),
            Sample(position + z) - Sample(position - z)));
    }
}

/// <summary>The height surface F(x,y,z) = y - H(x,z).</summary>
public sealed class HeightSurface(HeightField field) : TerrainSurface
{
    public HeightField Field { get; } = field;
    public override Terrain Terrain => Field.Terrain;
    public override bool Uses(HeightField field) => ReferenceEquals(Field, field);

    protected override CpuSurfaceData CaptureCpu(TerrainBounds bounds)
    {
        float spacing = Field.Grid.SampleSpacing;
        int minX = (int)MathF.Floor(bounds.Min.X / spacing);
        int minZ = (int)MathF.Floor(bounds.Min.Z / spacing);
        int maxX = (int)MathF.Ceiling(bounds.Max.X / spacing) + 1;
        int maxZ = (int)MathF.Ceiling(bounds.Max.Z / spacing) + 1;
        HeightFieldData data = Field.Data;
        HeightRegion range = new HeightRegion(minX, minZ, maxX - minX, maxZ - minZ).Intersect(Field.Grid.Range);
        var read = new HeightFieldRead(Field.Grid, Field.InitialHeight, data, range);
        return new(new HeightSurfaceRead(read, Terrain.Revision), data.Edit.Completion);
    }

    public override TerrainSurface Duplicate(Terrain terrain) => new HeightSurface(terrain.HeightFields[Field.Id]);

    private sealed class HeightSurfaceRead(HeightFieldRead heights, ulong revision) : TerrainSurfaceRead(revision)
    {
        public override float Sample(Vector3 position) => position.Y - heights.Sample(new(position.X, position.Z));
        public override JobResourceAccess[] GetReadAccesses(Scheduler scheduler) => heights.GetReadAccesses(scheduler);

        public override TerrainHeightRange? GetHeightRange(TerrainBounds bounds)
        {
            HeightGrid grid = heights.Grid;
            int minX = Math.Clamp((int)MathF.Floor(bounds.Min.X / grid.SampleSpacing), 0, grid.Width - 1);
            int minZ = Math.Clamp((int)MathF.Floor(bounds.Min.Z / grid.SampleSpacing), 0, grid.Depth - 1);
            int maxX = Math.Clamp((int)MathF.Ceiling(bounds.Max.X / grid.SampleSpacing), 0, grid.Width - 1);
            int maxZ = Math.Clamp((int)MathF.Ceiling(bounds.Max.Z / grid.SampleSpacing), 0, grid.Depth - 1);
            float min = float.MaxValue, max = float.MinValue;
            for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
            {
                float value = heights[x, z];
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }
            return new(min, max);
        }
    }
}
