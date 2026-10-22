using SomeEngine.Job;

namespace SomeEngine.Resources.Tests;

public sealed class HeightFieldTests
{
    [Fact]
    public async Task CpuDeltaChangesTheRequestedSamplesAfterCompletion()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var terrain = new Terrain(new TerrainEditing(scheduler));
        HeightField height = terrain.CreateHeightField("ground", new HeightGrid(8, 6, 4), 2f);

        TerrainEdit edit = height.EditCpu(new HeightDeltaBrush(new HeightRegion(2, 1, 4, 3), 3f));
        await edit.WaitAsync();
        HeightFieldRead data = await height.ReadCpuAsync(height.Grid.Range);

        Assert.Equal(5f, data[2, 1]);
        Assert.Equal(5f, data[5, 3]);
        Assert.Equal(2f, data[1, 1]);
        Assert.Equal(edit.Revision, data.Revision);
        Assert.True(edit.Completion.IsCompleted);
    }

    [Fact]
    public async Task SharedTerrainReadsFollowEditsWhileDuplicateAndEarlierReadKeepTheirData()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var terrain = new Terrain(new TerrainEditing(scheduler));
        HeightField height = terrain.CreateHeightField("ground", new HeightGrid(8, 6, 4), 2f);
        await height.EditCpu(new HeightDeltaBrush(height.Grid.Range, 3f)).WaitAsync();
        HeightFieldRead earlier = await height.ReadCpuAsync(height.Grid.Range);
        Terrain shared = terrain;
        Terrain branch = terrain.Duplicate();

        TerrainEdit originalEdit = height.EditCpu(new HeightDeltaBrush(new HeightRegion(1, 1, 2, 2), 2f));
        TerrainEdit branchEdit = branch.HeightFields[0].EditCpu(new HeightDeltaBrush(branch.HeightFields[0].Grid.Range, 10f));
        await originalEdit.WaitAsync();
        await branchEdit.WaitAsync();
        HeightFieldRead sharedData = await shared.HeightFields[0].ReadCpuAsync(height.Grid.Range);
        HeightFieldRead branchData = await branch.HeightFields[0].ReadCpuAsync(height.Grid.Range);

        Assert.Equal(7f, sharedData[1, 1]);
        Assert.Equal(15f, branchData[1, 1]);
        Assert.Equal(5f, earlier[1, 1]);
        Assert.Equal(5f, sharedData[5, 1]);
        Assert.Equal(15f, branchData[5, 1]);
    }

    [Fact]
    public async Task SubmittedEditsUsePreviousValuesAndReadsCaptureTheirCallVersion()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var terrain = new Terrain(new TerrainEditing(scheduler));
        HeightField height = terrain.CreateHeightField("ground", new HeightGrid(8, 6, 4), 2f);
        var signal = new EditSignal();
        JobHandle start = scheduler.CreateExternalFenceHandle(signal);

        TerrainEdit first = height.EditCpu(new HeightDeltaBrush(height.Grid.Range, 3f), start);
        ValueTask<HeightFieldRead> capturedRead = height.ReadCpuAsync(height.Grid.Range);
        TerrainEdit second = height.EditCpu(new ScaleHeightBrush(height.Grid.Range, 4f));
        signal.Complete();
        await second.WaitAsync();
        HeightFieldRead captured = await capturedRead;
        HeightFieldRead current = await height.ReadCpuAsync(height.Grid.Range);

        Assert.Equal(5f, captured[3, 2]);
        Assert.Equal(20f, current[3, 2]);
        Assert.Equal(first.Revision, captured.Revision);
        Assert.Equal(second.Revision, current.Revision);
        Assert.True(first.Completion.IsCompleted);
    }

    [Fact]
    public async Task NeighborBrushReadsOneInputVersionAcrossPageBoundaries()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var terrain = new Terrain(new TerrainEditing(scheduler));
        HeightField height = terrain.CreateHeightField("ground", new HeightGrid(8, 3, 4));
        height.EditCpu(new HeightDeltaBrush(new HeightRegion(3, 1, 1, 1), 10f));

        await height.EditCpu(new NeighborSumBrush(new HeightRegion(2, 1, 4, 1))).WaitAsync();
        HeightFieldRead data = await height.ReadCpuAsync(height.Grid.Range);

        Assert.Equal(10f, data[2, 1]);
        Assert.Equal(0f, data[3, 1]);
        Assert.Equal(10f, data[4, 1]);
        Assert.Equal(0f, data[5, 1]);
    }

    private readonly record struct ScaleHeightBrush(HeightRegion Range, float Scale) : IHeightBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(HeightFieldRead source, int x, int z) => source[x, z] * Scale;
    }

    private readonly record struct NeighborSumBrush(HeightRegion Range) : IHeightBrushCpu
    {
        public int ReadBorder => 1;
        public float Apply(HeightFieldRead source, int x, int z) => source[x - 1, z] + source[x + 1, z];
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
