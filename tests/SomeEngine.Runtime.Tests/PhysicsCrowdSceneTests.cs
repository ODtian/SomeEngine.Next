using System.Numerics;
using System.Diagnostics;
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
[Trait("Category", "Performance")]
public sealed class PhysicsCrowdSceneTests(ITestOutputHelper output)
{
    [Fact]
    public void TwoThousandCharactersAndTheirBoneClothCompleteTheSharedPhysicalFrame()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = Environment.ProcessorCount });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        long setup = Stopwatch.GetTimestamp();
        var timings = new PhysicsSceneTimings();
        var example = PhysicsCrowdExample.CreateDevice(2000, backend, device, timings);
        using var scene = example.CreateSimulation(world);
        output.WriteLine($"Characters=2000; SetupMilliseconds={Stopwatch.GetElapsedTime(setup).TotalMilliseconds:R}; StageTimings=true");
        var tip = scene.CharacterBodies[^1].Span[^1];
        Vector3 initial = world.Read<BodyPose>(tip).Position;

        long started = Stopwatch.GetTimestamp();
        scene.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult completed = scene.CompleteForRead();
        output.WriteLine($"Characters=2000; Step=0; WholeStepMilliseconds={Stopwatch.GetElapsedTime(started).TotalMilliseconds:R}; ManagedBytes={GC.GetTotalMemory(false)}; Adapter={device.Adapter.Name}; DedicatedVideoMemoryBytes={device.Adapter.DedicatedVideoMemory}");
        ReportBodies(world, completed.Number);

        Assert.Equal(2000, scene.CharacterTargets.Count);
        Assert.Equal(2000, scene.ClothTargets.Count);
        Assert.Equal(40001, scene.Bodies.Count);
        Assert.Equal(32000, scene.ClothBodies.Count);
        Assert.Equal(1000, scene.WorldDrivers.Count);
        Assert.Equal(1000, scene.ParentDrivers.Count);
        Assert.Equal(0, completed.Number);
        Assert.Equal(1d / 60, completed.EndTime, 12);
        Assert.Equal(completed.Number, world.Read<BodyStepState>(tip).Number);
        Assert.Equal(completed.EndTime, world.Read<BodyStepState>(tip).Time, 12);
        Assert.True(world.Read<BodyPose>(tip).Position.Z > initial.Z + .001f);
        Assert.True(world.Read<BodyVelocity>(tip).Linear.Z > .1f);
        using (DevicePoseOutputRead pose = GpuPoseAnimation.ReadDeviceOutput(world, scene.CharacterTargets[^1]))
        {
            Assert.Equal(completed.Number, pose.Output.Number);
            Assert.Equal(completed.EndTime, pose.Output.Time, 12);
        }
        for (int number = 1; number < 3; number++)
        {
            started = Stopwatch.GetTimestamp();
            scene.AdvanceTime(scene.StepSeconds);
            completed = scene.CompleteForRead();
            output.WriteLine($"Characters=2000; Step={number}; WholeStepMilliseconds={Stopwatch.GetElapsedTime(started).TotalMilliseconds:R}; ManagedBytes={GC.GetTotalMemory(false)}");
            ReportBodies(world, completed.Number);
            Assert.Equal(number, completed.Number);
            Assert.Equal((number + 1d) / 60, completed.EndTime, 12);
            Assert.Equal(completed.Number, world.Read<BodyStepState>(tip).Number);
        }
        output.WriteLine(timings.Describe().ToJsonString());
    }

    private void ReportBodies(World world, long number)
    {
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().Read<BodyPose>().Read<BodyVelocity>());
        var largest = (Position: 0f, Speed: 0f, Body: -1);
        world.ExecuteReadQuery(bodies, ref largest, static (scoped QueryCursor rows, ref (float Position, float Speed, int Body) largest) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                float position = row.Read<BodyPose>().Position.Length(), speed = row.Read<BodyVelocity>().Linear.Length();
                Assert.True(float.IsFinite(position + speed));
                largest.Position = MathF.Max(largest.Position, position);
                if (speed > largest.Speed) { largest.Speed = speed; largest.Body = row.Read<DeviceBodyBinding>().Index; }
            }
        });
        world.ReleaseQuery(bodies);
        output.WriteLine($"Step={number}; MaximumBodyDistance={largest.Position:R}; MaximumBodySpeed={largest.Speed:R}; FastestBodyIndex={largest.Body}");
        Assert.InRange(largest.Position, 0, 100);
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
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var scene = PhysicsCrowdExample.CreateDevice(2, backend, device).CreateSimulation(world);
        Vector3 shift = new(-1.2f, -.2f, .11f);
        foreach (Entity target in new[] { scene.CharacterTargets[1], scene.ClothTargets[1] })
        {
            GpuPoseTarget input = world.Read<GpuPoseTarget>(target);
            input.ModelToWorld.Translation += shift;
            world.Replace(target, input);
        }
        foreach (Entity body in scene.CharacterBodies[1].ToArray().Concat(scene.ClothBodies.Skip(16)))
        {
            BodyPose pose = world.Read<BodyPose>(body); pose.Position += shift; world.Replace(body, pose);
        }
        Entity[] firstCloth = scene.ClothBodies.Take(16).ToArray();
        float[] initialZ = firstCloth.Select(body => world.Read<BodyPose>(body).Position.Z).ToArray();

        scene.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult completed = scene.CompleteForRead();

        var pairs = new List<(Entity First, Entity Second)>();
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>()
            .Read<UndirectedRelationEndpoints<ContactPoint>>());
        world.ExecuteReadQuery(contacts, ref pairs, static (scoped QueryCursor rows, ref List<(Entity First, Entity Second)> pairs) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                var ends = row.Read<UndirectedRelationEndpoints<ContactPoint>>();
                pairs.Add((ends.EndpointA, ends.EndpointB));
            }
        });
        world.ReleaseQuery(contacts);
        Entity[] secondArm = scene.CharacterBodies[1].ToArray();
        Assert.Contains(pairs, pair => CrossesActors(pair.First, pair.Second) || CrossesActors(pair.Second, pair.First));
        Assert.Contains(Enumerable.Range(0, 16), index =>
            MathF.Abs(world.Read<BodyPose>(firstCloth[index]).Position.Z - initialZ[index]) > .001f);
        using DevicePoseOutputRead output = GpuPoseAnimation.ReadDeviceOutput(world, scene.ClothTargets[0]);
        Assert.Equal(completed.Number, output.Output.Number);
        Assert.Equal(completed.EndTime, output.Output.Time, 12);

        bool CrossesActors(Entity capsule, Entity face) => world.Has<DynamicCapsule>(capsule) && world.Has<DynamicFace>(face)
            && secondArm.Contains(world.Read<DynamicCapsule>(capsule).First.Body)
            && firstCloth.Contains(world.Read<DynamicFace>(face).First.Body);
    }
}
