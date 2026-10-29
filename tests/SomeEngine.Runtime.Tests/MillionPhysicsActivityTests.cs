using System.Diagnostics;
using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
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
    public void TheMillionExampleDeliversTheCompletedMotionOfEveryBody()
    {
        PhysicsSceneDescription description = PhysicsScaleScenes.All.Single(scene => scene.Id == "million-activity");
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)], label: "Million body scene consumer"));
        using var scheduler = new Scheduler(new() { WorkerCount = Environment.ProcessorCount });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneExample example = PhysicsSceneExample.CreateDevice(description, backend, device);
        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"MillionStage=Create; Workers={Environment.ProcessorCount}; ManagedBytes={GC.GetTotalMemory(false)}; WorkingSetBytes={process.WorkingSet64}");
        long setup = Stopwatch.GetTimestamp();
        using PhysicsSceneSimulation scene = example.CreateSimulation(world);
        double setupMilliseconds = Stopwatch.GetElapsedTime(setup).TotalMilliseconds;
        process.Refresh();
        Console.WriteLine($"MillionStage=Created; SetupMilliseconds={setupMilliseconds:R}; Bodies={scene.Bodies.Length}; ReadbackBodies={scene.ReadbackBodies.Length}; ManagedBytes={GC.GetTotalMemory(false)}; WorkingSetBytes={process.WorkingSet64}");
        output.WriteLine($"SetupMilliseconds={setupMilliseconds:R}; Adapter={device.Adapter.Name}; DedicatedVideoMemoryBytes={device.Adapter.DedicatedVideoMemory}");
        output.WriteLine("Workload=" + example.Describe().ToJsonString());
        Assert.Equal(481, example.StepCount);
        Assert.Equal(1_000_001, scene.ReadbackBodies.Length);
        Assert.True(scene.ReadbackBodies.Span.SequenceEqual(scene.Bodies.Span));

        Console.WriteLine("MillionStage=BeginFrame");
        long started = Stopwatch.GetTimestamp();
        scene.AdvanceTime(scene.StepSeconds * 1.5);
        PhysicsStepResult completed = scene.CompleteForRead();
        double wholeMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        process.Refresh();
        Console.WriteLine($"MillionStage=Completed; Number={completed.Number}; Time={completed.EndTime:R}; WholeStepMilliseconds={wholeMilliseconds:R}; AwakeBodies={scene.DeviceAwakeBodies}; ContactPoints={scene.DeviceContactPoints}; LargestIslandBodies={scene.DeviceLargestIslandBodies}; ManagedBytes={GC.GetTotalMemory(false)}; WorkingSetBytes={process.WorkingSet64}; PeakWorkingSetBytes={process.PeakWorkingSet64}");
        output.WriteLine($"WholeStepMilliseconds={wholeMilliseconds:R}; Consumer=AllSceneBodies; StageTimings=false; ProcessPeakWorkingSetBytes={process.PeakWorkingSet64}");

        Console.WriteLine("MillionStage=Verify");
        long verify = Stopwatch.GetTimestamp();
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().Read<BodyMass>()
            .Read<BodyPose>().Read<BodyVelocity>());
        int delivered = 0;
        world.ExecuteReadQuery(bodies, ref delivered, static (scoped QueryCursor rows, ref int moved) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                if (row.ReadRef<BodyMass>().Motion != BodyMotion.Dynamic) continue;
                int layer = (row.ReadRef<DeviceBodyBinding>().Index - 1) / 50_000;
                // The source layer starts at .3 + .55 * layer; one h=1/60 step under -10 moves it by -1/360.
                float height = (float)(.3 + .55 * layer - 1d / 360);
                float position = row.ReadRef<BodyPose>().Position.Z;
                float velocity = row.ReadRef<BodyVelocity>().Linear.Z;
                if (MathF.Abs(position - height) <= 1e-5f && velocity is >= -.166697f and <= -.166637f)
                    moved++;
            }
        });
        world.ReleaseQuery(bodies);
        Console.WriteLine($"MillionStage=Verified; DeliveredBodies={delivered}; VerificationMilliseconds={Stopwatch.GetElapsedTime(verify).TotalMilliseconds:R}");

        Assert.Equal(1_000_000, delivered);
        Assert.Equal(0, completed.Number); Assert.Equal(1d / 60, completed.EndTime);
        Assert.Equal(1_000_000, scene.DeviceAwakeBodies);
        Assert.Equal(0, scene.DeviceContactPoints);
        Assert.Equal(1, scene.DeviceLargestIslandBodies);
    }

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
            new PhysicsSceneSettings(Iterations: 16, BodyIterations: 2, StepBudgetMilliseconds: 1000d / 60)
            { Timings = timings, GeometryInputsFixed = true, EquationBindingsFixed = true }, new(1, 16),
            new(Bodies: 1_000_001, Cells: 8_000_008, Buckets: 16_000_017, Pairs: 1_000_000, Bins: 1),
            contactCapacity: 4_000_000, contactDistance: .02f, readbackBodyIndices: [1, 500_001, 1_000_000],
            shaderDirectory: Path.Combine(AppContext.BaseDirectory, "PhysicsShaders"));
        output.WriteLine($"SetupMilliseconds={Stopwatch.GetElapsedTime(setup).TotalMilliseconds:R}; Adapter={device.Adapter.Name}; DedicatedVideoMemoryBytes={device.Adapter.DedicatedVideoMemory}");

        TimeSpan pause = GC.GetTotalPauseDuration();
        int collections = GC.CollectionCount(2);
        long started = Stopwatch.GetTimestamp();
        scene.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult completed = scene.CompleteForRead();
        using var process = Process.GetCurrentProcess();
        output.WriteLine($"WholeStepMilliseconds={Stopwatch.GetElapsedTime(started).TotalMilliseconds:R}; ProcessPeakWorkingSetBytes={process.PeakWorkingSet64}");
        output.WriteLine($"GarbageCollectionPauseMilliseconds={(GC.GetTotalPauseDuration() - pause).TotalMilliseconds:R}; Generation2Collections={GC.CollectionCount(2) - collections}");
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

        pause = GC.GetTotalPauseDuration(); collections = GC.CollectionCount(2);
        started = Stopwatch.GetTimestamp();
        scene.AdvanceTime(scene.StepSeconds);
        completed = scene.CompleteForRead();
        output.WriteLine($"SecondStepMilliseconds={Stopwatch.GetElapsedTime(started).TotalMilliseconds:R}; GarbageCollectionPauseMilliseconds={(GC.GetTotalPauseDuration() - pause).TotalMilliseconds:R}; Generation2Collections={GC.CollectionCount(2) - collections}");
        process.Refresh(); output.WriteLine($"FinalProcessPeakWorkingSetBytes={process.PeakWorkingSet64}");
        output.WriteLine("CumulativeDiagnosticStages=" + timings.Describe().ToJsonString());
        Assert.Equal(1, completed.Number); Assert.Equal(2d / 60, completed.EndTime);
        Assert.Equal(1_000_000, scene.DeviceAwakeBodies); Assert.Equal(0, scene.DeviceContactPoints);
        Assert.Equal(1, scene.DeviceLargestIslandBodies);
        Assert.InRange(world.Read<BodyPose>(scene.Bodies.Span[1]).Position.Z, .291665f, .291669f);
        Assert.InRange(world.Read<BodyPose>(scene.Bodies.Span[500_001]).Position.Z, 5.79165f, 5.79169f);
        Assert.InRange(world.Read<BodyPose>(scene.Bodies.Span[1_000_000]).Position.Z, 10.74165f, 10.74169f);
        // At height 10.75, two float position differences divided by h carry at most 6e-5 velocity error.
        foreach (int index in new[] { 1, 500_001, 1_000_000 })
            Assert.InRange(world.Read<BodyVelocity>(scene.Bodies.Span[index]).Linear.Z, -.333393f, -.333273f);

        pause = GC.GetTotalPauseDuration(); collections = GC.CollectionCount(2);
        started = Stopwatch.GetTimestamp();
        scene.AdvanceTime(scene.StepSeconds);
        completed = scene.CompleteForRead();
        output.WriteLine($"ThirdStepMilliseconds={Stopwatch.GetElapsedTime(started).TotalMilliseconds:R}; GarbageCollectionPauseMilliseconds={(GC.GetTotalPauseDuration() - pause).TotalMilliseconds:R}; Generation2Collections={GC.CollectionCount(2) - collections}");
        output.WriteLine("ThirdDiagnosticStages=" + timings.Describe().ToJsonString());
        Assert.Equal(2, completed.Number); Assert.Equal(3d / 60, completed.EndTime);
        Assert.Equal(1_000_000, scene.DeviceAwakeBodies); Assert.Equal(0, scene.DeviceContactPoints);
        Assert.Equal(1, scene.DeviceLargestIslandBodies);
        Assert.InRange(world.Read<BodyPose>(scene.Bodies.Span[1]).Position.Z, .283331f, .283336f);
        Assert.InRange(world.Read<BodyPose>(scene.Bodies.Span[500_001]).Position.Z, 5.78331f, 5.78336f);
        Assert.InRange(world.Read<BodyPose>(scene.Bodies.Span[1_000_000]).Position.Z, 10.73331f, 10.73336f);
        foreach (int index in new[] { 1, 500_001, 1_000_000 })
            Assert.InRange(world.Read<BodyVelocity>(scene.Bodies.Span[index]).Linear.Z, -.50006f, -.49994f);

        for (int step = 3; step < 6; step++)
        {
            WorldStructuralMetrics structure = world.GetStructuralMetrics();
            pause = GC.GetTotalPauseDuration(); collections = GC.CollectionCount(2);
            started = Stopwatch.GetTimestamp();
            scene.AdvanceTime(scene.StepSeconds);
            completed = scene.CompleteForRead();
            output.WriteLine($"ContactStep={completed.Number}; WholeStepMilliseconds={Stopwatch.GetElapsedTime(started).TotalMilliseconds:R}; GarbageCollectionPauseMilliseconds={(GC.GetTotalPauseDuration() - pause).TotalMilliseconds:R}; Generation2Collections={GC.CollectionCount(2) - collections}; AwakeBodies={scene.DeviceAwakeBodies}; ContactPoints={scene.DeviceContactPoints}; LargestIslandBodies={scene.DeviceLargestIslandBodies}");
            WorldStructuralMetrics current = world.GetStructuralMetrics();
            output.WriteLine($"ContactStructureStep={completed.Number}; Transactions={current.Published - structure.Published}; PrepareMilliseconds={(current.PrepareTime - structure.PrepareTime).TotalMilliseconds:R}; CommitMilliseconds={(current.CommitTime - structure.CommitTime).TotalMilliseconds:R}; LifetimeMilliseconds={(current.Lifetime - structure.Lifetime).TotalMilliseconds:R}");
        }
        process.Refresh(); output.WriteLine($"ContactProcessPeakWorkingSetBytes={process.PeakWorkingSet64}");
        output.WriteLine("ContactDiagnosticStages=" + timings.Describe().ToJsonString());
        Assert.Equal(5, completed.Number); Assert.Equal(.1, completed.EndTime, 12);
        Assert.Equal(1_000_000, scene.DeviceAwakeBodies);
        Assert.Equal(200_000, scene.DeviceContactPoints);
        Assert.Equal(1, scene.DeviceLargestIslandBodies);
        Assert.InRange(world.Read<BodyPose>(scene.Bodies.Span[1]).Position.Z, .249f, .251f);
        Assert.InRange(world.Read<BodyPose>(scene.Bodies.Span[500_001]).Position.Z, 5.74164f, 5.74170f);
        Assert.InRange(world.Read<BodyPose>(scene.Bodies.Span[1_000_000]).Position.Z, 10.69164f, 10.69170f);
        // Six float position differences at height 10.75 carry at most 6 * 0.5 ULP / h = 1.72e-4 velocity error.
        Assert.InRange(world.Read<BodyVelocity>(scene.Bodies.Span[500_001]).Linear.Z, -1.00018f, -.99982f);
        Assert.InRange(world.Read<BodyVelocity>(scene.Bodies.Span[1_000_000]).Linear.Z, -1.00018f, -.99982f);
    }
}
