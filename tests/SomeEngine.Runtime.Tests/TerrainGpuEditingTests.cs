using System.Numerics;
using DefaultGame;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS.Systems;
using SomeEngine.Core.Math;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Render.Components;
using SomeEngine.Render.Terrain;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class TerrainGpuEditingTests(SceneContentFixture content)
{
    [Fact]
    public async Task MultipleTerrainFeaturesRecordEachFieldEditOnce()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var first = new HeightTerrainExample(scheduler);
        var second = new HeightTerrainExample(scheduler);
        Shader shader = await LoadHeightShaderAsync();
        int firstRecords = 0, secondRecords = 0;
        Task<HeightFieldRead>? firstRead = null, secondRead = null;
        var range = new HeightRegion(0, 0, 2, 2);
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "8"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    Material red = frame.Scene.GetMaterial(frame.Scene.MeshEntities.First());
                    first.AddPlacements(red, red);
                    second.AddPlacements(red, red);
                    first.Heights.EditGpu(new RecordedHeightBrush(new(shader, range, 1f), () => firstRecords++));
                    second.Heights.EditGpu(new RecordedHeightBrush(new(shader, range, 2f), () => secondRecords++));
                    firstRead = first.Heights.ReadCpuAsync(range).AsTask();
                    secondRead = second.Heights.ReadCpuAsync(range).AsTask();
                }
            },
        };
        first.Install(application);
        second.Install(application);

        Assert.Equal(0, await Application.RunAsync([], application: application));
        Assert.Equal(1f, (await firstRead!)[1, 1]);
        Assert.Equal(2f, (await secondRead!)[1, 1]);
        Assert.Equal(1, firstRecords);
        Assert.Equal(1, secondRecords);
    }

    private readonly record struct RecordedHeightBrush(HeightDeltaBrushGpu Brush, Action Recorded) : IHeightBrushGpu
    {
        public HeightRegion Range => Brush.Range;
        public int ReadBorder => Brush.ReadBorder;
        public void Record(ref HeightBrushGpuContext context)
        {
            Recorded();
            Brush.Record(ref context);
        }
    }

    [Fact]
    public async Task SavedHeightAndVoxelPagesFeedTheFirstGpuEdits()
    {
        string path = Path.Combine(Path.GetTempPath(), $"SomeEngine-Terrain-Gpu-Read-{Guid.NewGuid():N}.terrain.asset");
        try
        {
            using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
            var editing = new TerrainEditing(scheduler);
            var original = new Terrain(editing);
            HeightField heights = original.CreateHeightField("ground", new HeightGrid(8, 4, 4), 2f);
            VoxelField voxels = original.CreateVoxelField("detail", new VoxelGrid(4), 2f);
            original.Surface = new HeightSurface(heights);
            heights.EditCpu(new HeightDeltaBrush(heights.Grid.Range, 1f));
            voxels.EditCpu(new ScaleVoxelBrush(new(0, 0, 0, 8, 4, 4), 2f));
            using (var asset = new TerrainAsset())
            {
                await asset.UpdateAsync(await original.CaptureCpuAsync(), Array.Empty<AssetId<MaterialAsset>>());
                await asset.SaveAsync(path);
            }
            using var opened = await TerrainAsset.OpenAsync(path);
            using Terrain terrain = await opened.CreateTerrainAsync(editing);
            opened.Dispose();
            Shader heightShader = await LoadHeightShaderAsync();
            Shader voxelShader = await LoadVoxelShaderAsync();
            HeightField height = terrain.HeightFields[0];
            VoxelField voxel = terrain.VoxelFields[0];
            TerrainEdit heightEdit = height.EditGpu(new HeightDeltaBrushGpu(heightShader, new(1, 1, 1, 1), 2f));
            TerrainEdit voxelEdit = voxel.EditGpu(new VoxelSphereDeltaBrushGpu(voxelShader, voxel.Grid, new(1, 1, 1), 1f, -3f));
            Task<HeightFieldRead> heightRead = height.ReadCpuAsync(new(1, 1, 1, 1)).AsTask();
            Task<VoxelFieldRead> voxelRead = voxel.ReadCpuAsync(new(0, 1, 1, 2, 1, 1)).AsTask();
            var application = new ApplicationScene(content.DirectoryPath)
            {
                Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                    "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "12"]),
            };
            application.InstallGeometry(new FieldFeature(terrain));

            Assert.Equal(0, await Application.RunAsync([], application: application));
            HeightFieldRead actualHeight = await heightRead;
            VoxelFieldRead actualVoxel = await voxelRead;
            Assert.Equal(5f, actualHeight[1, 1]);
            Assert.Equal(4f, actualVoxel[0, 1, 1]);
            Assert.Equal(1f, actualVoxel[1, 1, 1]);
            Assert.Equal(heightEdit.Revision, actualHeight.Revision);
            Assert.Equal(voxelEdit.Revision, actualVoxel.Revision);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task GpuCaptureSavesItsFieldsAndBlendWhileTheOriginalContinuesEditing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"SomeEngine-Gpu-Terrain-{Guid.NewGuid():N}.terrain.asset");
        try
        {
            using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
            var editing = new TerrainEditing(scheduler);
            using var terrain = new Terrain(editing);
            HeightField heights = terrain.CreateHeightField("ground", new HeightGrid(5, 5, 2));
            VoxelField voxels = terrain.CreateVoxelField("detail", new VoxelGrid(2));
            var height = new HeightSurface(heights);
            var voxelRange = new VoxelRegion(-2, -2, -2, 7, 7, 7);
            await height.WriteVoxelsCpu(voxels, voxelRange).WaitAsync();
            var region = new TerrainBounds(new(0, -4, 0), new(4, 4, 4));
            var blend = new BlendSurface(height, new VoxelSurface(voxels), .5f, region, 1f);
            terrain.Surface = new BlendSurface(blend, height, .5f);
            Shader heightShader = await LoadHeightShaderAsync();
            Shader voxelShader = await LoadVoxelShaderAsync();
            var center = new Vector3(1, 1, 1);
            TerrainEdit savedHeightEdit = default, savedVoxelEdit = default, nextHeightEdit = default, nextVoxelEdit = default;
            ulong savedRevision = 0, nextRevision = 0;
            Task<TerrainCpuData>? capture = null;
            Task<HeightFieldRead>? nextHeights = null;
            Task<VoxelFieldRead>? nextVoxels = null;
            Task<TerrainSurfaceRead>? nextSurface = null;
            var application = new ApplicationScene(content.DirectoryPath)
            {
                Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                    "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "16"]),
                UpdateFrame = frame =>
                {
                    if (frame.Index == 0)
                    {
                        savedHeightEdit = heights.EditGpu(new HeightDeltaBrushGpu(heightShader, heights.Grid.Range, 2f));
                        savedVoxelEdit = voxels.EditGpu(new VoxelSphereDeltaBrushGpu(voxelShader, voxels.Grid, center, 2f, -1f));
                        savedRevision = terrain.Revision;
                        capture = terrain.CaptureCpuAsync().AsTask();

                        nextHeightEdit = heights.EditGpu(new HeightDeltaBrushGpu(heightShader, heights.Grid.Range, 5f));
                        nextVoxelEdit = voxels.EditGpu(new VoxelSphereDeltaBrushGpu(voxelShader, voxels.Grid, center, 2f, 3f));
                        terrain.Surface = height;
                        nextRevision = terrain.Revision;
                        nextHeights = heights.ReadCpuAsync(heights.Grid.Range).AsTask();
                        nextVoxels = voxels.ReadCpuAsync(voxelRange).AsTask();
                        nextSurface = terrain.Surface.ReadCpuAsync(region).AsTask();
                    }
                },
            };
            application.InstallGeometry(new FieldFeature(terrain));

            Assert.Equal(0, await Application.RunAsync([], application: application));
            TerrainCpuData data = await capture!;
            using (var asset = new TerrainAsset { Name = "Captured GPU terrain" })
            {
                await asset.UpdateAsync(data, []);
                await asset.SaveAsync(path);
            }
            using TerrainAsset opened = await TerrainAsset.OpenAsync(path);
            using Terrain restored = await opened.CreateTerrainAsync(editing);
            opened.Dispose();

            HeightFieldRead savedHeights = await restored.HeightFields[0].ReadCpuAsync(heights.Grid.Range);
            VoxelFieldRead savedVoxels = await restored.VoxelFields[0].ReadCpuAsync(voxelRange);
            TerrainSurfaceRead savedSurface = await restored.Surface!.ReadCpuAsync(region);
            HeightFieldRead changedHeights = await nextHeights!;
            VoxelFieldRead changedVoxels = await nextVoxels!;
            TerrainSurfaceRead changedSurface = await nextSurface!;

            for (int z = 0; z < heights.Grid.Depth; z++)
            for (int x = 0; x < heights.Grid.Width; x++)
            {
                Assert.Equal(2f, savedHeights[x, z]);
                Assert.Equal(7f, changedHeights[x, z]);
            }
            for (int z = voxelRange.MinZ; z < voxelRange.MaxZ; z++)
            for (int y = voxelRange.MinY; y < voxelRange.MaxY; y++)
            for (int x = voxelRange.MinX; x < voxelRange.MaxX; x++)
            {
                float weight = MathF.Max(0f, 1f - Vector3.Distance(new(x, y, z), center) / 2f);
                Assert.Equal(y - weight, savedVoxels[x, y, z], tolerance: 1e-5f);
                Assert.Equal(y + 2f * weight, changedVoxels[x, y, z], tolerance: 1e-5f);
            }
            Assert.Equal(savedRevision, data.Revision);
            Assert.Equal(savedRevision, restored.Revision);
            Assert.Equal(savedRevision, savedSurface.Revision);
            Assert.Equal(savedHeightEdit.Revision, savedHeights.Revision);
            Assert.Equal(savedVoxelEdit.Revision, savedVoxels.Revision);
            Assert.Equal(nextRevision, terrain.Revision);
            Assert.Equal(nextRevision, changedSurface.Revision);
            Assert.Equal(nextHeightEdit.Revision, changedHeights.Revision);
            Assert.Equal(nextVoxelEdit.Revision, changedVoxels.Revision);
            Assert.True(nextHeightEdit.Revision > savedRevision);
            Assert.True(nextVoxelEdit.Revision > savedRevision);
            Assert.Equal(-.75f, savedSurface.Sample(center));
            Assert.Equal(-6f, changedSurface.Sample(center));
            var restoredRoot = Assert.IsType<BlendSurface>(restored.Surface);
            var restoredBlend = Assert.IsType<BlendSurface>(restoredRoot.First);
            Assert.Same(restoredBlend.First, restoredRoot.Second);
            Assert.Same(restored.HeightFields[0], Assert.IsType<HeightSurface>(restoredRoot.Second).Field);
            Assert.Same(restored.VoxelFields[0], Assert.IsType<VoxelSurface>(restoredBlend.Second).Field);
            Assert.Equal(.5f, restoredRoot.Weight);
            Assert.Equal(.5f, restoredBlend.Weight);
            Assert.Equal(region, restoredBlend.Region);
            Assert.Equal(1f, restoredBlend.FadeDistance);
            Assert.Same(height, terrain.Surface);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task GpuVoxelNeighborhoodEditsMatchTheCpuVersions()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var cpuTerrain = new Terrain(new TerrainEditing(scheduler));
        var gpuTerrain = new Terrain(new TerrainEditing(scheduler));
        var grid = new VoxelGrid(3);
        VoxelField cpu = cpuTerrain.CreateVoxelField("cave", grid, 0f);
        VoxelField gpu = gpuTerrain.CreateVoxelField("cave", grid, 0f);
        var center = new Vector3(-1, 0, 0);
        var impulse = new VoxelSphereDeltaBrush(grid, center, .5f, 27f);
        await cpu.EditCpu(impulse).WaitAsync();
        await gpu.EditCpu(impulse).WaitAsync();
        Shader shader = await LoadNeighborhoodShaderAsync("terrain_voxel_neighborhood.slang");
        var smooth = new VoxelSmoothBrush(grid, center, 3f, 1, .5f);
        var flatten = new VoxelFlattenBrush(grid, center, 3f, new Plane(new Vector3(.25f, 1, .5f), -1f), .5f);
        VoxelRegion range = smooth.Range.Expand(1);
        await cpu.EditCpu(smooth).WaitAsync();
        VoxelFieldRead expectedSmooth = await cpu.ReadCpuAsync(range);
        await cpu.EditCpu(flatten).WaitAsync();
        VoxelFieldRead expectedFlatten = await cpu.ReadCpuAsync(range);
        TerrainEdit smoothEdit = gpu.EditGpu(new VoxelSmoothBrushGpu(shader, smooth));
        Task<VoxelFieldRead> smoothRead = gpu.ReadCpuAsync(range).AsTask();
        TerrainEdit flattenEdit = gpu.EditGpu(new VoxelFlattenBrushGpu(shader, flatten));
        Task<VoxelFieldRead> flattenRead = gpu.ReadCpuAsync(range).AsTask();
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "16"]),
        };
        application.InstallGeometry(new FieldFeature(gpuTerrain));

        Assert.Equal(0, await Application.RunAsync([], application: application));
        await flattenEdit.WaitAsync();
        VoxelFieldRead actualSmooth = await smoothRead;
        VoxelFieldRead actualFlatten = await flattenRead;

        for (int z = range.MinZ; z < range.MaxZ; z++)
        for (int y = range.MinY; y < range.MaxY; y++)
        for (int x = range.MinX; x < range.MaxX; x++)
        {
            Assert.Equal(expectedSmooth[x, y, z], actualSmooth[x, y, z], tolerance: 1e-5f);
            Assert.Equal(expectedFlatten[x, y, z], actualFlatten[x, y, z], tolerance: 1e-5f);
        }
        Assert.Equal(14f, actualSmooth[-1, 0, 0]);
        Assert.Equal(smoothEdit.Revision, actualSmooth.Revision);
        Assert.Equal(flattenEdit.Revision, actualFlatten.Revision);
    }

    [Fact]
    public async Task GpuHeightNeighborhoodEditsMatchTheCpuVersions()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var cpuTerrain = new Terrain(new TerrainEditing(scheduler));
        var gpuTerrain = new Terrain(new TerrainEditing(scheduler));
        var grid = new HeightGrid(7, 7, 3);
        HeightField cpu = cpuTerrain.CreateHeightField("ground", grid);
        HeightField gpu = gpuTerrain.CreateHeightField("ground", grid);
        var impulse = new HeightDeltaBrush(new HeightRegion(3, 3, 1, 1), 9f);
        await cpu.EditCpu(impulse).WaitAsync();
        await gpu.EditCpu(impulse).WaitAsync();
        Shader shader = await LoadNeighborhoodShaderAsync("terrain_height_neighborhood.slang");
        var smooth = new HeightSmoothBrush(grid, new Vector2(3, 3), 3f, 1, .5f);
        var flatten = new HeightFlattenBrush(grid, new Vector2(3, 3), 3f, new Plane(new Vector3(-.25f, 1, 0), -1f), .5f);
        await cpu.EditCpu(smooth).WaitAsync();
        HeightFieldRead expectedSmooth = await cpu.ReadCpuAsync(grid.Range);
        await cpu.EditCpu(flatten).WaitAsync();
        HeightFieldRead expectedFlatten = await cpu.ReadCpuAsync(grid.Range);
        TerrainEdit smoothEdit = gpu.EditGpu(new HeightSmoothBrushGpu(shader, smooth));
        Task<HeightFieldRead> smoothRead = gpu.ReadCpuAsync(grid.Range).AsTask();
        TerrainEdit flattenEdit = gpu.EditGpu(new HeightFlattenBrushGpu(shader, flatten));
        Task<HeightFieldRead> flattenRead = gpu.ReadCpuAsync(grid.Range).AsTask();
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "16"]),
        };
        application.InstallGeometry(new FieldFeature(gpuTerrain));

        Assert.Equal(0, await Application.RunAsync([], application: application));
        await flattenEdit.WaitAsync();
        HeightFieldRead actualSmooth = await smoothRead;
        HeightFieldRead actualFlatten = await flattenRead;

        for (int z = 0; z < grid.Depth; z++)
        for (int x = 0; x < grid.Width; x++)
        {
            Assert.Equal(expectedSmooth[x, z], actualSmooth[x, z], tolerance: 1e-5f);
            Assert.Equal(expectedFlatten[x, z], actualFlatten[x, z], tolerance: 1e-5f);
        }
        Assert.Equal(5f, actualSmooth[3, 3]);
        Assert.Equal(3.375f, actualFlatten[3, 3], tolerance: 1e-5f);
        Assert.Equal(smoothEdit.Revision, actualSmooth.Revision);
        Assert.Equal(flattenEdit.Revision, actualFlatten.Revision);
    }

    [Fact]
    public async Task GpuVoxelSphereDeltaMatchesCpuSamplesAfterTheEditCompletes()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var cpuTerrain = new Terrain(new TerrainEditing(scheduler));
        var gpuTerrain = new Terrain(new TerrainEditing(scheduler));
        var grid = new VoxelGrid(4);
        VoxelField cpu = cpuTerrain.CreateVoxelField("cave", grid, 2f);
        VoxelField gpu = gpuTerrain.CreateVoxelField("cave", grid, 2f);
        Shader shader = await LoadVoxelShaderAsync();
        var brush = new VoxelSphereDeltaBrush(grid, new Vector3(-1, 0, 0), 2f, -4f);
        await cpu.EditCpu(brush).WaitAsync();
        TerrainEdit edit = gpu.EditGpu(new VoxelSphereDeltaBrushGpu(shader, grid, brush.Center, brush.Radius, brush.Delta));
        VoxelRegion range = brush.Range.Expand(1);
        Task<VoxelFieldRead> read = gpu.ReadCpuAsync(range).AsTask();
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "12"]),
        };
        application.InstallGeometry(new FieldFeature(gpuTerrain));

        int result = await Application.RunAsync([], application: application);
        await edit.WaitAsync();
        VoxelFieldRead actual = await read;
        VoxelFieldRead expected = await cpu.ReadCpuAsync(range);

        Assert.Equal(0, result);
        Assert.Equal(edit.Revision, actual.Revision);
        Assert.True(edit.Completion.IsCompleted);
        for (int z = range.MinZ; z < range.MaxZ; z++)
        for (int y = range.MinY; y < range.MaxY; y++)
        for (int x = range.MinX; x < range.MaxX; x++)
            Assert.Equal(expected[x, y, z], actual[x, y, z], precision: 5);
    }

    [Fact]
    public async Task AlternatingCpuAndGpuVoxelEditsKeepEachCapturedVersion()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var terrain = new Terrain(new TerrainEditing(scheduler));
        VoxelField field = terrain.CreateVoxelField("cave", new VoxelGrid(4), 2f);
        Shader shader = await LoadVoxelShaderAsync();
        var brush = new VoxelSphereDeltaBrushGpu(shader, field.Grid, new Vector3(-1, 0, 0), 2f, -6f);
        var range = new VoxelRegion(-1, 0, 0, 3, 1, 1);
        var start = new EditStart();
        field.EditCpu(new ScaleVoxelBrush(brush.Range, 2f), scheduler.CreateExternalFenceHandle(start));
        TerrainEdit firstGpu = field.EditGpu(brush);
        Task<VoxelFieldRead> earlier = field.ReadCpuAsync(range).AsTask();
        TerrainEdit cpuEdit = field.EditCpu(new ScaleVoxelBrush(brush.Range, 3f));
        Task<VoxelFieldRead> middle = field.ReadCpuAsync(range).AsTask();
        TerrainEdit lastGpu = field.EditGpu(new VoxelSphereDeltaBrushGpu(shader, field.Grid, brush.Center, brush.Radius, 1f));
        Task<VoxelFieldRead> latest = field.ReadCpuAsync(range).AsTask();
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "16"]),
            UpdateFrame = frame => { if (frame.Index == 0) start.Complete(); },
        };
        application.InstallGeometry(new FieldFeature(terrain));

        int result = await Application.RunAsync([], application: application);
        VoxelFieldRead first = await earlier;
        VoxelFieldRead second = await middle;
        VoxelFieldRead third = await latest;

        Assert.Equal(0, result);
        Assert.Equal([-2f, 1f, 4f], new[] { first[-1, 0, 0], first[0, 0, 0], first[1, 0, 0] });
        Assert.Equal([-6f, 3f, 12f], new[] { second[-1, 0, 0], second[0, 0, 0], second[1, 0, 0] });
        Assert.Equal([-5f, 3.5f, 12f], new[] { third[-1, 0, 0], third[0, 0, 0], third[1, 0, 0] });
        Assert.Equal(firstGpu.Revision, first.Revision);
        Assert.Equal(cpuEdit.Revision, second.Revision);
        Assert.Equal(lastGpu.Revision, third.Revision);
    }

    [Fact]
    public async Task GpuVoxelSphereDeltaRendersTheSameChangedSurfaceAsCpu()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var gpu = new VoxelTerrainExample(scheduler);
        var cpu = new VoxelTerrainExample(scheduler);
        Shader shader = await LoadVoxelShaderAsync();
        var center = new Vector3(-1, 0, 0);
        TerrainEdit edit = default;
        SceneImage? before = null, after = null, expectedBefore = null, expectedAfter = null;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "18"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    var material = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    gpu.AddPlacement(material);
                }
                if (frame.Index == 1) gpu.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 8) frame.Scene.CaptureImage(image => before = image);
                if (frame.Index == 9) edit = gpu.AddSphereDeltaGpu(shader, center, 2f, -1f);
                if (frame.Index == 17) frame.Scene.CaptureImage(image => after = image);
            },
        };
        gpu.Install(application);

        int result = await Application.RunAsync([], application: application);
        await edit.WaitAsync();
        VoxelFieldRead values = await gpu.Field.ReadCpuAsync(gpu.SampleRegion);
        Assert.Equal(0, result);
        Assert.Equal(-2f, values[-1, 0, 0]);

        var reference = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "18"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    var material = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    cpu.AddPlacement(material);
                }
                if (frame.Index is 1 or 10) cpu.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 8) frame.Scene.CaptureImage(image => expectedBefore = image);
                if (frame.Index == 9) cpu.Field.EditCpu(new VoxelSphereDeltaBrush(cpu.Field.Grid, center, 2f, -1f));
                if (frame.Index == 17) frame.Scene.CaptureImage(image => expectedAfter = image);
            },
        };
        cpu.Install(reference);
        Assert.Equal(0, await Application.RunAsync([], application: reference));

        SceneImages.AssertEqual(expectedBefore!, before!);
        SceneImages.AssertEqual(expectedAfter!, after!);
        SceneImages.AssertChanged(before!, after!);
    }

    [Fact]
    public async Task GpuHeightDeltaMatchesCpuSamplesAfterTheEditCompletes()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var cpuTerrain = new Terrain(new TerrainEditing(scheduler));
        var gpuTerrain = new Terrain(new TerrainEditing(scheduler));
        var grid = new HeightGrid(8, 6, 4);
        var range = new HeightRegion(2, 1, 4, 3);
        HeightField cpu = cpuTerrain.CreateHeightField("ground", grid, 2f);
        HeightField gpu = gpuTerrain.CreateHeightField("ground", grid, 2f);
        Shader shader = await LoadHeightShaderAsync();
        await cpu.EditCpu(new HeightDeltaBrush(range, 3f)).WaitAsync();
        TerrainEdit edit = gpu.EditGpu(new HeightDeltaBrushGpu(shader, range, 3f));
        Task<HeightFieldRead> read = gpu.ReadCpuAsync(grid.Range).AsTask();
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "12"]),
        };
        scene.InstallGeometry(new FieldFeature(gpuTerrain));

        int result = await Application.RunAsync([], application: scene);
        await edit.WaitAsync();
        HeightFieldRead actual = await read;
        HeightFieldRead expected = await cpu.ReadCpuAsync(grid.Range);

        Assert.Equal(0, result);
        Assert.Equal(edit.Revision, actual.Revision);
        Assert.True(edit.Completion.IsCompleted);
        for (int z = 0; z < grid.Depth; z++)
        for (int x = 0; x < grid.Width; x++)
            Assert.Equal(expected[x, z], actual[x, z], precision: 5);
    }

    [Fact]
    public async Task CpuAndGpuEditsReadThePreviouslySubmittedHeightVersion()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var terrain = new Terrain(new TerrainEditing(scheduler));
        HeightField height = terrain.CreateHeightField("ground", new HeightGrid(8, 6, 4), 2f);
        Shader shader = await LoadHeightShaderAsync();
        var start = new EditStart();
        height.EditCpu(new HeightDeltaBrush(height.Grid.Range, 3f), scheduler.CreateExternalFenceHandle(start));
        TerrainEdit gpuEdit = height.EditGpu(new HeightDeltaBrushGpu(shader, new HeightRegion(2, 1, 4, 3), 7f));
        Task<HeightFieldRead> captured = height.ReadCpuAsync(height.Grid.Range).AsTask();
        TerrainEdit cpuEdit = height.EditCpu(new ScaleHeightBrush(height.Grid.Range, 4f));
        Task<HeightFieldRead> current = height.ReadCpuAsync(height.Grid.Range).AsTask();
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "12"]),
            UpdateFrame = frame => { if (frame.Index == 0) start.Complete(); },
        };
        scene.InstallGeometry(new FieldFeature(terrain));

        int result = await Application.RunAsync([], application: scene);
        HeightFieldRead gpu = await captured;
        HeightFieldRead cpu = await current;

        Assert.Equal(0, result);
        Assert.Equal(12f, gpu[3, 2]);
        Assert.Equal(5f, gpu[0, 0]);
        Assert.Equal(48f, cpu[3, 2]);
        Assert.Equal(20f, cpu[0, 0]);
        Assert.Equal(gpuEdit.Revision, gpu.Revision);
        Assert.Equal(cpuEdit.Revision, cpu.Revision);
    }

    [Fact]
    public async Task GpuHeightEditMovesBothPlacementsToTheRaisedSurface()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var example = new HeightTerrainExample(scheduler);
        Shader shader = await LoadHeightShaderAsync();
        TerrainEdit edit = default;
        SceneImage? before = null, after = null, expectedBefore = null, expectedAfter = null;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "18"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    var red = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    var green = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "green sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    example.AddPlacements(red, green);
                }
                if (frame.Index == 1) example.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 8) frame.Scene.CaptureImage(image => before = image);
                if (frame.Index == 9) edit = example.AddHeightGpu(shader, 1f);
                if (frame.Index == 17) frame.Scene.CaptureImage(image => after = image);
            },
        };
        example.Install(application);

        int result = await Application.RunAsync([], application: application);
        await edit.WaitAsync();
        HeightFieldRead heights = await example.Heights.ReadCpuAsync(example.Heights.Grid.Range);
        Assert.Equal(0, result);
        Assert.Equal(1f, heights[2, 2]);

        Mesh ground = CreateGround(0);
        await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 18,
        [
            new(0, scene =>
            {
                var red = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "red sphere"));
                var green = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "green sphere"));
                foreach (var entity in scene.MeshEntities) scene.DeleteEntity(entity);
                scene.AddEntity(ground, [red], new TransformQvvs(new Vector3(-5, -2, 6), Quaternion.Identity));
                scene.AddEntity(ground, [green], new TransformQvvs(new Vector3(1, -2, 6), Quaternion.Identity));
            }),
            new(8, scene => scene.CaptureImage(image => expectedBefore = image)),
            new(9, _ => ground.Update(CreateGround(1))),
            new(17, scene => scene.CaptureImage(image => expectedAfter = image)),
        ]), "--no-taa", "--no-vsm");

        SceneImages.AssertEqual(expectedBefore!, before!);
        SceneImages.AssertEqual(expectedAfter!, after!);
        SceneImages.AssertChanged(before!, after!);

        static Mesh CreateGround(float height)
        {
            Vector3[] positions = [new(0, height, 0), new(4, height, 0), new(0, height, 4), new(4, height, 4)];
            StandardClusterAttributes[] attributes = positions.Select(position =>
                new StandardClusterAttributes(Vector3.UnitY, new(1, 0, 0, 1), new(position.X, position.Z))).ToArray();
            return ClusterMeshBuilder.Build(positions, attributes, [0u, 2, 1, 1, 2, 3], [new(0, 6, 0)],
                StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime, "Raised ground reference");
        }
    }

    private static async ValueTask<Shader> LoadHeightShaderAsync()
    {
        DirectoryInfo? source = new(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "SomeEngine.slnx")))
            source = source.Parent;
        using var block = new ShaderSourceBlock { SourceNames = ["terrain_height_delta.slang"] };
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        return await block.CreateAsync(slang, new SessionDesc
        {
            Targets = [new TargetDesc { Format = SlangCompileTarget.Dxil, Profile = slang.FindProfile("sm_6_6") }],
            SearchPaths = [Path.Combine(source!.FullName, "assets/Shaders")],
        }, "terrain height delta");
    }

    private static async ValueTask<Shader> LoadVoxelShaderAsync()
    {
        DirectoryInfo? source = new(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "SomeEngine.slnx")))
            source = source.Parent;
        using var block = new ShaderSourceBlock { SourceNames = ["terrain_voxel_delta.slang"] };
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        return await block.CreateAsync(slang, new SessionDesc
        {
            Targets = [new TargetDesc { Format = SlangCompileTarget.Dxil, Profile = slang.FindProfile("sm_6_6") }],
            SearchPaths = [Path.Combine(source!.FullName, "assets/Shaders")],
        }, "terrain voxel delta");
    }

    internal static async ValueTask<Shader> LoadNeighborhoodShaderAsync(string sourceName)
    {
        DirectoryInfo? source = new(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "SomeEngine.slnx")))
            source = source.Parent;
        using var block = new ShaderSourceBlock { SourceNames = [sourceName] };
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        return await block.CreateAsync(slang, new SessionDesc
        {
            Targets = [new TargetDesc { Format = SlangCompileTarget.Dxil, Profile = slang.FindProfile("sm_6_6") }],
            SearchPaths = [Path.Combine(source!.FullName, "assets/Shaders")],
        }, "terrain neighborhood brush");
    }

    internal sealed class FieldFeature(Terrain terrain) : IApplicationGeometryFeature
    {
        public IDisposable Install(ApplicationGeometryContext context)
        {
            var data = new RenderTerrainData(context.Backend, context.Device, context.Graph);
            context.AddGameSystem(new FieldSourceSystem(terrain));
            context.AddExtractor(new TerrainRenderExtractor());
            context.AddPrepareSystem(data);
            context.AddFrameSystem(data);
            context.AddCompletionObserver(data);
            return data;
        }
    }

    private sealed class FieldSourceSystem(Terrain terrain) : ISystem<ImmediateSystemContext>
    {
        public void OnCreate(ref ImmediateSystemContext context) => context.World.CreateEntity(new TerrainSource(terrain));
        public void OnUpdate(ref ImmediateSystemContext context) { }
    }

    private readonly record struct ScaleHeightBrush(HeightRegion Range, float Scale) : IHeightBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(HeightFieldRead source, int x, int z) => source[x, z] * Scale;
    }

    private readonly record struct ScaleVoxelBrush(VoxelRegion Range, float Scale) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z) => source[x, y, z] * Scale;
    }

    private sealed class EditStart : IJobExternalFence
    {
        private readonly TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsSignaled => _signal.Task.IsCompleted;
        public void OnSignaled(Action<object?> callback, object? state)
        {
            if (IsSignaled) callback(state);
            else _signal.Task.GetAwaiter().OnCompleted(() => callback(state));
        }
        public void Complete() => _signal.SetResult();
    }
}
