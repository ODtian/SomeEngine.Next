using SomeEngine.Assets.Schema;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets.Consumer.Tests;

public sealed class TerrainRegionTests
{
    [Fact]
    public async Task FilePagesCanBeReadAgainAfterTheirSourceRecovers()
    {
        string path = Path.Combine(Path.GetTempPath(), $"SomeEngine-Terrain-Retry-{Guid.NewGuid():N}.terrain.asset");
        try
        {
            using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
            var editing = new TerrainEditing(scheduler);
            using var initial = new Terrain(editing);
            HeightField heights = initial.CreateHeightField("ground", new HeightGrid(4, 4, 4));
            TerrainEdit edit = heights.EditCpu(new HeightDeltaBrush(heights.Grid.Range, 2f));
            TerrainCpuData data = await initial.CaptureCpuAsync();
            AssetEntry entry;
            using (var asset = new TerrainAsset())
            {
                await asset.UpdateAsync(data, Array.Empty<AssetId<MaterialAsset>>());
                entry = await asset.SaveAsync(path);
            }

            var reads = new FileReads();
            using (var description = await TerrainAsset.OpenAsync(path))
            await using (var file = await AssetFile.OpenAsync(path, TerrainAsset.WireType))
            {
                HeightFieldsBlock fields = await description.GetBlockAsync<HeightFieldsBlock>();
                AssetBlockDescription block = description.BlockDescriptions.Single(block => block.BlockTypeId == HeightFieldsBlock.BlockTypeId);
                TerrainHeightPage page = fields.Pages.Single();
                BinaryChunkEntry chunk = await file.DescribeChunkAsync(block.GetStorageKey(page.ChunkKey));
                reads.Ranges.Add((chunk.Offset, chunk.Offset + chunk.StoredLength, page.X));
            }
            await using var loader = new AssetLoader(new TerrainStorage(entry, path, reads));
            using var opened = await TerrainAsset.OpenAsync(loader, new(entry.AssetGuid));
            using Terrain terrain = await opened.CreateTerrainAsync(editing);
            using Terrain branch = terrain.Duplicate();
            var range = new HeightRegion(1, 1, 1, 1);

            reads.Failure = new IOException();
            await Assert.ThrowsAsync<IOException>(() => terrain.HeightFields[0].ReadCpuAsync(range).AsTask());

            reads.Failure = null;
            HeightFieldRead recovered = await branch.HeightFields[0].ReadCpuAsync(range);
            HeightFieldRead shared = await terrain.HeightFields[0].ReadCpuAsync(range);
            Assert.Equal(2f, recovered[1, 1]);
            Assert.Equal(edit.Revision, recovered.Revision);
            Assert.Equal(2f, shared[1, 1]);
            Assert.Equal(edit.Revision, shared.Revision);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RegionalEditsKeepTheirFileDataAfterTheOriginalTerrainCloses()
    {
        string path = Path.Combine(Path.GetTempPath(), $"SomeEngine-Terrain-Region-{Guid.NewGuid():N}.terrain.asset");
        string changedPath = Path.Combine(Path.GetTempPath(), $"SomeEngine-Terrain-Edited-{Guid.NewGuid():N}.terrain.asset");
        try
        {
            using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
            var editing = new TerrainEditing(scheduler);
            var initial = new Terrain(editing);
            HeightField heights = initial.CreateHeightField("ground", new HeightGrid(8, 4, 4));
            VoxelField voxels = initial.CreateVoxelField("detail", new VoxelGrid(4));
            TerrainEdit heightEdit = heights.EditCpu(new HeightDeltaBrush(heights.Grid.Range, 2f));
            initial.Surface = new HeightSurface(heights);
            initial.Surface.WriteVoxelsCpu(voxels, new(0, 0, 0, 8, 4, 4));
            TerrainCpuData initialData = await initial.CaptureCpuAsync();
            AssetEntry entry;
            using (var asset = new TerrainAsset())
            {
                await asset.UpdateAsync(initialData, Array.Empty<AssetId<MaterialAsset>>());
                entry = await asset.SaveAsync(path);
            }

            var reads = new FileReads();
            using (var description = await TerrainAsset.OpenAsync(path))
            await using (var file = await AssetFile.OpenAsync(path, TerrainAsset.WireType))
            {
                HeightFieldsBlock fields = await description.GetBlockAsync<HeightFieldsBlock>();
                AssetBlockDescription block = description.BlockDescriptions.Single(block => block.BlockTypeId == HeightFieldsBlock.BlockTypeId);
                foreach (TerrainHeightPage page in fields.Pages)
                {
                    BinaryChunkEntry chunk = await file.DescribeChunkAsync(block.GetStorageKey(page.ChunkKey));
                    reads.Ranges.Add((chunk.Offset, chunk.Offset + chunk.StoredLength, page.X));
                }
            }
            await using var loader = new AssetLoader(new TerrainStorage(entry, path, reads));
            using var opened = await TerrainAsset.OpenAsync(loader, new(entry.AssetGuid));
            using Terrain terrain = await opened.CreateTerrainAsync(editing);
            using Terrain branch = terrain.Duplicate();
            opened.Dispose();
            await loader.DisposeAsync();
            Assert.Empty(reads.Pages);

            reads.PausePage = 0;
            ValueTask<HeightFieldRead> firstRead = terrain.HeightFields[0].ReadCpuAsync(new(1, 1, 1, 1));
            await reads.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            terrain.Dispose();
            TerrainEdit edit = branch.HeightFields[0].EditCpu(new HeightDeltaBrush(new(1, 1, 1, 1), 3f));
            reads.Continue.TrySetResult();
            HeightFieldRead earlier = await firstRead;
            HeightFieldRead current = await branch.HeightFields[0].ReadCpuAsync(new(1, 1, 1, 1));
            Assert.Equal(2f, earlier[1, 1]);
            Assert.Equal(5f, current[1, 1]);
            Assert.Equal(new[] { 0 }, reads.Pages.Order().ToArray());

            HeightFieldRead distant = await branch.HeightFields[0].ReadCpuAsync(new(6, 1, 1, 1));
            Assert.Equal(2f, distant[6, 1]);
            Assert.Equal(new[] { 0, 1 }, reads.Pages.Order().ToArray());
            ValueTask<TerrainCpuData> capture = branch.CaptureCpuAsync();
            branch.Dispose();
            TerrainCpuData data = await capture;
            Assert.Equal(edit.Revision, data.HeightFields[0].Pages.Single(page => page.X == 0).Revision);
            Assert.Equal(heightEdit.Revision, data.HeightFields[0].Pages.Single(page => page.X == 1).Revision);
            Assert.NotEqual(initialData.HeightFields[0].Pages.Single(page => page.X == 0).Id,
                data.HeightFields[0].Pages.Single(page => page.X == 0).Id);
            Assert.Equal(initialData.HeightFields[0].Pages.Single(page => page.X == 1).Id,
                data.HeightFields[0].Pages.Single(page => page.X == 1).Id);
            Assert.True(reads.Closed);
            using (var asset = new TerrainAsset())
            {
                await asset.UpdateAsync(data, Array.Empty<AssetId<MaterialAsset>>());
                await asset.SaveAsync(changedPath);
            }
            using var saved = await TerrainAsset.OpenAsync(changedPath);
            using Terrain restored = await saved.CreateTerrainAsync(editing);
            saved.Dispose();
            HeightFieldRead result = await restored.HeightFields[0].ReadCpuAsync(heights.Grid.Range);
            VoxelFieldRead volume = await restored.VoxelFields[0].ReadCpuAsync(new(6, 1, 1, 1, 1, 1));
            Assert.Equal(5f, result[1, 1]);
            Assert.Equal(2f, result[6, 1]);
            Assert.Equal(-1f, volume[6, 1, 1]);
            TerrainCpuData savedData = await restored.CaptureCpuAsync();
            Assert.Equal(data.HeightFields[0].Pages.Select(page => page.Id), savedData.HeightFields[0].Pages.Select(page => page.Id));
        }
        finally
        {
            File.Delete(path);
            File.Delete(changedPath);
        }
    }

    private sealed class FileReads
    {
        internal List<(long Min, long Max, int Page)> Ranges { get; } = [];
        internal HashSet<int> Pages { get; } = [];
        internal int PausePage { get; set; } = -1;
        internal IOException? Failure { get; set; }
        internal bool Closed { get; set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async ValueTask ObserveAsync(long offset, int length)
        {
            bool pause = false;
            bool pageRead = false;
            lock (Pages)
                foreach (var range in Ranges)
                    if (offset < range.Max && offset + length > range.Min)
                    {
                        Pages.Add(range.Page);
                        pageRead = true;
                        pause |= range.Page == PausePage;
                    }
            if (pageRead && Failure is { } failure) throw failure;
            if (pause)
            {
                Started.TrySetResult();
                await Continue.Task;
            }
        }
    }

    private sealed class TerrainStorage(AssetEntry entry, string path, FileReads reads) : IAssetStorage
    {
        public bool TryFind(Guid id, out AssetEntry result) { result = entry; return id == entry.AssetGuid; }
        public ValueTask<IRangeSource> OpenAsync(AssetEntry selected, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IRangeSource>(new ObservedSource(FileRangeSource.Open(path), reads));
    }

    private sealed class ObservedSource(IRangeSource source, FileReads reads) : IRangeSource
    {
        public long Length => source.Length;
        public string Generation => source.Generation;
        public bool LeasesAreImmutable => source.LeasesAreImmutable;
        public bool RetainsResidentBacking => source.RetainsResidentBacking;
        public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            await reads.ObserveAsync(offset, destination.Length);
            await source.ReadExactlyAsync(offset, destination, cancellationToken);
        }
        public async ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken cancellationToken = default)
        {
            await reads.ObserveAsync(offset, length);
            return await source.AcquireAsync(offset, length, cancellationToken);
        }
        public async ValueTask DisposeAsync() { await source.DisposeAsync(); reads.Closed = true; }
    }
}
