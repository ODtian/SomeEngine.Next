using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.Job;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Consumer.Tests;

public sealed class TerrainAssetBehaviorTests
{
    [Fact]
    public async Task SavedCaptureKeepsItsFieldsGraphAndMaterialsAcrossLaterEditsAndAssetClosure()
    {
        string path = Path.Combine(Path.GetTempPath(), $"SomeEngine-Terrain-{Guid.NewGuid():N}.terrain.asset");
        try
        {
            using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
            var editing = new TerrainEditing(scheduler);
            var terrain = new Terrain(editing);
            HeightField heights = terrain.CreateHeightField("ground", new HeightGrid(5, 5, 2));
            VoxelField voxels = terrain.CreateVoxelField("detail", new VoxelGrid(2));
            var height = new HeightSurface(heights);
            heights.EditCpu(new HeightDeltaBrush(heights.Grid.Range, 2f));
            height.WriteVoxelsCpu(voxels, new(-1, -1, -1, 5, 5, 5));
            voxels.EditCpu(new VoxelSphereDeltaBrush(voxels.Grid, new(1, 1, 1), 2f, -1f));
            var blend = new BlendSurface(height, new VoxelSurface(voxels), .5f,
                new(new(0, -4, 0), new(4, 4, 4)), 1f);
            terrain.Surface = new BlendSurface(blend, height, .5f);
            ulong revision = terrain.Revision;
            var material = new AssetId<MaterialAsset>(Guid.NewGuid());
            ValueTask<TerrainCpuData> capture = terrain.CaptureCpuAsync();

            heights.EditCpu(new HeightDeltaBrush(heights.Grid.Range, 5f));
            terrain.Surface = height;
            using (var asset = new TerrainAsset { Name = "captured ground" })
            {
                using var bindings = new TerrainMaterialBlock
                {
                    Materials = [material],
                    HeightSources = [new(0, "height", heights.Id)],
                    VoxelSources = [new(0, "density", voxels.Id)],
                };
                await asset.UpdateAsync(await capture, bindings);
                await asset.SaveAsync(path);
            }

            using var opened = await TerrainAsset.OpenAsync(path);
            Terrain restored = await opened.CreateTerrainAsync(editing);
            TerrainMaterialBlock materials = await opened.GetBlockAsync<TerrainMaterialBlock>();
            Assert.Equal(material, Assert.Single(materials.Materials));
            Assert.Equal(new TerrainMaterialHeightSource(0, "height", heights.Id), Assert.Single(materials.HeightSources));
            Assert.Equal(new TerrainMaterialVoxelSource(0, "density", voxels.Id), Assert.Single(materials.VoxelSources));
            HeightFieldsBlock heightFields = await opened.GetBlockAsync<HeightFieldsBlock>();
            using var source = heightFields.OpenPageSource();
            ulong pageKey = heightFields.Pages[0].ChunkKey;
            opened.Dispose();

            HeightFieldRead restoredHeights = await restored.HeightFields[0].ReadCpuAsync(heights.Grid.Range);
            VoxelFieldRead restoredVoxels = await restored.VoxelFields[0].ReadCpuAsync(new(1, 1, 1, 1, 1, 1));
            TerrainSurfaceRead restoredSurface = await restored.Surface!.ReadCpuAsync(new(Vector3.Zero, new(3)));
            ReadOnlyMemory<float> page = await source.ReadPageAsync(pageKey);
            Assert.Equal(revision, restored.Revision);
            Assert.Equal(2f, restoredHeights[1, 1]);
            Assert.Equal(-2f, restoredVoxels[1, 1, 1]);
            Assert.Equal(-1.25f, restoredSurface.Sample(new(1, 1, 1)));
            Assert.All(page.ToArray(), value => Assert.Equal(2f, value));
            var root = Assert.IsType<BlendSurface>(restored.Surface);
            Assert.Same(Assert.IsType<BlendSurface>(root.First).First, root.Second);

            TerrainEdit edit = restored.HeightFields[0].EditCpu(new HeightDeltaBrush(heights.Grid.Range, 1f));
            HeightFieldRead changed = await restored.HeightFields[0].ReadCpuAsync(heights.Grid.Range);
            Assert.Equal(3f, changed[1, 1]);
            Assert.Equal(edit.Revision, changed.Revision);
            Assert.Equal(2f, restoredHeights[1, 1]);
        }
        finally { File.Delete(path); }
    }
}
