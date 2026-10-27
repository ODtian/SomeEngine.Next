using System.Diagnostics;
using System.Numerics;
using DefaultGame;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit.Abstractions;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
public sealed class PhysicsMixedCrowdTests(ITestOutputHelper output)
{
    [Fact]
    public void TheRigidJointNetAndBoneClothUseOneCompletedFrame()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)], requiredFeatures: DeviceFeatures.IndirectCommands));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var scene = PhysicsCrowdExample.CreateMixedDevice(2, 4, 6, 8, backend, device).CreateSimulation(world);
        Assert.Equal(64, scene.Bodies.Count);
        Assert.Equal(32, scene.ClothBodies.Count);
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>());
        int jointCount = 0;
        world.ExecuteReadQuery(joints, ref jointCount, static (scoped QueryCursor rows, ref int count) =>
        {
            foreach (QueryRow row in rows.Rows) count++;
        });
        world.ReleaseQuery(joints);
        Assert.Equal(48, jointCount);
        Entity cloth = scene.ClothBodies[5];
        Vector3 initial = world.Read<BodyPose>(cloth).Position;
        Entity rigid = scene.Bodies.Where(body => world.Has<BoxGeometry>(body)
            && world.Read<BodyMass>(body).Motion == BodyMotion.Dynamic)
            .MinBy(body => Vector3.DistanceSquared(initial, world.Read<BodyPose>(body).Position));
        BodyPose pose = world.Read<BodyPose>(rigid);
        pose.Position = initial + new Vector3(0, 0, .12f);
        world.Replace(rigid, pose);

        scene.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult completed = scene.CompleteForRead();

        var pairs = new List<(Entity First, Entity Second)>();
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<UndirectedRelationEndpoints<ContactPoint>>());
        world.ExecuteReadQuery(contacts, ref pairs, static (scoped QueryCursor rows, ref List<(Entity First, Entity Second)> pairs) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                var ends = row.Read<UndirectedRelationEndpoints<ContactPoint>>();
                pairs.Add((ends.EndpointA, ends.EndpointB));
            }
        });
        world.ReleaseQuery(contacts);
        Assert.Contains(pairs, pair => pair.First == rigid && world.Has<DynamicFace>(pair.Second)
            || pair.Second == rigid && world.Has<DynamicFace>(pair.First));
        Assert.True(MathF.Abs(world.Read<BodyPose>(cloth).Position.Z - initial.Z) > .001f);
        Assert.Equal(completed.Number, world.Read<BodyStepState>(rigid).Number);
        Assert.Equal(completed.Number, world.Read<BodyStepState>(cloth).Number);
        using DevicePoseOutputRead output = GpuPoseAnimation.ReadDeviceOutput(world, scene.ClothTargets[0]);
        Assert.Equal(completed.Number, output.Output.Number);
        Assert.Equal(completed.EndTime, output.Output.Time, 12);
    }

    [Fact]
    public void TheRigidJointNetKeepsTheNodeBeneathItsStaticAnchor()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)], requiredFeatures: DeviceFeatures.IndirectCommands));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var scene = PhysicsCrowdExample.CreateMixedDevice(1, 2, 2, 0, backend, device).CreateSimulation(world);
        var rows = new List<(JointConstraint Joint, Entity Source, Entity Target)>();
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>());
        world.ExecuteReadQuery(joints, ref rows,
            static (scoped QueryCursor query, ref List<(JointConstraint Joint, Entity Source, Entity Target)> rows) =>
            {
                foreach (QueryRow row in query.Rows)
                {
                    var ends = row.Read<DirectedRelationEndpoints<JointConstraint>>();
                    rows.Add((row.Read<JointConstraint>(), ends.Source, ends.Target));
                }
            });
        world.ReleaseQuery(joints);
        var link = rows.Single(row => world.Has<BoxGeometry>(row.Source) && world.Has<BoxGeometry>(row.Target)
            && world.Read<BodyMass>(row.Source).Motion == BodyMotion.Static
            && world.Read<BodyPose>(row.Source).Position.Y > .5f
            && world.Read<BodyMass>(row.Target).Motion == BodyMotion.Dynamic);
        scene.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult completed = scene.CompleteForRead();
        BodyPose first = world.Read<BodyPose>(link.Source), second = world.Read<BodyPose>(link.Target);
        Vector3 firstAnchor = first.Position + Vector3.Transform(link.Joint.SourceFrame.Position, first.Rotation);
        Vector3 secondAnchor = second.Position + Vector3.Transform(link.Joint.TargetFrame.Position, second.Rotation);
        Assert.InRange(Vector3.Distance(firstAnchor, secondAnchor), 0, .001f);
        Assert.Equal(completed.Number, world.Read<BodyStepState>(link.Target).Number);
    }
    [Fact]
    [Trait("Category", "Performance")]
    public void TheDeclaredRigidJointAndClothCountsCompleteTheirSharedFrame()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)], requiredFeatures: DeviceFeatures.IndirectCommands));
        using var scheduler = new Scheduler(new() { WorkerCount = Environment.ProcessorCount });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var timings = new PhysicsSceneTimings();
        long setup = Stopwatch.GetTimestamp();
        using var scene = PhysicsCrowdExample.CreateMixedDevice(backend, device, timings).CreateSimulation(world);
        Assert.Equal(45000, scene.Bodies.Count);
        Assert.Equal(10000, scene.ClothBodies.Count);
        Assert.Equal(35000, scene.Bodies.Count - scene.ClothBodies.Count);
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>());
        int jointCount = 0;
        world.ExecuteReadQuery(joints, ref jointCount, static (scoped QueryCursor rows, ref int count) =>
        {
            foreach (QueryRow row in rows.Rows) count++;
        });
        world.ReleaseQuery(joints);
        Assert.Equal(72000, jointCount);
        float maximumDistance = scene.Bodies.Max(body => world.Read<BodyPose>(body).Position.Length()) + 10;
        output.WriteLine($"RigidBodies=35000; Joints=72000; ClothBodies=10000; Characters=625; SetupMilliseconds={Stopwatch.GetElapsedTime(setup).TotalMilliseconds:R}; StageTimings=true; IndirectCommands={backend.TryGetCapability<IndirectCommands>(device, out _)}");
        var elapsed = new List<double>(180);
        using var process = Process.GetCurrentProcess();
        for (int number = 0; number < 180; number++)
        {
            long started = Stopwatch.GetTimestamp();
            scene.AdvanceTime(scene.StepSeconds);
            PhysicsStepResult completed = scene.CompleteForRead();
            elapsed.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Assert.Equal(number, completed.Number);
            Assert.Equal((number + 1d) / 60, completed.EndTime, 12);
            output.WriteLine($"Step={number}; WholeStepMilliseconds={elapsed[^1]:R}; ManagedBytes={GC.GetTotalMemory(false)}");
            var largest = (Position: 0f, Speed: 0f, Number: completed.Number);
            QueryHandle bodies = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().Read<BodyPose>()
                .Read<BodyVelocity>().Read<BodyStepState>());
            world.ExecuteReadQuery(bodies, ref largest,
                static (scoped QueryCursor rows, ref (float Position, float Speed, long Number) largest) =>
                {
                    foreach (QueryRow row in rows.Rows)
                    {
                        float position = row.Read<BodyPose>().Position.Length(), speed = row.Read<BodyVelocity>().Linear.Length();
                        Assert.True(float.IsFinite(position + speed));
                        Assert.Equal(largest.Number, row.Read<BodyStepState>().Number);
                        largest.Position = MathF.Max(largest.Position, position);
                        largest.Speed = MathF.Max(largest.Speed, speed);
                    }
                });
            world.ReleaseQuery(bodies);
            Assert.InRange(largest.Position, 0, maximumDistance);
            Assert.InRange(largest.Speed, 0, 100);
            output.WriteLine($"Step={number}; MaximumBodyDistance={largest.Position:R}; MaximumBodySpeed={largest.Speed:R}");
            foreach (Entity target in new[] { scene.CharacterTargets[^1], scene.ClothTargets[0], scene.ClothTargets[^1] })
            {
                using DevicePoseOutputRead pose = GpuPoseAnimation.ReadDeviceOutput(world, target);
                Assert.Equal(completed.Number, pose.Output.Number);
                Assert.Equal(completed.EndTime, pose.Output.Time, 12);
            }
        }
        elapsed.Sort();
        process.Refresh();
        output.WriteLine($"CompletedSteps={elapsed.Count}; BudgetMilliseconds={1000d / 60:R}; WholeStepMeanMilliseconds={elapsed.Average():R}; WholeStepP95Milliseconds={elapsed[(int)Math.Ceiling(elapsed.Count * .95) - 1]:R}; WholeStepP99Milliseconds={elapsed[(int)Math.Ceiling(elapsed.Count * .99) - 1]:R}; WholeStepMaximumMilliseconds={elapsed[^1]:R}; ProcessPeakWorkingSetBytes={process.PeakWorkingSet64}");
        output.WriteLine($"Adapter={device.Adapter.Name}; DedicatedVideoMemoryBytes={device.Adapter.DedicatedVideoMemory}");
        output.WriteLine(timings.Describe().ToJsonString());
    }
}

