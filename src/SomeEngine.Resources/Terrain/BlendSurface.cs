using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Resources;

/// <summary>Combines two sources from the same Terrain as (1 - weight) * first + weight * second.</summary>
public sealed class BlendSurface(TerrainSurface first, TerrainSurface second, float weight) : TerrainSurface
{
    /// <summary>Fades the second source to the first across the inner boundary of a local region.</summary>
    public BlendSurface(TerrainSurface first, TerrainSurface second, float weight, TerrainBounds region, float fadeDistance)
        : this(first, second, weight)
    {
        Region = region;
        FadeDistance = fadeDistance;
    }

    public TerrainSurface First { get; } = first;
    public TerrainSurface Second { get; } = second;
    public float Weight { get; } = weight;
    public TerrainBounds? Region { get; }
    public float FadeDistance { get; }
    public override Terrain Terrain => First.Terrain;

    public override bool Uses(HeightField field) => First.Uses(field) || Second.Uses(field);
    public override bool Uses(VoxelField field) => First.Uses(field) || Second.Uses(field);

    protected override CpuSurfaceData CaptureCpu(TerrainBounds bounds)
    {
        CpuSurfaceData first = First.CaptureCpuRead(bounds);
        CpuSurfaceData second = Second.CaptureCpuRead(bounds);
        JobHandle completion = Terrain.Editing.Scheduler.CombineDependencies([first.Completion, second.Completion]);
        return new(new BlendSurfaceRead(first.Samples, second.Samples, Weight, Region, FadeDistance, Terrain.Revision, bounds), completion);
    }

    public override TerrainSurface Duplicate(Terrain terrain) => Region is { } region
        ? new BlendSurface(First.Duplicate(terrain), Second.Duplicate(terrain), Weight, region, FadeDistance)
        : new BlendSurface(First.Duplicate(terrain), Second.Duplicate(terrain), Weight);

    private sealed class BlendSurfaceRead(TerrainSurfaceRead first, TerrainSurfaceRead second,
        float weight, TerrainBounds? region, float fadeDistance, ulong revision, TerrainBounds bounds)
        : TerrainSurfaceRead(revision, bounds,
            first.FieldVersions.ToArray().Concat(second.FieldVersions.ToArray()).Distinct().ToArray())
    {
        public override float Sample(Vector3 position)
        {
            float blend = weight;
            if (region is { } bounds)
            {
                Vector3 distances = Vector3.Min(position - bounds.Min, bounds.Max - position);
                float distance = MathF.Min(distances.X, MathF.Min(distances.Y, distances.Z));
                float fraction = Math.Clamp(distance / fadeDistance, 0f, 1f);
                blend *= fraction * fraction * (3f - 2f * fraction);
            }
            return float.Lerp(first.Sample(position), second.Sample(position), blend);
        }

        public override JobResourceAccess[] GetReadAccesses(Scheduler scheduler) =>
            [.. first.GetReadAccesses(scheduler), .. second.GetReadAccesses(scheduler)];
    }
}
