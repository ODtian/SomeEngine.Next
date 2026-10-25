using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using SlangShaderSharp;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class DeviceCollisionGridTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void DevicePairsFollowTheSelectedQueriesWhenEnabledMembersChange()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        DeviceGridKernels kernels = CompileKernels("#include \"physics_grid.slang\"");
        using var grid = new DeviceCollisionGrid(backend, device, kernels,
            new DeviceGridSettings(1, 3), new DeviceGridCapacity(3, 64, 127, 8, 1));
        Entity first = world.CreateEntity(new DeviceGridTestBody(Box(0), 0, 11, 0));
        Entity shared = world.CreateEntity(new DeviceGridTestBody(Box(0), 0, 22, 0));
        Entity third = world.CreateEntity(new DeviceGridTestBody(Box(0), 0, 33, 0));
        world.Add(first, new DeviceGridFirst());
        world.Add(shared, new DeviceGridFirst());
        world.Add(shared, new DeviceGridSecond());
        world.Add(third, new DeviceGridSecond());
        world.Add(first, new DeviceCollisionBody(grid, 0));
        world.Add(shared, new DeviceCollisionBody(grid, 1));
        world.Add(third, new DeviceCollisionBody(grid, 2));
        QueryHandle firstMembers = world.Query(world.QueryDefinition().Enabled<DeviceGridFirst>());
        QueryHandle secondMembers = world.Query(world.QueryDefinition().Enabled<DeviceGridSecond>());
        QueryDefinition bodies = world.QueryDefinition().All<DeviceGridTestBody>().Build();
        var stage = grid.Candidates<DeviceGridTestBody, DeviceGridTestReader>(
            world.Product(firstMembers, secondMembers), default, 2, DevicePairRules.None,
            DeviceInputTransfer.Upload, DevicePairReadback.Pairs);
        var simulation = PhysicsSimulation.Create(world, bodies, stage, 1);

        simulation.Schedule(1, 0, 0.25).Complete();
        Assert.Equal([
            new DeviceCollisionPair(first, shared, 11, 22, 0, 1),
            new DeviceCollisionPair(first, third, 11, 33, 0, 2),
            new DeviceCollisionPair(shared, shared, 22, 22, 1, 1),
            new DeviceCollisionPair(shared, third, 22, 33, 1, 2)
        ], Ordered(grid.ReadBin(0)));

        world.Disable<DeviceGridFirst>(shared);
        world.Disable<DeviceGridSecond>(third);
        simulation.Schedule(2, 0.25, 0.25).Complete();

        Assert.Equal([new DeviceCollisionPair(first, shared, 11, 22, 0, 1)], grid.ReadBin(0).ToArray());
    }

    [Fact]
    public void DeviceGridStepReturnsFilteredEntityPairsAfterDeviceCompletion()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        DeviceGridKernels kernels = CompileKernels("#include \"physics_grid.slang\"");
        using var grid = new DeviceCollisionGrid(backend, device, kernels,
            new DeviceGridSettings(CellSize: 1, LevelCount: 1),
            new DeviceGridCapacity(Bodies: 3, Cells: 64, Buckets: 127, Pairs: 16, Bins: 3));
        Entity first = world.CreateEntity(new DeviceGridTestBody(Box(0), 0, 11, 7));
        Entity second = world.CreateEntity(new DeviceGridTestBody(Box(0.2f), 1, 22, 8));
        Entity third = world.CreateEntity(new DeviceGridTestBody(Box(0.4f), 1, 33, 7));
        world.Add(first, new DeviceCollisionBody(grid, 0));
        world.Add(second, new DeviceCollisionBody(grid, 1));
        world.Add(third, new DeviceCollisionBody(grid, 2));
        QueryDefinition bodies = world.QueryDefinition().All<DeviceGridTestBody>().Build();
        QueryHandle members = world.Query(bodies);
        var stages = grid.Candidates<DeviceGridTestBody, DeviceGridTestReader>(world.Within(members), default, 2,
            DevicePairRules.MutualCategories | DevicePairRules.ExceptMatchingKeys,
            DeviceInputTransfer.Upload, DevicePairReadback.Pairs);
        var simulation = PhysicsSimulation.Create(world, bodies, stages, substeps: 1);

        PhysicsStepResult step = simulation.Schedule(number: 7, startTime: 2, duration: 0.25);
        step.Complete();

        Assert.Equal([new DeviceCollisionPair(first, second, 11, 22, 0, 1)], grid.ReadBin(1).ToArray());
        Assert.Equal([new DeviceCollisionPair(second, third, 22, 33, 1, 2)], grid.ReadBin(2).ToArray());
        Assert.Equal(7, grid.Number);
        Assert.Equal(2.25, grid.Time);
        Assert.True(backend.IsComplete(grid.Completion));
    }

    [Fact]
    public void UserDeviceRulesAndClassifierReadTheirShaderResourceAcrossGridLevels()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        DeviceGridKernels kernels = CompileKernels("""
            #include "physics_grid_common.slang"
            StructuredBuffer<uint> UserTags;
            struct UserPairRule : IDevicePairRule
            {
                uint minimumDifference;
                bool matches(uint a, uint b, DeviceCollisionInput first, DeviceCollisionInput second)
                { return UserTags[a] + first.userValue + minimumDifference <= UserTags[b] + second.userValue; }
            };
            struct UserPairTypes : IDevicePairClassifier
            {
                uint selectedBin;
                uint firstBias;
                uint secondBias;
                DevicePairClassification classify(uint a, uint b, DeviceCollisionInput first, DeviceCollisionInput second)
                {
                    DevicePairClassification type;
                    type.bin = selectedBin;
                    type.firstGeometry = first.geometryIndex + firstBias;
                    type.secondGeometry = second.geometryIndex + secondBias;
                    return type;
                }
            };
            [shader("compute")] [numthreads(256,1,1)]
            void CountDevicePairs(uint3 id : SV_DispatchThreadID)
            {
                UserPairRule rule; rule.minimumDifference = 2;
                UserPairTypes types; types.selectedBin = 1; types.firstBias = 100; types.secondBias = 200;
                countDevicePairs(id.x, rule, types);
            }
            [shader("compute")] [numthreads(256,1,1)]
            void WriteDevicePairs(uint3 id : SV_DispatchThreadID)
            {
                UserPairRule rule; rule.minimumDifference = 2;
                UserPairTypes types; types.selectedBin = 1; types.firstBias = 100; types.secondBias = 200;
                writeDevicePairs(id.x, rule, types);
            }
            """);
        using SomeEngine.Graphics.Buffer tags = backend.CreateBuffer(device,
            new(12, BufferUsages.ShaderRead), MemoryType.Upload);
        using (MappedBuffer mapping = backend.Map(tags, MapType.Write, BufferRange.Whole))
        {
            uint[] values = [10, 0, 0];
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(mapping.Bytes);
            mapping.Flush(mapping.Range);
        }
        using BufferSrv tagsView = backend.CreateBufferSrv(device, new(tags, new(0, 12), StructureStride: 4));
        var accesses = new List<JobResourceAccess>();
        var resources = new JobResourceAccessBuilder(jobs, accesses);
        resources.Read(tags, 0, 12);
        using var grid = new DeviceCollisionGrid(backend, device, kernels,
            new DeviceGridSettings(0.25f, 8), new DeviceGridCapacity(3, 64, 127, 8, 2),
            new Dictionary<string, ResourceBinding[]> { ["UserTags"] = [ResourceBinding.ReadOnlyBuffer(tagsView)] },
            accesses.ToArray());
        Entity first = world.CreateEntity(new DeviceGridTestBody(new(Vector3.Zero, new(0.2f)), 0, 11, 1));
        Entity second = world.CreateEntity(new DeviceGridTestBody(new(new(-1, 0, 0), new(8, 1, 1)), 1, 22, 2));
        Entity third = world.CreateEntity(new DeviceGridTestBody(new(new(5, 0, 0), new(5.2f, 0.2f, 0.2f)), 0, 33, 4));
        world.Add(first, new DeviceCollisionBody(grid, 0));
        world.Add(second, new DeviceCollisionBody(grid, 1));
        world.Add(third, new DeviceCollisionBody(grid, 2));
        world.Add(first, new DeviceGridFirst());
        world.Add(second, new DeviceGridFirst());
        world.Add(second, new DeviceGridSecond());
        world.Add(third, new DeviceGridSecond());
        QueryDefinition bodies = world.QueryDefinition().All<DeviceGridTestBody>().Build();
        QueryHandle firstMembers = world.Query(world.QueryDefinition().Enabled<DeviceGridFirst>());
        QueryHandle secondMembers = world.Query(world.QueryDefinition().Enabled<DeviceGridSecond>());
        var stages = grid.Candidates<DeviceGridTestBody, DeviceGridTestReader>(
            world.Product(firstMembers, secondMembers), default, 1, DevicePairRules.None,
            DeviceInputTransfer.Upload, DevicePairReadback.Pairs);

        PhysicsSimulation.Create(world, bodies, stages, 1).Schedule(9, 3, 0.5).Complete();

        Assert.Equal([new DeviceCollisionPair(second, third, 122, 233, 1, 2)], grid.ReadBin(1).ToArray());
        Assert.Equal(9, grid.Number);
        Assert.Equal(3.5, grid.Time);
    }

    [Fact]
    public void DeviceBinPrefixCoversMultipleBlocksAndReusesEntityBindingsAfterInputChanges()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        DeviceGridKernels kernels = CompileKernels("#include \"physics_grid.slang\"");
        using var grid = new DeviceCollisionGrid(backend, device, kernels,
            new DeviceGridSettings(1, 1), new DeviceGridCapacity(260, 4096, 1021, 8, 3));
        var entities = new Entity[260];
        for (int index = 0; index < entities.Length; index++)
        {
            float x = index == 1 ? 0.5f : index == 259 ? 258 * 4 + 0.5f : index * 4;
            uint kind = index is 1 or 259 ? 1u : 0u;
            entities[index] = world.CreateEntity(new DeviceGridTestBody(Box(x), kind, (uint)(1000 + index), (uint)index));
            world.Add(entities[index], new DeviceCollisionBody(grid, index));
        }
        QueryDefinition bodies = world.QueryDefinition().All<DeviceGridTestBody>().Build();
        QueryHandle members = world.Query(bodies);
        var stages = grid.Candidates<DeviceGridTestBody, DeviceGridTestReader>(world.Within(members), default, 64,
            DevicePairRules.None, DeviceInputTransfer.Upload, DevicePairReadback.Pairs);
        var simulation = PhysicsSimulation.Create(world, bodies, stages, 1);

        PhysicsStepResult first = simulation.Schedule(1, 0, 0.25);
        first.Complete();
        Assert.Equal([
            new DeviceCollisionPair(entities[0], entities[1], 1000, 1001, 0, 1),
            new DeviceCollisionPair(entities[258], entities[259], 1258, 1259, 258, 259)
        ], grid.ReadBin(1).ToArray());

        world.DestroyEntity(entities[259]);
        Entity replacement = world.CreateEntity(new DeviceGridTestBody(Box(258 * 4 + 0.5f), 1, 9000, 2));
        world.Add(replacement, new DeviceCollisionBody(grid, 259));
        var move = new MoveDeviceBody(world, entities[0], new(Box(10000), 0, 1000, 0));
        JobHandle input = ComponentJobAccess<DeviceGridTestBody>.ScheduleWrite(world, in move, first.Completion);
        PhysicsStepResult second = simulation.Schedule(2, 0.25, 0.25, input);
        second.Complete();

        Assert.Equal([new DeviceCollisionPair(entities[258], replacement, 1258, 9000, 258, 259)],
            grid.ReadBin(1).ToArray());
        Assert.Equal(2, grid.Number);
        Assert.Equal(0.5, grid.Time);
    }

    [Fact]
    public void DeviceBetweenAndProductKeepResidentInputsUntilTheSelectedUpload()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        DeviceGridKernels kernels = CompileKernels("#include \"physics_grid.slang\"");
        using var grid = new DeviceCollisionGrid(backend, device, kernels,
            new DeviceGridSettings(1, 1), new DeviceGridCapacity(3, 64, 127, 8, 1));
        Entity first = world.CreateEntity(new DeviceGridTestBody(Box(0), 0, 11, 0));
        Entity shared = world.CreateEntity(new DeviceGridTestBody(Box(0), 0, 22, 0));
        Entity third = world.CreateEntity(new DeviceGridTestBody(Box(0), 0, 33, 0));
        world.Add(first, new DeviceCollisionBody(grid, 0));
        world.Add(shared, new DeviceCollisionBody(grid, 1));
        world.Add(third, new DeviceCollisionBody(grid, 2));
        world.Add(first, new DeviceGridSecond());
        world.Add(shared, new DeviceGridFirst());
        world.Add(shared, new DeviceGridSecond());
        world.Add(third, new DeviceGridFirst());
        QueryDefinition bodies = world.QueryDefinition().All<DeviceGridTestBody>().Build();
        QueryHandle firstMembers = world.Query(world.QueryDefinition().Enabled<DeviceGridFirst>());
        QueryHandle secondMembers = world.Query(world.QueryDefinition().Enabled<DeviceGridSecond>());
        var pairs = world.Product(firstMembers, secondMembers);
        var write = grid.WriteInputs<DeviceGridTestBody, DeviceGridTestReader>(pairs, default, 1);
        var between = grid.Candidates<DeviceGridTestBody, DeviceGridTestReader>(
            world.Between(firstMembers, secondMembers), default, 1, DevicePairRules.None,
            DeviceInputTransfer.Upload, DevicePairReadback.Pairs);

        PhysicsSimulation.Create(world, bodies, between, 1).Schedule(1, 0, 0.25).Complete();
        Assert.Equal([
            new DeviceCollisionPair(first, shared, 11, 22, 0, 1),
            new DeviceCollisionPair(first, third, 11, 33, 0, 2),
            new DeviceCollisionPair(shared, third, 22, 33, 1, 2)
        ], Ordered(grid.ReadBin(0)));

        world.Replace(first, new DeviceGridTestBody(Box(10), 0, 11, 0));
        var resident = write.Then(grid.Candidates<DeviceGridTestBody, DeviceGridTestReader>(
            pairs, default, 1, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.Pairs));
        PhysicsSimulation.Create(world, bodies, resident, 1).Schedule(2, 0.25, 0.25).Complete();
        Assert.Equal([
            new DeviceCollisionPair(shared, first, 22, 11, 1, 0),
            new DeviceCollisionPair(shared, shared, 22, 22, 1, 1),
            new DeviceCollisionPair(third, first, 33, 11, 2, 0),
            new DeviceCollisionPair(third, shared, 33, 22, 2, 1)
        ], Ordered(grid.ReadBin(0)));

        var upload = grid.Candidates<DeviceGridTestBody, DeviceGridTestReader>(
            pairs, default, 1, DevicePairRules.None, DeviceInputTransfer.Upload, DevicePairReadback.Pairs);
        PhysicsSimulation.Create(world, bodies, upload, 1).Schedule(3, 0.5, 0.25).Complete();
        Assert.Equal([
            new DeviceCollisionPair(shared, shared, 22, 22, 1, 1),
            new DeviceCollisionPair(third, shared, 33, 22, 2, 1)
        ], Ordered(grid.ReadBin(0)));
        Assert.Equal(3, grid.Number);
        Assert.Equal(0.75, grid.Time);
    }

    private DeviceGridKernels CompileKernels(string source)
    {
        string directory = ShaderDirectory();
        D3D12TestEntry[] entries = [new("ClearDeviceGrid", SlangStage.Compute),
            new("BuildDeviceGrid", SlangStage.Compute), new("CountDevicePairs", SlangStage.Compute),
            new("ScanDevicePairCounts", SlangStage.Compute), new("OffsetDevicePairBlocks", SlangStage.Compute),
            new("OffsetDevicePairBins", SlangStage.Compute), new("WriteDevicePairs", SlangStage.Compute)];
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("device_collision_grid", source, entries,
            "sm_6_0", directory);
        ComputePipelineDesc Entry(int index) =>
            new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6));
    }

    private static string ShaderDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "assets", "Shaders")))
                return Path.Combine(directory.FullName, "assets", "Shaders");
        throw new DirectoryNotFoundException();
    }

    private static CollisionBounds Box(float x) => new(new Vector3(x, 0, 0), new Vector3(x + 1, 1, 1));

    private static DeviceCollisionPair[] Ordered(ReadOnlySpan<DeviceCollisionPair> pairs) =>
        pairs.ToArray().OrderBy(pair => pair.First.Index).ThenBy(pair => pair.Second.Index).ToArray();

    private readonly struct MoveDeviceBody(World world, Entity entity, DeviceGridTestBody value) : IJob
    {
        public void Execute() => world.Replace(entity, in value);
    }
}

public readonly record struct DeviceGridTestBody(CollisionBounds Bounds, uint Kind, uint Geometry, uint Key) : IComponent;
public struct DeviceGridFirst : IEnableableComponent;
public struct DeviceGridSecond : IEnableableComponent;

public readonly struct DeviceGridTestReader : IDeviceCollisionReader<DeviceGridTestBody>
{
    public DeviceCollisionInput Read(Entity entity, in DeviceGridTestBody body) => new()
    {
        Min = body.Bounds.Min, Max = body.Bounds.Max,
        EntityIndex = (uint)entity.Index, EntityGeneration = (uint)entity.Generation,
        GeometryType = body.Kind, GeometryIndex = body.Geometry,
        KeyLow = body.Key, HasKey = 1, CategoriesLow = 1, AllowedLow = 1, UserValue = body.Key,
    };
}
