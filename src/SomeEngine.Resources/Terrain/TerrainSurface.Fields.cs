using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Job;

namespace SomeEngine.Resources;

public abstract partial class TerrainSurface
{
    private CpuSurfaceData CaptureCpu(TerrainBounds bounds, TerrainFieldInputs fields)
    {
        CpuSurfaceData source = CaptureCpu(bounds);
        if (fields.HeightFieldIds.IsDefaultOrEmpty && fields.VoxelFieldIds.IsDefaultOrEmpty) return source;
        var heights = new Dictionary<int, HeightFieldRead>();
        var voxels = new Dictionary<int, VoxelFieldRead>();
        var dependencies = new List<JobHandle> { source.Completion };
        foreach (int id in fields.HeightFieldIds.AsSpan())
        {
            if (source.Samples.ContainsField(TerrainFieldKind.Height, id) || heights.ContainsKey(id)) continue;
            HeightFieldCpuRead read = Terrain.HeightFields[id].CaptureCpuRead(bounds);
            heights.Add(id, read.Samples);
            dependencies.Add(read.Completion);
        }
        foreach (int id in fields.VoxelFieldIds.AsSpan())
        {
            if (source.Samples.ContainsField(TerrainFieldKind.Voxel, id) || voxels.ContainsKey(id)) continue;
            VoxelFieldCpuRead read = Terrain.VoxelFields[id].CaptureCpuRead(bounds);
            voxels.Add(id, read.Samples);
            dependencies.Add(read.Completion);
        }
        return new(new AdditionalFieldSurfaceRead(source.Samples, heights, voxels),
            Terrain.Editing.Scheduler.CombineDependencies(CollectionsMarshal.AsSpan(dependencies)));
    }

    private sealed class AdditionalFieldSurfaceRead(TerrainSurfaceRead source,
        Dictionary<int, HeightFieldRead> heights, Dictionary<int, VoxelFieldRead> voxels)
        : TerrainSurfaceRead(source.Revision, source.Bounds, CombineVersions(source, heights, voxels))
    {
        public override float Sample(Vector3 position) => source.Sample(position);
        public override TerrainHeightRange? GetHeightRange(TerrainBounds bounds) => source.GetHeightRange(bounds);
        public override HeightFieldRead GetHeightField(int id) => heights.TryGetValue(id, out HeightFieldRead? read)
            ? read : source.GetHeightField(id);
        public override VoxelFieldRead GetVoxelField(int id) => voxels.TryGetValue(id, out VoxelFieldRead? read)
            ? read : source.GetVoxelField(id);

        public override JobResourceAccess[] GetReadAccesses(Scheduler scheduler)
        {
            var accesses = new List<JobResourceAccess>(source.GetReadAccesses(scheduler));
            foreach (HeightFieldRead read in heights.Values) accesses.AddRange(read.GetReadAccesses(scheduler));
            foreach (VoxelFieldRead read in voxels.Values) accesses.AddRange(read.GetReadAccesses(scheduler));
            return accesses.ToArray();
        }

        private static TerrainFieldVersion[] CombineVersions(TerrainSurfaceRead source,
            Dictionary<int, HeightFieldRead> heights, Dictionary<int, VoxelFieldRead> voxels) =>
            [.. source.FieldVersions.Span, .. heights.Values.Select(static read => read.Version),
                .. voxels.Values.Select(static read => read.Version)];
    }
}
