using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Diagnostics;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

/// <summary>Runs the same scene factory and completed frame as the interactive platform, for every original input step.</summary>
public static class MeasurePhysicsScenes
{
    public static int Run(TextWriter output, string? id = null) => Run(output, id, null, null);

    public static int RunDevice(TextWriter output, IGraphicsBackend backend, Device device, string? id = null) =>
        Run(output, id, backend, device);

    private static int Run(TextWriter output, string? id, IGraphicsBackend? backend, Device? device)
    {
        PhysicsSceneDescription[] descriptions = id is null ? PhysicsScenes.All.ToArray()
            : [PhysicsScenes.All.Concat(PhysicsScaleScenes.All).Single(description => description.Id == id)];
        int failures = 0;
        long measuredFrame = 0;
        using var process = Process.GetCurrentProcess();
        using var scheduler = new Scheduler(new() { WorkerCount = Environment.ProcessorCount });
        output.WriteLine(new JsonObject
        {
            ["Device"] = backend is null ? "CPU" : "GPU", ["Workers"] = Environment.ProcessorCount, ["Framework"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["Architecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["Processor"] = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            ["DynamicCodeCompiled"] = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled,
#if DEBUG
            ["BuildConfiguration"] = "Debug",
#else
            ["BuildConfiguration"] = "Release",
#endif
            ["TieredCompilation"] = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            ["TieredPGO"] = Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
            ["Trace"] = EngineTrace.IsEnabled,
            ["WarmupSteps"] = 0, ["FirstStepIncluded"] = true,
            ["WholeStepScope"] = "AdvanceTime through CompleteForRead, including transforms and completed event consumption",
            ["StageScopes"] = "Caller submission and dependency-to-completion intervals are reported separately",
            ["StageTimingJobs"] = "Trace runs add a start Job and a finish Job per stage; ordinary runs use timings=null",
            ["MemoryScope"] = "Managed live bytes sampled after each completed step; process peak working set includes setup and prior scenes in this process",
        }.ToJsonString());
        foreach (PhysicsSceneDescription description in descriptions)
        {
            using World world = WorldJobs.CreateWorld(scheduler: scheduler);
            PhysicsSceneTimings? timings = EngineTrace.IsEnabled ? new PhysicsSceneTimings() : null;
            PhysicsSceneExample example = backend is null ? PhysicsSceneExample.CreateCpu(description, timings)
                : PhysicsSceneExample.CreateDevice(description, backend, device!, timings);
            using PhysicsSceneSimulation scene = example.CreateSimulation(world);
            using var observation = new ReadScenePhysicsResults(world, scene);
            var steps = new double[example.StepCount];
            var reads = new double[steps.Length];
            long managedPeak = 0;
            PhysicsStepResult result = default;
            for (int number = 0; number < steps.Length; number++)
            {
                long started;
                using (EngineTrace.BeginFrame(++measuredFrame))
                {
                    started = Stopwatch.GetTimestamp();
                    scene.Frame.AdvanceTime(scene.StepSeconds * (number == 0 ? 1.5 : 1));
                    result = scene.CompleteForRead();
                    steps[number] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
                if (result.Number != number) throw new InvalidOperationException("The scene must complete each original physical step in sequence.");
                started = Stopwatch.GetTimestamp();
                observation.Read();
                managedPeak = Math.Max(managedPeak, GC.GetTotalMemory(false));
                reads[number] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            process.Refresh();
            bool withinBudget = steps.Max() <= scene.Settings.StepBudgetMilliseconds;
            if (!withinBudget) failures++;
            output.WriteLine(new JsonObject
            {
                ["Workload"] = example.Describe().DeepClone(), ["CompletedStep"] = result.Number, ["CompletedTime"] = result.EndTime,
                ["ClockLeadSeconds"] = scene.StepSeconds * .5, ["WithinBudget"] = withinBudget,
                ["FirstStepMilliseconds"] = steps[0],
                ["WholeStep"] = PhysicsSceneTimings.Distribution(steps), ["Observation"] = PhysicsSceneTimings.Distribution(reads),
                ["Stages"] = timings?.Describe(), ["Results"] = observation.Describe(),
                ["ManagedLivePeakBytes"] = managedPeak, ["ProcessPeakWorkingSetBytes"] = process.PeakWorkingSet64,
            }.ToJsonString());
        }
        output.WriteLine(new JsonObject { ["Scenes"] = descriptions.Length, ["WithinBudget"] = descriptions.Length - failures }.ToJsonString());
        return failures == 0 ? 0 : 1;
    }
}

internal sealed class ReadScenePhysicsResults : IDisposable
{
    private readonly World _world;
    private readonly PhysicsSceneSimulation _scene;
    private readonly QueryHandle _bodies, _contacts;
    private readonly HashSet<Entity>? _readbackBodies;
    private int _entitiesNow, _entityPeak, _contactsNow, _contactPeak, _awakeNow, _awakePeak, _liveJoints, _islandPeak;
    private float _jointGap, _angularGap, _rangeGap, _penetration, _speed, _finalSpeed, _quaternionError, _dynamicMass;
    private float _angularSpeed, _finalAngularSpeed;
    private Vector3 _centerOfMass, _linearMomentum, _minimumBodyCenter, _maximumBodyCenter;

    public ReadScenePhysicsResults(World world, PhysicsSceneSimulation scene)
    {
        _world = world; _scene = scene;
        var bodyResults = new QueryDefinitionBuilder().All<ScenePhysicsBody>().Read<BodyPose>().Read<BodyMass>()
            .Read<BodyVelocity>().Read<BodyActivity>();
        if (scene.DeviceStatistics is not null) bodyResults.All<SceneReadbackBody>();
        _bodies = world.Query(bodyResults);
        if (scene.ReadbackBodies.Length < scene.Bodies.Length) _readbackBodies = scene.ReadbackBodies.ToArray().ToHashSet();
        _contacts = world.Query(new QueryDefinitionBuilder().Any<ScenePhysicsContact>().Any<ScenePhysicsBody>().Any<ContactParticipant>()
            .Optional<ContactPoint>(QueryAccess.Read).Optional<BodyPose>(QueryAccess.Read)
            .Optional<SomeEngine.ECS.Relations.DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .OptionalBuffer<ContactBodyLink>(QueryAccess.Read));
    }

    public void Read()
    {
        _entitiesNow = _world.EntityCount;
        _entityPeak = Math.Max(_entityPeak, _entitiesNow);
        _awakeNow = _contactsNow = _liveJoints = 0;
        _finalSpeed = _finalAngularSpeed = _dynamicMass = 0;
        _centerOfMass = _linearMomentum = default;
        _minimumBodyCenter = new(float.PositiveInfinity);
        _maximumBodyCenter = new(float.NegativeInfinity);
        _world.ExecuteQuery(_bodies, rows =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                ref readonly BodyPose pose = ref row.ReadRef<BodyPose>();
                ref readonly BodyVelocity velocity = ref row.ReadRef<BodyVelocity>();
                ref readonly BodyMass mass = ref row.ReadRef<BodyMass>();
                if (!float.IsFinite(pose.Position.LengthSquared() + pose.Rotation.LengthSquared()
                    + velocity.Linear.LengthSquared() + velocity.Angular.LengthSquared()))
                    throw new InvalidOperationException("The completed scene body pose and velocity must be finite.");
                if (mass.Motion == BodyMotion.Dynamic)
                {
                    if (!row.ReadRef<BodyActivity>().Sleeping) _awakeNow++;
                    float value = 1 / mass.InverseMass;
                    Vector3 center = pose.GetCenterOfMass(in mass);
                    _dynamicMass += value;
                    _centerOfMass += center * value;
                    _linearMomentum += velocity.Linear * value;
                    _minimumBodyCenter = Vector3.Min(_minimumBodyCenter, center);
                    _maximumBodyCenter = Vector3.Max(_maximumBodyCenter, center);
                }
                _finalSpeed = MathF.Max(_finalSpeed, velocity.Linear.Length());
                _speed = MathF.Max(_speed, velocity.Linear.Length());
                _finalAngularSpeed = MathF.Max(_finalAngularSpeed, velocity.Angular.Length());
                _angularSpeed = MathF.Max(_angularSpeed, velocity.Angular.Length());
                _quaternionError = MathF.Max(_quaternionError, MathF.Abs(pose.Rotation.LengthSquared() - 1));
            }
        });
        _centerOfMass /= _dynamicMass;
        if (_scene.DeviceStatistics is null) _world.ExecuteQuery(_contacts, rows =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                if (!row.TryReadRef<ContactPoint>(out var geometry)) continue;
                _contactsNow++;
                ref readonly ContactPoint point = ref geometry.Value;
                ReadOnlySpan<ContactBodyLink> links = row.ReadBuffer<ContactBodyLink>().AsSpan();
                Vector3 delta = default;
                for (int index = 0; index < links.Length; index++)
                {
                    ref readonly ContactBodyLink link = ref links[index];
                    rows.TryGetRow(link.Participant.Entity, out QueryRow participant);
                    Entity body = participant.ReadRef<SomeEngine.ECS.Relations.DirectedRelationEndpoints<ContactParticipant>>().Target;
                    rows.TryGetRow(body, out QueryRow bodyRow);
                    ref readonly BodyPose pose = ref bodyRow.ReadRef<BodyPose>();
                    Vector3 anchor = pose.Position + Vector3.Transform(link.LocalPoint, pose.Rotation);
                    delta += anchor * (link.Weight * (index < point.FirstBodyCount ? -1 : 1));
                }
                _penetration = MathF.Max(_penetration, -point.Separation - Vector3.Dot(point.Normal, delta - point.BodyAnchorDelta));
            }
        });
        if (_scene.DeviceStatistics is { } statistics)
        {
            _penetration = MathF.Max(_penetration, statistics.MaximumPenetration);
            _awakeNow = statistics.AwakeBodies;
            _contactsNow = statistics.ContactPoints;
            _islandPeak = Math.Max(_islandPeak, statistics.LargestIslandBodies);
        }
        foreach (Entity entity in _scene.Joints.Span)
        {
            if (!_world.IsAlive(entity)) continue;
            _liveJoints++;
            var ends = _world.GetDirectedRelationEndpoints(new SomeEngine.ECS.Relations.RelationEdge<JointConstraint>(entity));
            if (_readbackBodies is not null && (!_readbackBodies.Contains(ends.Source) || !_readbackBodies.Contains(ends.Target))) continue;
            JointConstraint joint = _world.Read<JointConstraint>(entity);
            BodyPose first = _world.Read<BodyPose>(ends.Source), second = _world.Read<BodyPose>(ends.Target);
            _jointGap = MathF.Max(_jointGap, Vector3.Distance(first.Position + Vector3.Transform(joint.SourceFrame.Position, first.Rotation),
                second.Position + Vector3.Transform(joint.TargetFrame.Position, second.Rotation)));
            if (joint.AngularX.Motion == JointAxisMotion.Locked && joint.AngularY.Motion == JointAxisMotion.Locked
                && joint.AngularZ.Motion == JointAxisMotion.Locked)
                _angularGap = MathF.Max(_angularGap, 2 * MathF.Acos(MathF.Min(1, MathF.Abs(Quaternion.Dot(
                    first.Rotation * joint.SourceFrame.Rotation, second.Rotation * joint.TargetFrame.Rotation)))));
        }
        foreach (Entity entity in _scene.DistanceRanges.Span)
        {
            var ends = _world.GetUndirectedRelationEndpoints(new SomeEngine.ECS.Relations.RelationEdge<DistanceRange>(entity));
            if (_readbackBodies is not null && (!_readbackBodies.Contains(ends.EndpointA) || !_readbackBodies.Contains(ends.EndpointB))) continue;
            DistanceRange range = _world.Read<DistanceRange>(entity);
            BodyPose first = _world.Read<BodyPose>(ends.EndpointA), second = _world.Read<BodyPose>(ends.EndpointB);
            float distance = Vector3.Distance(first.Position + Vector3.Transform(range.FirstAnchor, first.Rotation),
                second.Position + Vector3.Transform(range.SecondAnchor, second.Rotation));
            _rangeGap = MathF.Max(_rangeGap, MathF.Max(range.Minimum - distance, distance - range.Maximum));
        }
        _contactPeak = Math.Max(_contactPeak, _contactsNow);
        _awakePeak = Math.Max(_awakePeak, _awakeNow);
    }

    public JsonObject Describe()
    {
        Entity lastBody = _scene.ReadbackBodies.Span[^1];
        Vector3 last = _world.Read<BodyPose>(lastBody).Position;
        Vector3 velocity = _world.Read<BodyVelocity>(lastBody).Linear;
        Vector3 angularVelocity = _world.Read<BodyVelocity>(lastBody).Angular;
        return new JsonObject
        {
            ["Entities"] = _entitiesNow, ["PeakEntities"] = _entityPeak,
            ["BodyCount"] = _scene.Bodies.Length, ["LiveJoints"] = _liveJoints, ["DistanceRanges"] = _scene.DistanceRanges.Length,
            ["ReadbackBodyCount"] = _scene.ReadbackBodies.Length,
            ["BodyMotionScope"] = _readbackBodies is null ? "Every scene body" : "Selected readback bodies",
            ["ConstraintErrorScope"] = _readbackBodies is null ? "Every scene constraint" : "Constraints between selected readback bodies",
            ["LargestConnectedBodyCount"] = _scene.DeviceLargestIslandBodies,
            ["PeakConnectedBodyCount"] = _scene.DeviceStatistics is null ? null : _islandPeak,
            ["LastBodyEntityIndex"] = lastBody.Index,
            ["ContactPoints"] = _contactsNow, ["PeakContactPoints"] = _contactPeak, ["AwakeBodies"] = _awakeNow, ["PeakAwakeBodies"] = _awakePeak,
            ["MaximumJointAnchorGapMeters"] = _jointGap, ["MaximumDistanceViolationMeters"] = _rangeGap,
            ["MaximumAngularLockErrorRadians"] = _angularGap, ["MaximumQuaternionSquaredNormError"] = _quaternionError,
            ["MaximumPenetrationMeters"] = _penetration, ["MaximumSpeedMetersPerSecond"] = _speed,
            ["FinalMaximumSpeedMetersPerSecond"] = _finalSpeed,
            ["MaximumAngularSpeedRadiansPerSecond"] = _angularSpeed,
            ["FinalMaximumAngularSpeedRadiansPerSecond"] = _finalAngularSpeed,
            ["DynamicCenterOfMass"] = new JsonArray(_centerOfMass.X, _centerOfMass.Y, _centerOfMass.Z),
            ["LinearMomentum"] = new JsonArray(_linearMomentum.X, _linearMomentum.Y, _linearMomentum.Z),
            ["MinimumDynamicBodyCenter"] = new JsonArray(_minimumBodyCenter.X, _minimumBodyCenter.Y, _minimumBodyCenter.Z),
            ["MaximumDynamicBodyCenter"] = new JsonArray(_maximumBodyCenter.X, _maximumBodyCenter.Y, _maximumBodyCenter.Z),
            ["LastBodyPosition"] = new JsonArray(last.X, last.Y, last.Z),
            ["LastBodyVelocity"] = new JsonArray(velocity.X, velocity.Y, velocity.Z),
            ["LastBodyAngularVelocity"] = new JsonArray(angularVelocity.X, angularVelocity.Y, angularVelocity.Z),
        };
    }

    public void Dispose() { _world.ReleaseQuery(_contacts); _world.ReleaseQuery(_bodies); }
}
