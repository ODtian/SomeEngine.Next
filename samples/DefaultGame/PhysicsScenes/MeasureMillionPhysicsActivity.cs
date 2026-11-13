using System.Diagnostics;
using System.Text.Json.Nodes;
using SomeEngine.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

/// <summary>The original million-body six-step workload with its three selected body results.</summary>
public static class MeasureMillionPhysicsActivity
{
    public static int Run(TextWriter output, IGraphicsBackend backend, Device device)
    {
        using var scheduler = new Scheduler(new() { WorkerCount = Environment.ProcessorCount });
        using World world = new World(execution: new WorldJobs(scheduler));
            world.StructureTimings.Enabled = true;
        using var process = Process.GetCurrentProcess();
        PhysicsSceneDescription description = PhysicsScaleScenes.All.Single(scene => scene.Id == "million-activity");
        PhysicsSceneTimings? timings = EngineTrace.IsEnabled ? new() : null;
        var settings = new PhysicsSceneSettings(16, 2, 1000d / 60)
        { Timings = timings, GeometryInputsFixed = true, EquationBindingsFixed = true };
        output.WriteLine(new JsonObject
        {
            ["Id"] = "million-activity-six-steps", ["Adapter"] = device.Adapter.Name,
            ["Workers"] = Environment.ProcessorCount, ["Framework"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
#if DEBUG
            ["BuildConfiguration"] = "Debug",
#else
            ["BuildConfiguration"] = "Release",
#endif
            ["StepCount"] = 6, ["WarmupSteps"] = 0, ["FirstStepIncluded"] = true,
            ["WorldStructureTimingsEnabled"] = true,
            ["WorldStructureTimingScope"] = "Actual structure writes include internal membership work; category times are independent",
            ["Bodies"] = 1_000_001, ["ReadbackBodyIndices"] = new JsonArray(1, 500_001, 1_000_000),
            ["StepSeconds"] = 1d / 60, ["Iterations"] = 16, ["BodyIterations"] = 2,
            ["ContactCapacity"] = 4_000_000, ["ContactDistance"] = .02f,
            ["Trace"] = EngineTrace.IsEnabled, ["StageTimingJobs"] = timings is not null,
            ["TieredCompilation"] = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            ["TieredPGO"] = Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
            ["WholeStepScope"] = "AdvanceTime through CompleteForRead with the original three selected body results",
        }.ToJsonString());
        output.Flush();
        long started = Stopwatch.GetTimestamp();
        using PhysicsSceneSimulation scene = PhysicsScenes.CreateDevice(world, description, backend, device,
            settings, new(1, 16),
            new(Bodies: 1_000_001, Cells: 8_000_008, Buckets: 16_000_017, Pairs: 1_000_000, Bins: 1),
            contactCapacity: 4_000_000, contactDistance: .02f, readbackBodyIndices: [1, 500_001, 1_000_000],
            shaderDirectory: Path.Combine(AppContext.BaseDirectory, "PhysicsShaders"));
        double setupMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        output.WriteLine(new JsonObject { ["SetupMilliseconds"] = setupMilliseconds }.ToJsonString());
        output.Flush();
        var steps = new double[6];
        bool behavior = true;
        PhysicsStepResult result = default;
        for (int number = 0; number < steps.Length; number++)
        {
            int entitiesBefore = world.EntityCount;
            WorldStructureTimes structureBefore = world.StructureTimings.Read();
            using (EngineTrace.BeginFrame(number + 1))
            {
                started = Stopwatch.GetTimestamp();
                scene.AdvanceTime(scene.StepSeconds);
                result = scene.CompleteForRead();
                steps[number] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            WorldStructureTimes structureAfter = world.StructureTimings.Read();
            if (timings is null && steps[number] > settings.StepBudgetMilliseconds)
            {
                output.WriteLine(new JsonObject
                {
                    ["Id"] = "million-activity-six-steps", ["Step"] = result.Number, ["Time"] = result.EndTime,
                    ["WholeStepMilliseconds"] = steps[number],
                ["WorldStructure"] = PhysicsSceneTimings.StructureChanges(in structureBefore, in structureAfter),
                    ["StepBudgetMilliseconds"] = settings.StepBudgetMilliseconds,
                    ["WithinBudget"] = false, ["StopReason"] = "StepBudgetExceeded",
                }.ToJsonString());
                output.Flush();
                Environment.Exit(1);
            }
            int entitiesAfter = world.EntityCount;
            bool stepBehavior = result.Number == number && Math.Round(result.EndTime, 12) == Math.Round((number + 1d) / 60, 12)
                && scene.Bodies.Length == 1_000_001 && scene.DeviceAwakeBodies == 1_000_000
                && scene.DeviceLargestIslandBodies == 1;
            var bodies = new JsonArray();
            int[] indices = [1, 500_001, 1_000_000];
            for (int selected = 0; selected < indices.Length; selected++)
            {
                float height = world.ReadComponentData<BodyPose>(scene.Bodies.Span[indices[selected]]).Position.Z;
                float velocity = world.ReadComponentData<BodyVelocity>(scene.Bodies.Span[indices[selected]]).Linear.Z;
                bodies.Add(new JsonObject { ["Index"] = indices[selected], ["Height"] = height, ["Velocity"] = velocity });
                if (number < 3)
                {
                    (float minimum, float maximum) = (number, selected) switch
                    {
                        (0, 0) => (.297221f, .297223f), (0, 1) => (5.79721f, 5.79723f), (0, 2) => (10.74721f, 10.74723f),
                        (1, 0) => (.291665f, .291669f), (1, 1) => (5.79165f, 5.79169f), (1, 2) => (10.74165f, 10.74169f),
                        (2, 0) => (.283331f, .283336f), (2, 1) => (5.78331f, 5.78336f), _ => (10.73331f, 10.73336f),
                    };
                    (float minimumSpeed, float maximumSpeed) = number switch
                    { 0 => (-.166697f, -.166637f), 1 => (-.333393f, -.333273f), _ => (-.50006f, -.49994f) };
                    stepBehavior &= height >= minimum && height <= maximum && velocity >= minimumSpeed && velocity <= maximumSpeed;
                }
                if (number == 5)
                {
                    (float minimum, float maximum) = selected switch
                    { 0 => (.249f, .251f), 1 => (5.74164f, 5.74170f), _ => (10.69164f, 10.69170f) };
                    stepBehavior &= height >= minimum && height <= maximum;
                    if (selected > 0) stepBehavior &= velocity >= -1.00018f && velocity <= -.99982f;
                }
            }
            if (number < 3) stepBehavior &= scene.DeviceContactPoints == 0;
            if (number == 5) stepBehavior &= scene.DeviceContactPoints == 200_000;
            stepBehavior &= entitiesBefore == entitiesAfter;
            behavior &= stepBehavior;
            process.Refresh();
            output.WriteLine(new JsonObject
            {
                ["Step"] = result.Number, ["Time"] = result.EndTime, ["WholeStepMilliseconds"] = steps[number],
                ["WorldStructure"] = PhysicsSceneTimings.StructureChanges(in structureBefore, in structureAfter),
                ["BehaviorSatisfied"] = stepBehavior, ["AwakeBodies"] = scene.DeviceAwakeBodies,
                ["ContactPoints"] = scene.DeviceContactPoints, ["LargestIslandBodies"] = scene.DeviceLargestIslandBodies,
                ["Bodies"] = bodies, ["EntitiesBefore"] = entitiesBefore, ["EntitiesAfter"] = entitiesAfter,
                ["ProcessPeakWorkingSetBytes"] = process.PeakWorkingSet64,
            }.ToJsonString());
            output.Flush();
        }
        bool withinBudget = steps.Max() <= settings.StepBudgetMilliseconds;
        output.WriteLine(new JsonObject
        {
            ["CompletedStep"] = result.Number, ["CompletedTime"] = result.EndTime,
            ["BehaviorSatisfied"] = behavior, ["WithinBudget"] = withinBudget,
            ["SetupMilliseconds"] = setupMilliseconds, ["WholeStep"] = PhysicsSceneTimings.Distribution(steps),
            ["Stages"] = timings?.Describe(), ["ProcessPeakWorkingSetBytes"] = process.PeakWorkingSet64,
        }.ToJsonString());
        output.Flush();
        return behavior && withinBudget ? 0 : 1;
    }
}
