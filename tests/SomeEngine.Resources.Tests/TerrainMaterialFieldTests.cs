using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Resources.Tests;

public sealed class TerrainMaterialFieldTests
{
    [Fact]
    public async Task SurfaceReadsKeepDeclaredMaterialFieldsFromTheSameCapturedRevision()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var terrain = new Terrain(new TerrainEditing(scheduler));
        HeightField ground = terrain.CreateHeightField("ground", new HeightGrid(9, 9, 3, .5f), .5f);
        VoxelField shape = terrain.CreateVoxelField("shape", new VoxelGrid(3, .5f), 1.5f);
        HeightField paint = terrain.CreateHeightField("paint", new HeightGrid(5, 5, 2, 1f));
        VoxelField density = terrain.CreateVoxelField("density", new VoxelGrid(2, .25f), 0f);
        terrain.Surface = new BlendSurface(new HeightSurface(ground), new VoxelSurface(shape), .5f);
        var bounds = new TerrainBounds(new(.5f, -.5f, .5f), new(2.5f, 1.5f, 2.5f));
        var range = new VoxelRegion(0, -4, 0, 13, 13, 13);
        TerrainEdit paintEdit = paint.EditCpu(new HeightDeltaBrush(paint.Grid.Range, 2f));
        TerrainEdit densityEdit = density.EditCpu(new AddScalar(range, 3f));
        ulong revision = terrain.Revision;
        var fields = new TerrainFieldInputs([ground.Id, paint.Id], [shape.Id, density.Id]);
        var values = new FieldValues[1];
        ValueTask<TerrainSurfaceRead> read = terrain.Surface.ReadCpuAsync(bounds, fields);
        TerrainSurfaceJob work = terrain.Surface.ScheduleRead(bounds, fields,
            new ReadMaterialFields(paint.Id, density.Id, new(1.25f, .75f, 1.25f), values), 1);

        TerrainEdit nextPaint = paint.EditCpu(new HeightDeltaBrush(paint.Grid.Range, 3f));
        TerrainEdit nextDensity = density.EditCpu(new AddScalar(range, 4f));
        TerrainSurfaceRead current = await terrain.Surface.ReadCpuAsync(bounds, fields);
        TerrainSurfaceRead captured = await read;
        await work.WaitAsync();

        Assert.Equal(revision, captured.Revision);
        Assert.Equal(revision, work.Revision);
        Assert.Equal(.5f, captured.GetHeightField(ground.Id).Sample(new(1.25f, 1.25f)));
        Assert.Equal(1.5f, captured.GetVoxelField(shape.Id).Sample(new(1.25f, .75f, 1.25f)));
        Assert.Equal(2f, captured.GetHeightField(paint.Id).Sample(new(1.25f, 1.25f)));
        Assert.Equal(3f, captured.GetVoxelField(density.Id).Sample(new(1.25f, .75f, 1.25f)));
        Assert.Equal(.875f, captured.Sample(new(1.25f, .75f, 1.25f)));
        Assert.Equal(new HeightRegion(0, 0, 4, 4), captured.GetHeightField(paint.Id).Range);
        Assert.Equal(new VoxelRegion(2, -2, 2, 10, 10, 10), captured.GetVoxelField(density.Id).Range);
        Assert.Equal(new FieldValues(2f, 3f, paintEdit.Revision, densityEdit.Revision), values[0]);
        Assert.Equal(new TerrainFieldVersion[]
        {
            new(TerrainFieldKind.Height, ground.Id, ground.Revision),
            new(TerrainFieldKind.Voxel, shape.Id, shape.Revision),
            new(TerrainFieldKind.Height, paint.Id, paintEdit.Revision),
            new(TerrainFieldKind.Voxel, density.Id, densityEdit.Revision),
        }, captured.FieldVersions.ToArray());
        Assert.Equal(5f, current.GetHeightField(paint.Id).Sample(new(1.25f, 1.25f)));
        Assert.Equal(7f, current.GetVoxelField(density.Id).Sample(new(1.25f, .75f, 1.25f)));
        Assert.Equal(nextPaint.Revision, current.GetHeightField(paint.Id).Revision);
        Assert.Equal(nextDensity.Revision, current.GetVoxelField(density.Id).Revision);
    }

    private readonly record struct FieldValues(float Paint, float Density, ulong PaintRevision, ulong DensityRevision);

    private readonly record struct ReadMaterialFields(int PaintId, int DensityId, Vector3 Position, FieldValues[] Values)
        : ITerrainSurfaceWork
    {
        public void Execute(TerrainSurfaceRead surface, int index)
        {
            HeightFieldRead paint = surface.GetHeightField(PaintId);
            VoxelFieldRead density = surface.GetVoxelField(DensityId);
            Values[index] = new(paint.Sample(new(Position.X, Position.Z)), density.Sample(Position), paint.Revision, density.Revision);
        }
    }

    private readonly record struct AddScalar(VoxelRegion Range, float Delta) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z) => source[x, y, z] + Delta;
    }
}
