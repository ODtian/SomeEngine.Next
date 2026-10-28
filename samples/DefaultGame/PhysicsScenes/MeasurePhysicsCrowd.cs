using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using SomeEngine.Animation.Pose;
using SomeEngine.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal static class MeasurePhysicsCrowd
{
    internal static int Run(TextWriter output, IGraphicsBackend backend, Device device, PhysicsCrowdScale scale)
    {
        using var scheduler = new Scheduler(new() { WorkerCount = Environment.ProcessorCount,
            MaxResourceStates = PhysicsCrowdScale.MaxResourceStates });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var process = Process.GetCurrentProcess();
        PhysicsSceneTimings? timings = EngineTrace.IsEnabled ? new() : null;
        PhysicsCrowdExample example = scale.Create(backend, device, timings);
        long started = Stopwatch.GetTimestamp();
        using PhysicsCrowdScene scene = example.CreateSimulation(world);
        double setup = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        JsonObject workload = scale.Describe(example, scene, world);
        workload["Adapter"] = device.Adapter.Name;
        workload["StepCount"] = 180; workload["WarmupSteps"] = 0; workload["FirstStepIncluded"] = true;
        workload["Trace"] = EngineTrace.IsEnabled;
        workload["TieredCompilation"] = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation");
        workload["TieredPGO"] = Environment.GetEnvironmentVariable("DOTNET_TieredPGO");
        workload["JobConfiguration"] = "Scene scheduler selects the shared crowd resource-state capacity";
        workload["WholeStepScope"] = "AdvanceTime through CompleteForRead, including all body results, transforms and device bone outputs";
        workload["ObservationScope"] = "All bodies and every character and cloth DevicePoseOutput through its public read";
        workload["Events"] = new JsonArray((example.JointNet?.Events ?? []).Select(change => (JsonNode)new JsonObject
        {
            ["Step"] = change.Frame, ["Body"] = change.Body,
            ["VelocityIncrement"] = change.Velocity is null ? null
                : new JsonArray(change.Velocity.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        }).ToArray());
        using var observation = new ReadCrowdResults(world, scene, scale);
        output.WriteLine(new JsonObject { ["Workload"] = workload.DeepClone(), ["SetupMilliseconds"] = setup }.ToJsonString());
        output.Flush();
        var steps = new double[180];
        var reads = new double[steps.Length];
        PhysicsStepResult result = default;
        long managedPeak = 0;
        bool behavior = true;
        for (int number = 0; number < steps.Length; number++)
        {
            using (EngineTrace.BeginFrame(number + 1))
            {
                started = Stopwatch.GetTimestamp();
                scene.AdvanceTime(scene.StepSeconds);
                result = scene.CompleteForRead();
                steps[number] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            started = Stopwatch.GetTimestamp();
            behavior &= observation.Read(number, result);
            managedPeak = Math.Max(managedPeak, GC.GetTotalMemory(false));
            reads[number] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            output.WriteLine(new JsonObject { ["Step"] = result.Number, ["Time"] = result.EndTime,
                ["WholeStepMilliseconds"] = steps[number], ["ObservationMilliseconds"] = reads[number],
                ["BehaviorSatisfied"] = behavior, ["Results"] = observation.Describe() }.ToJsonString());
            output.Flush();
        }
        process.Refresh();
        bool withinBudget = steps.Max() <= 1000d / 60;
        output.WriteLine(new JsonObject
        {
            ["Workload"] = workload.DeepClone(), ["CompletedStep"] = result.Number, ["CompletedTime"] = result.EndTime,
            ["WithinBudget"] = withinBudget, ["BehaviorSatisfied"] = behavior, ["FirstStepMilliseconds"] = steps[0],
            ["WholeStep"] = PhysicsSceneTimings.Distribution(steps), ["Observation"] = PhysicsSceneTimings.Distribution(reads),
            ["Stages"] = timings?.Describe(), ["Results"] = observation.Describe(),
            ["ManagedLivePeakBytes"] = managedPeak, ["ProcessPeakWorkingSetBytes"] = process.PeakWorkingSet64,
        }.ToJsonString());
        output.WriteLine(new JsonObject { ["Scenes"] = 1, ["WithinBudget"] = withinBudget ? 1 : 0,
            ["BehaviorFailures"] = behavior ? 0 : 1 }.ToJsonString());
        output.Flush();
        return withinBudget && behavior ? 0 : 1;
    }

    private sealed class ReadCrowdResults : IDisposable
    {
        private readonly World _world;
        private readonly PhysicsCrowdScene _scene;
        private readonly QueryHandle _bodies, _outputs;
        private readonly Entity _tip;
        private readonly Vector3 _initialTip;
        private readonly float _distanceLimit;
        private readonly bool _checkImpulse;
        private long _number;
        private double _time;
        private int _bodyCount, _outputCount, _invalidBodies;
        private float _distance, _speed, _peakDistance, _peakSpeed;
        private ulong _modelBytes;
        private bool _clocks, _impulse = true;

        internal ReadCrowdResults(World world, PhysicsCrowdScene scene, PhysicsCrowdScale scale)
        {
            _world = world; _scene = scene; _checkImpulse = !scale.JointNet;
            _tip = scene.CharacterBodies[^1].Span[^1];
            _initialTip = world.Read<BodyPose>(_tip).Position;
            _distanceLimit = scene.Bodies.Max(body => world.Read<BodyPose>(body).Position.Length()) + 10;
            _bodies = world.Query(new QueryDefinitionBuilder().All<CrowdPhysicsBody>().Read<BodyPose>()
                .Read<BodyVelocity>().Read<BodyStepState>());
            _outputs = world.Query(new QueryDefinitionBuilder().All<CrowdPose>().Read<DevicePoseOutput>());
        }

        internal bool Read(long requested, PhysicsStepResult completed)
        {
            _number = completed.Number; _time = completed.EndTime;
            _clocks = _number == requested && SameTime(_time, (requested + 1d) / 60);
            _bodyCount = _outputCount = _invalidBodies = 0;
            _distance = _speed = 0; _modelBytes = 0;
            ReadCrowdResults state = this;
            _world.ExecuteReadQuery(_bodies, ref state,
                static (scoped QueryCursor rows, ref ReadCrowdResults read) => read.ReadBodies(rows));
            _world.ExecuteReadQuery(_outputs, ref state,
                static (scoped QueryCursor rows, ref ReadCrowdResults read) => read.ReadOutputs(rows));
            if (_checkImpulse && requested == 0)
                _impulse = _world.Read<BodyPose>(_tip).Position.Z > _initialTip.Z + .001f
                    && _world.Read<BodyVelocity>(_tip).Linear.Z > .1f;
            _peakDistance = MathF.Max(_peakDistance, _distance); _peakSpeed = MathF.Max(_peakSpeed, _speed);
            return _clocks && _impulse && _invalidBodies == 0 && _distance <= _distanceLimit && _speed <= 100
                && _bodyCount == _scene.Bodies.Count
                && _outputCount == _scene.CharacterTargets.Count + _scene.ClothTargets.Count;
        }

        private void ReadBodies(scoped QueryCursor rows)
        {
            var components = rows.ReadRows<BodyPose, BodyVelocity>();
            while (components.MoveNext(out QueryRow row, out var pose, out var velocity))
            {
                float position = pose.Value.Position.Length(), speed = velocity.Value.Linear.Length();
                if (float.IsFinite(position + speed))
                { _distance = MathF.Max(_distance, position); _speed = MathF.Max(_speed, speed); }
                else _invalidBodies++;
                ref readonly BodyStepState step = ref row.ReadRef<BodyStepState>();
                _clocks &= step.Number == _number;
                _bodyCount++;
            }
        }

        private void ReadOutputs(scoped QueryCursor rows)
        {
            foreach (QueryChunkView chunk in rows.Chunks)
            foreach (ref readonly DevicePoseOutput value in chunk.Read<DevicePoseOutput>())
            {
                using DevicePoseOutputRead read = GpuPoseAnimation.ReadDeviceOutput(_world, in value);
                _clocks &= read.Output.Number == _number && SameTime(read.Output.Time, _time);
                _modelBytes += read.Output.Models.Size;
                _outputCount++;
            }
        }
        private static bool SameTime(double first, double second) => Math.Round(first, 12) == Math.Round(second, 12);
        internal JsonObject Describe() => new()
        {
            ["BodyCount"] = _bodyCount, ["ClothBodies"] = _scene.ClothBodies.Count,
            ["PoseOutputs"] = _outputCount, ["ModelOutputBytes"] = _modelBytes,
            ["BodyAndPoseClocksMatch"] = _clocks, ["InitialImpulseSatisfied"] = _impulse,
            ["InvalidBodyCount"] = _invalidBodies,
            ["MaximumBodyDistance"] = _distance, ["MaximumBodySpeed"] = _speed,
            ["PeakBodyDistance"] = _peakDistance, ["PeakBodySpeed"] = _peakSpeed,
            ["BodyDistanceLimit"] = _distanceLimit, ["BodySpeedLimit"] = 100,
        };
        public void Dispose() { _world.ReleaseQuery(_outputs); _world.ReleaseQuery(_bodies); }
    }
}
