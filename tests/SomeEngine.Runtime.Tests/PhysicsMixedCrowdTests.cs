using System.Diagnostics;
using System.Numerics;
using DefaultGame;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
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
    public void TheRigidJointNetBeginsWithCoincidentJointFrames()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)], requiredFeatures: DeviceFeatures.IndirectCommands));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(scheduler));
        using var scene = PhysicsCrowdExample.CreateMixedDevice(1, 250, 2, 0, backend, device).CreateSimulation(world);
        var joints = new List<(JointConstraint Joint, Entity Source, Entity Target)>();
        Query query = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>());
        using (QueryScope read = query.OpenRead())
            foreach (QueryChunk chunk in read)
            {
                ReadOnlySpan<JointConstraint> values = chunk.Read<JointConstraint>();
                ReadOnlySpan<RelationEndpoints<JointConstraint>> ends = chunk.Read<RelationEndpoints<JointConstraint>>();
                for (int row = 0; row < chunk.Count; row++) joints.Add((values[row], ends[row].Source, ends[row].Target));
            }
        
        double maximumGap = 0;
        var links = joints.Where(link => world.Has<BoxGeometry>(link.Source)
            && world.Has<BoxGeometry>(link.Target)).ToList();
        Assert.NotEmpty(links);
        foreach (var link in links)
        {
            BodyPose source = world.ReadComponentData<BodyPose>(link.Source), target = world.ReadComponentData<BodyPose>(link.Target);
            Vector3 sourceArm = Vector3.Transform(link.Joint.SourceFrame.Position, source.Rotation);
            Vector3 targetArm = Vector3.Transform(link.Joint.TargetFrame.Position, target.Rotation);
            double x = ((double)target.Position.X - source.Position.X) + targetArm.X - sourceArm.X;
            double y = ((double)target.Position.Y - source.Position.Y) + targetArm.Y - sourceArm.Y;
            double z = ((double)target.Position.Z - source.Position.Z) + targetArm.Z - sourceArm.Z;
            maximumGap = Math.Max(maximumGap, Math.Sqrt(x * x + y * y + z * z));
        }
        output.WriteLine($"JointFrameMaximumGap={maximumGap:R}");
        Assert.InRange(maximumGap, 0, 1e-7);
    }

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
        using World world = new World(execution: new WorldJobs(scheduler));
        using var scene = PhysicsCrowdExample.CreateMixedDevice(2, 4, 6, 8, backend, device).CreateSimulation(world, readContacts: true);
        Assert.Equal(64, scene.Bodies.Count);
        Assert.Equal(32, scene.ClothBodies.Count);
        Query joints = world.Query(new QueryDescription().WithStored<JointConstraint>());
        int jointCount = joints.Count;
        
        Assert.Equal(48, jointCount);
        Entity cloth = scene.ClothBodies[5];
        Vector3 initial = world.ReadComponentData<BodyPose>(cloth).Position;
        Entity rigid = scene.Bodies.Where(body => world.Has<BoxGeometry>(body)
            && world.ReadComponentData<BodyMass>(body).Motion == BodyMotion.Dynamic)
            .MinBy(body => Vector3.DistanceSquared(initial, world.ReadComponentData<BodyPose>(body).Position));
        BodyPose pose = world.ReadComponentData<BodyPose>(rigid);
        pose.Position = initial + new Vector3(0, 0, .12f);
        world.Set(rigid, pose);

        scene.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult completed = scene.CompleteForRead();

        var pairs = new List<(Entity First, Entity Second)>();
        foreach (ContactPair pair in scene.Contacts!.OpenView().Pairs)
            if (pair.PointCount != 0) pairs.Add((pair.FirstGeometry, pair.SecondGeometry));
        Assert.Contains(pairs, pair => pair.First == rigid && world.Has<DynamicFace>(pair.Second)
            || pair.Second == rigid && world.Has<DynamicFace>(pair.First));
        Assert.True(MathF.Abs(world.ReadComponentData<BodyPose>(cloth).Position.Z - initial.Z) > .001f);
        Assert.Equal(completed.Number, world.ReadComponentData<BodyStepState>(rigid).Number);
        Assert.Equal(completed.Number, world.ReadComponentData<BodyStepState>(cloth).Number);
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
        using World world = new World(execution: new WorldJobs(scheduler));
        using var scene = PhysicsCrowdExample.CreateMixedDevice(1, 2, 2, 0, backend, device).CreateSimulation(world);
        var rows = new List<(JointConstraint Joint, Entity Source, Entity Target)>();
        Query joints = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>());
        using (QueryScope read = joints.OpenRead())
            foreach (QueryChunk chunk in read)
            {
                ReadOnlySpan<JointConstraint> values = chunk.Read<JointConstraint>();
                ReadOnlySpan<RelationEndpoints<JointConstraint>> ends = chunk.Read<RelationEndpoints<JointConstraint>>();
                for (int row = 0; row < chunk.Count; row++) rows.Add((values[row], ends[row].Source, ends[row].Target));
            }
        
        var link = rows.Single(row => world.Has<BoxGeometry>(row.Source) && world.Has<BoxGeometry>(row.Target)
            && world.ReadComponentData<BodyMass>(row.Source).Motion == BodyMotion.Static
            && world.ReadComponentData<BodyPose>(row.Source).Position.Y > .5f
            && world.ReadComponentData<BodyMass>(row.Target).Motion == BodyMotion.Dynamic);
        scene.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult completed = scene.CompleteForRead();
        BodyPose first = world.ReadComponentData<BodyPose>(link.Source), second = world.ReadComponentData<BodyPose>(link.Target);
        Vector3 firstAnchor = first.Position + Vector3.Transform(link.Joint.SourceFrame.Position, first.Rotation);
        Vector3 secondAnchor = second.Position + Vector3.Transform(link.Joint.TargetFrame.Position, second.Rotation);
        Assert.InRange(Vector3.Distance(firstAnchor, secondAnchor), 0, .001f);
        Assert.Equal(completed.Number, world.ReadComponentData<BodyStepState>(link.Target).Number);
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
        using World world = new World(execution: new WorldJobs(scheduler));
        var timings = new PhysicsSceneTimings();
        long setup = Stopwatch.GetTimestamp();
        using var scene = PhysicsCrowdExample.CreateMixedDevice(backend, device, timings).CreateSimulation(world);
        Assert.Equal(45000, scene.Bodies.Count);
        Assert.Equal(10000, scene.ClothBodies.Count);
        Assert.Equal(35000, scene.Bodies.Count - scene.ClothBodies.Count);
        Query joints = world.Query(new QueryDescription().WithStored<JointConstraint>());
        int jointCount = joints.Count;
        
        Assert.Equal(72000, jointCount);
        float maximumDistance = scene.Bodies.Max(body => world.ReadComponentData<BodyPose>(body).Position.Length()) + 10;
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
            Query bodies = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>().WithStored<BodyPose>()
                .WithStored<BodyVelocity>().WithStored<BodyStepState>());
            using (QueryScope rows = bodies.OpenRead())
                foreach (QueryChunk chunk in rows)
                    for (int row = 0; row < chunk.Count; row++)
                    {
                        float position = chunk.Read<BodyPose>()[row].Position.Length(), speed = chunk.Read<BodyVelocity>()[row].Linear.Length();
                        Assert.True(float.IsFinite(position + speed));
                        Assert.Equal(largest.Number, chunk.Read<BodyStepState>()[row].Number);
                        largest.Position = MathF.Max(largest.Position, position); largest.Speed = MathF.Max(largest.Speed, speed);
                    }
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

