using System.Numerics;
using System.Diagnostics;
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
[Trait("Category", "Performance")]
public sealed class PhysicsCrowdSceneTests(ITestOutputHelper output)
{
    [Fact]
    public void CharactersAndTheirBoneClothCompleteTheSharedPhysicalFrame()
    {
        const int characters = 2000;
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)], requiredFeatures: DeviceFeatures.IndirectCommands));
        using var scheduler = new Scheduler(new() { WorkerCount = Environment.ProcessorCount });
        using World world = new World(execution: new WorldJobs(scheduler));
        long setup = Stopwatch.GetTimestamp();
        var timings = new PhysicsSceneTimings();
        var example = PhysicsCrowdExample.CreateDevice(characters, backend, device, timings);
        using var scene = example.CreateSimulation(world);
        output.WriteLine($"Characters={characters}; SetupMilliseconds={Stopwatch.GetElapsedTime(setup).TotalMilliseconds:R}; StageTimings=true; IndirectCommands={backend.TryGetCapability<IndirectCommands>(device, out _)}");
        var tip = scene.CharacterBodies[^1].Span[^1];
        Vector3 initial = world.ReadComponentData<BodyPose>(tip).Position;
        var elapsed = new List<double>(180);
        using var process = Process.GetCurrentProcess();

        long started = Stopwatch.GetTimestamp();
        scene.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult completed = scene.CompleteForRead();
        elapsed.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        output.WriteLine($"Characters={characters}; Step=0; WholeStepMilliseconds={elapsed[^1]:R}; ManagedBytes={GC.GetTotalMemory(false)}; Adapter={device.Adapter.Name}; DedicatedVideoMemoryBytes={device.Adapter.DedicatedVideoMemory}");
        ReportBodies(world, scene, completed.Number, initial.Length() + 10);

        Assert.Equal(characters, scene.CharacterTargets.Count);
        Assert.Equal(characters, scene.ClothTargets.Count);
        Assert.Equal(characters * 20 + 1, scene.Bodies.Count);
        Assert.Equal(characters * 16, scene.ClothBodies.Count);
        Assert.Equal((characters + 1) / 2, scene.WorldDrivers.Count);
        Assert.Equal(characters / 2, scene.ParentDrivers.Count);
        Assert.Equal(0, completed.Number);
        Assert.Equal(1d / 60, completed.EndTime, 12);
        Assert.Equal(completed.Number, world.ReadComponentData<BodyStepState>(tip).Number);
        Assert.Equal(completed.EndTime, world.ReadComponentData<BodyStepState>(tip).Time, 12);
        Assert.True(world.ReadComponentData<BodyPose>(tip).Position.Z > initial.Z + .001f);
        Assert.True(world.ReadComponentData<BodyVelocity>(tip).Linear.Z > .1f);
        using (DevicePoseOutputRead pose = GpuPoseAnimation.ReadDeviceOutput(world, scene.CharacterTargets[^1]))
        {
            Assert.Equal(completed.Number, pose.Output.Number);
            Assert.Equal(completed.EndTime, pose.Output.Time, 12);
        }
        for (int number = 1; number < 180; number++)
        {
            started = Stopwatch.GetTimestamp();
            scene.AdvanceTime(scene.StepSeconds);
            completed = scene.CompleteForRead();
            elapsed.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            output.WriteLine($"Characters={characters}; Step={number}; WholeStepMilliseconds={elapsed[^1]:R}; ManagedBytes={GC.GetTotalMemory(false)}");
            ReportBodies(world, scene, completed.Number, initial.Length() + 10);
            Assert.Equal(number, completed.Number);
            Assert.Equal((number + 1d) / 60, completed.EndTime, 12);
            Assert.Equal(completed.Number, world.ReadComponentData<BodyStepState>(tip).Number);
        }
        using (DevicePoseOutputRead pose = GpuPoseAnimation.ReadDeviceOutput(world, scene.CharacterTargets[^1]))
        {
            Assert.Equal(completed.Number, pose.Output.Number);
            Assert.Equal(completed.EndTime, pose.Output.Time, 12);
        }
        elapsed.Sort();
        process.Refresh();
        output.WriteLine($"Characters={characters}; CompletedSteps={elapsed.Count}; BudgetMilliseconds={1000d / 60:R}; WholeStepMeanMilliseconds={elapsed.Average():R}; WholeStepP95Milliseconds={elapsed[(int)Math.Ceiling(elapsed.Count * .95) - 1]:R}; WholeStepP99Milliseconds={elapsed[(int)Math.Ceiling(elapsed.Count * .99) - 1]:R}; WholeStepMaximumMilliseconds={elapsed[^1]:R}; ProcessPeakWorkingSetBytes={process.PeakWorkingSet64}");
        output.WriteLine(timings.Describe().ToJsonString());
    }

    private void ReportBodies(World world, PhysicsCrowdScene scene, long number, float maximumDistance)
    {
        Query bodies = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>().WithStored<BodyPose>().WithStored<BodyVelocity>());
        var largest = (Position: 0f, Speed: 0f, Body: -1);
        using (QueryScope rows = bodies.OpenRead())
            foreach (QueryChunk chunk in rows)
                for (int row = 0; row < chunk.Count; row++)
                {
                    float position = chunk.Read<BodyPose>()[row].Position.Length(), speed = chunk.Read<BodyVelocity>()[row].Linear.Length();
                    Assert.True(float.IsFinite(position + speed));
                    largest.Position = MathF.Max(largest.Position, position);
                    if (speed > largest.Speed) { largest.Speed = speed; largest.Body = chunk.Read<DeviceBodyBinding>()[row].Index; }
                }
        int? contactCount = scene.Contacts is { } contacts ? contacts.OpenView().PointCount : null;
        output.WriteLine($"Step={number}; MaximumBodyDistance={largest.Position:R}; MaximumBodySpeed={largest.Speed:R}; FastestBodyIndex={largest.Body}; CpuContactReadback={scene.Contacts is not null}; ContactPoints={contactCount}");
        Assert.InRange(largest.Position, 0, maximumDistance);
        Assert.InRange(largest.Speed, 0, 100);
    }

    [Fact]
    public void ACharacterCapsuleMovesAnotherCharactersBoneClothInTheirCompletedFrame()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)], requiredFeatures: DeviceFeatures.IndirectCommands));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(scheduler));
        using var scene = PhysicsCrowdExample.CreateDevice(2, backend, device).CreateSimulation(world, readContacts: true);
        Vector3 shift = new(-1.2f, -.2f, .11f);
        foreach (Entity target in new[] { scene.CharacterTargets[1], scene.ClothTargets[1] })
        {
            GpuPoseTarget input = world.ReadComponentData<GpuPoseTarget>(target);
            input.ModelToWorld.Translation += shift;
            world.Set(target, input);
        }
        foreach (Entity body in scene.CharacterBodies[1].ToArray().Concat(scene.ClothBodies.Skip(16)))
        {
            BodyPose pose = world.ReadComponentData<BodyPose>(body); pose.Position += shift; world.Set(body, pose);
        }
        Entity[] firstCloth = scene.ClothBodies.Take(16).ToArray();
        float[] initialZ = firstCloth.Select(body => world.ReadComponentData<BodyPose>(body).Position.Z).ToArray();

        scene.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult completed = scene.CompleteForRead();

        var pairs = new List<(Entity First, Entity Second)>();
        foreach (ContactPair pair in scene.Contacts!.OpenView().Pairs)
            if (pair.PointCount != 0) pairs.Add((pair.FirstGeometry, pair.SecondGeometry));
        Entity[] secondArm = scene.CharacterBodies[1].ToArray();
        Assert.Contains(pairs, pair => CrossesActors(pair.First, pair.Second) || CrossesActors(pair.Second, pair.First));
        Assert.Contains(Enumerable.Range(0, 16), index =>
            MathF.Abs(world.ReadComponentData<BodyPose>(firstCloth[index]).Position.Z - initialZ[index]) > .001f);
        using DevicePoseOutputRead output = GpuPoseAnimation.ReadDeviceOutput(world, scene.ClothTargets[0]);
        Assert.Equal(completed.Number, output.Output.Number);
        Assert.Equal(completed.EndTime, output.Output.Time, 12);

        bool CrossesActors(Entity capsule, Entity face) => world.Has<DynamicCapsule>(capsule) && world.Has<DynamicFace>(face)
            && secondArm.Contains(world.ReadComponentData<DynamicCapsule>(capsule).First.Body)
            && firstCloth.Contains(world.ReadComponentData<DynamicFace>(face).First.Body);
    }
}
