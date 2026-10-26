using System.Diagnostics;
using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit.Abstractions;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Trait("Category", "Performance")]
public sealed class MillionPhysicsActivityTests(ITestOutputHelper output)
{
    [Fact]
    public void AMillionDeviceBodiesAdvanceThroughTheSharedSceneFrame()
    {
        PhysicsSceneDescription description = PhysicsScaleScenes.All.Single(scene => scene.Id == "million-activity");
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)], label: "Million body activity"));
        using var scheduler = new Scheduler(new() { WorkerCount = Environment.ProcessorCount });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var timings = new PhysicsSceneTimings();
        long setup = Stopwatch.GetTimestamp();
        using PhysicsSceneSimulation scene = PhysicsScenes.CreateDevice(world, description, backend, device,
            new PhysicsSceneSettings(Iterations: 16, BodyIterations: 2, StepBudgetMilliseconds: 1000d / 60) { Timings = timings }, new(1, 16),
            new(Bodies: 1_000_001, Cells: 8_000_008, Buckets: 16_000_017, Pairs: 1_000_000, Bins: 1),
            contactCapacity: 4_000_000, contactDistance: .02f, readbackBodyIndices: [1, 500_001, 1_000_000],
            shaderDirectory: Path.Combine(AppContext.BaseDirectory, "PhysicsShaders"));
        output.WriteLine($"SetupMilliseconds={Stopwatch.GetElapsedTime(setup).TotalMilliseconds:R}; Adapter={device.Adapter.Name}");

        long started = Stopwatch.GetTimestamp();
        scene.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult completed = scene.CompleteForRead();
        using var process = Process.GetCurrentProcess();
        output.WriteLine($"WholeStepMilliseconds={Stopwatch.GetElapsedTime(started).TotalMilliseconds:R}; ProcessPeakWorkingSetBytes={process.PeakWorkingSet64}");
        output.WriteLine($"AwakeBodies={scene.DeviceAwakeBodies}; ContactPoints={scene.DeviceContactPoints}; LargestIslandBodies={scene.DeviceLargestIslandBodies}; ReadbackBodies={scene.ReadbackBodies.Length}");
        output.WriteLine("DiagnosticStages=" + timings.Describe().ToJsonString());

        Assert.Equal(1_000_001, scene.Bodies.Length);
        Assert.Equal(1_000_000, scene.DeviceAwakeBodies);
        Assert.Equal(0, scene.DeviceContactPoints);
        Assert.Equal(1, scene.DeviceLargestIslandBodies);
        Assert.Equal(0, completed.Number); Assert.Equal(1d / 60, completed.EndTime);
        Assert.InRange(world.Read<BodyPose>(scene.Bodies.Span[1]).Position.Z, .297221f, .297223f);
        Assert.InRange(world.Read<BodyPose>(scene.Bodies.Span[500_001]).Position.Z, 5.79721f, 5.79723f);
        Assert.InRange(world.Read<BodyPose>(scene.Bodies.Span[1_000_000]).Position.Z, 10.74721f, 10.74723f);
        foreach (int index in new[] { 1, 500_001, 1_000_000 })
            Assert.InRange(world.Read<BodyVelocity>(scene.Bodies.Span[index]).Linear.Z, -.166697f, -.166637f);
    }
}
