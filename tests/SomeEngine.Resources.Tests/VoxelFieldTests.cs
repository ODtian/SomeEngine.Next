using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Resources.Tests;

public sealed class VoxelFieldTests
{
    [Fact]
    public async Task SphereDeltaChangesSparseSamplesWithDistanceFalloff()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var terrain = new Terrain(new TerrainEditing(scheduler));
        VoxelField field = terrain.CreateVoxelField("cave", new VoxelGrid(4), 2f);

        TerrainEdit edit = field.EditCpu(new VoxelSphereDeltaBrush(field.Grid, new Vector3(-1, 0, 0), 2f, -4f));
        await edit.WaitAsync();
        VoxelFieldRead data = await field.ReadCpuAsync(new VoxelRegion(-4, -3, -3, 20, 8, 8));

        Assert.Equal(-2f, data[-1, 0, 0]);
        Assert.Equal(0f, data[0, 0, 0]);
        Assert.Equal(2f, data[1, 0, 0]);
        Assert.Equal(2f, data[12, 4, -3]);
        Assert.Equal(edit.Revision, data.Revision);
    }

    [Fact]
    public async Task TrilinearSamplingUsesFieldSpacingAndGlobalCoordinates()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var terrain = new Terrain(new TerrainEditing(scheduler));
        VoxelField field = terrain.CreateVoxelField("corners", new VoxelGrid(4, 2f));
        var range = new VoxelRegion(-1, 0, 0, 2, 2, 2);
        await field.EditCpu(new CornerValuesBrush(range)).WaitAsync();

        VoxelFieldRead data = await field.ReadCpuAsync(range);

        Assert.Equal(8f, data.Sample(new Vector3(-1f, 1f, 1f)));
        Assert.Equal(8.5f, data.Sample(new Vector3(-1.5f, 0.5f, 1.5f)));
    }

    [Fact]
    public async Task ReadsAndDuplicatesKeepTheirVersionWhileLaterEditsUsePreviousValues()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var terrain = new Terrain(new TerrainEditing(scheduler));
        VoxelField field = terrain.CreateVoxelField("cave", new VoxelGrid(4), 2f);
        var brush = new VoxelSphereDeltaBrush(field.Grid, new Vector3(-1, 0, 0), 2f, -4f);
        var signal = new EditSignal();
        JobHandle start = scheduler.CreateExternalFenceHandle(signal);

        TerrainEdit first = field.EditCpu(brush, start);
        ValueTask<VoxelFieldRead> earlierRead = field.ReadCpuAsync(brush.Range);
        Terrain branch = terrain.Duplicate();
        TerrainEdit originalEdit = field.EditCpu(new ScaleVoxelBrush(brush.Range, 2f));
        signal.Complete();
        TerrainEdit branchEdit = branch.VoxelFields[0].EditCpu(
            new VoxelSphereDeltaBrush(field.Grid, new Vector3(-1, 0, 0), 2f, 3f));
        await originalEdit.WaitAsync();
        await branchEdit.WaitAsync();
        VoxelFieldRead earlier = await earlierRead;
        VoxelFieldRead current = await field.ReadCpuAsync(brush.Range);
        VoxelFieldRead branchData = await branch.VoxelFields[0].ReadCpuAsync(brush.Range);

        Assert.Equal(-2f, earlier[-1, 0, 0]);
        Assert.Equal(-4f, current[-1, 0, 0]);
        Assert.Equal(1f, branchData[-1, 0, 0]);
        Assert.Equal(first.Revision, earlier.Revision);
        Assert.Equal(originalEdit.Revision, current.Revision);
    }

    private readonly record struct CornerValuesBrush(VoxelRegion Range) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z) => (x, y, z) switch
        {
            (-1, 0, 0) => 1f,
            (0, 0, 0) => 3f,
            (-1, 1, 0) => 5f,
            (0, 1, 0) => 7f,
            (-1, 0, 1) => 9f,
            (0, 0, 1) => 11f,
            (-1, 1, 1) => 13f,
            _ => 15f,
        };
    }

    private readonly record struct ScaleVoxelBrush(VoxelRegion Range, float Scale) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z) => source[x, y, z] * Scale;
    }

    private sealed class EditSignal : IJobExternalFence
    {
        private readonly TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsSignaled => _signal.Task.IsCompleted;
        public void OnSignaled(Action<object?> callback, object? state)
        {
            if (IsSignaled)
                callback(state);
            else
                _signal.Task.GetAwaiter().OnCompleted(() => callback(state));
        }
        public void Complete() => _signal.SetResult();
    }
}
