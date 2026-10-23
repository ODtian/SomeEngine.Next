using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Resources;

/// <summary>The zero isosurface of a trilinearly sampled three-dimensional field.</summary>
public sealed class VoxelSurface(VoxelField field) : TerrainSurface
{
    public VoxelField Field { get; } = field;
    public override Terrain Terrain => Field.Terrain;
    public override bool Uses(VoxelField field) => ReferenceEquals(Field, field);

    protected override CpuSurfaceData CaptureCpu(TerrainBounds bounds)
    {
        float spacing = Field.Grid.SampleSpacing;
        int minX = (int)MathF.Floor(bounds.Min.X / spacing);
        int minY = (int)MathF.Floor(bounds.Min.Y / spacing);
        int minZ = (int)MathF.Floor(bounds.Min.Z / spacing);
        int maxX = (int)MathF.Floor(bounds.Max.X / spacing) + 2;
        int maxY = (int)MathF.Floor(bounds.Max.Y / spacing) + 2;
        int maxZ = (int)MathF.Floor(bounds.Max.Z / spacing) + 2;
        VoxelFieldCpuRead read = Field.CaptureCpuRead(new VoxelRegion(minX, minY, minZ,
            maxX - minX, maxY - minY, maxZ - minZ));
        return new(new VoxelSurfaceRead(read.Samples, Terrain.Revision, bounds), read.Completion);
    }

    public override TerrainSurface Duplicate(Terrain terrain) => new VoxelSurface(terrain.VoxelFields[Field.Id]);

    private sealed class VoxelSurfaceRead(VoxelFieldRead voxels, ulong revision, TerrainBounds bounds)
        : TerrainSurfaceRead(revision, bounds, new[] { voxels.Version })
    {
        public override float Sample(Vector3 position) => voxels.Sample(position);
        public override JobResourceAccess[] GetReadAccesses(Scheduler scheduler) => voxels.GetReadAccesses(scheduler);
    }
}
