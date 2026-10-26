using System.Numerics;
using System.Text.Json.Nodes;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Physics;
using SomeEngine.Render.Components;
using SomeEngine.Resources;
using SomeEngine.Runtime;

namespace DefaultGame;

/// <summary>Original scene inputs, the selected physics and ordinary mesh extraction in the shared scene platform.</summary>
public sealed partial class PhysicsSceneExample(PhysicsSceneDescription description, PhysicsSceneSettings settings)
{
    private JsonObject _workload = new();
    private readonly ScenePhysicsEventCounts _events = new();
    private PhysicsSceneData? _input;
    private StandardMaterial _displayMaterial = null!;
    public int StepCount { get; private set; }

    public static PhysicsSceneExample CreateCpu(PhysicsSceneDescription description, PhysicsSceneTimings? timings = null) =>
        new(description, new PhysicsSceneSettings(Iterations: 8, BodyIterations: 1, StepBudgetMilliseconds: 1000d / 60)
        {
            JointBounds = new JointLongRangeSettings(6, 32, .99f, .99f),
            ContactSupports = new ContactSupportSettings(description.Dimension == 2 ? Vector3.UnitY : Vector3.UnitZ,
                1e-5f, 1e-4f, 1e-4f, 1e-4f, .99f, .99f, 1e-6f),
            ContactDistance = .02f,
            Graph = new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 16, 16, 64, 4, false),
            Timings = timings,
        });

    public TestScene CreateScene(string contentDirectory, StandardMaterial displayMaterial)
    {
        _displayMaterial = displayMaterial;
        _input = PhysicsScenes.Create(description);
        StepCount = GetStepCount(_input);
        return new($"Physics / {description.Name}", contentDirectory)
        {
            Initialize = Initialize, DescribeWorkload = Describe,
            WorldNameplates = false,
            MeasurementFrames = new(128, StepCount),
        };
    }

    private static int GetStepCount(PhysicsSceneData data) =>
        Math.Max(180, data.Events.Select(change => change.Frame + 121).DefaultIfEmpty(0).Max());

    public JsonNode Describe()
    {
        _workload["CompletedEvents"] = _events.Describe();
        return _workload;
    }

    public PhysicsSceneSimulation CreateSimulation(World world)
    {
        PhysicsSceneData data = _input ?? PhysicsScenes.Create(description);
        _input = null;
        _events.Clear();
        StepCount = GetStepCount(data);
        double[] momentum = Enumerable.Range(0, 3).Select(axis => data.Bodies.Sum(body => body.Mass * body.Velocity[axis])).ToArray();
        _workload = new JsonObject
        {
            ["Id"] = description.Id, ["Name"] = description.Name, ["Dimension"] = description.Dimension,
            ["Family"] = description.Family, ["Trigger"] = description.Trigger, ["Observation"] = description.Measurement,
            ["Device"] = "CPU", ["Algorithm"] = "AVBD", ["BroadPhase"] = "BVH refit",
            ["NarrowPhase"] = description.Dimension == 2 ? "PlanarBoxCollision" : "ConvexCollision<BoxGeometry, BoxGeometry>",
            ["Bodies"] = data.Bodies.Length, ["Joints"] = data.Joints.Length,
            ["DistanceRanges"] = data.DistanceRanges?.Length ?? 0, ["CollisionExclusions"] = data.CollisionExclusions.Length,
            ["InitialLinearMomentum"] = new JsonArray(momentum[0], momentum[1], momentum[2]),
            ["StepSeconds"] = data.StepSeconds, ["Substeps"] = data.Substeps, ["StepCount"] = StepCount,
            ["WarmupSteps"] = 0, ["FirstStepIncluded"] = true,
            ["WarmupScope"] = "Complete physics-step measurement",
            ["RenderingMeasurementFirstFrame"] = 128, ["RenderingMeasurementFrameCount"] = StepCount,
            ["Jit"] = new JsonObject
            {
                ["DynamicCodeCompiled"] = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled,
                ["TieredCompilation"] = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                ["TieredPGO"] = Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
                ["Scope"] = "Current process; every scene step, including its first step, is sampled",
            },
            ["StepBudgetMilliseconds"] = settings.StepBudgetMilliseconds,
            ["Iterations"] = settings.Iterations, ["BodyIterations"] = settings.BodyIterations,
            ["ContactDistance"] = settings.ContactDistance,
            ["JointBoundLevels"] = settings.JointBounds.Levels, ["ContactSupports"] = settings.ContactSupports.HasValue,
            ["Solver"] = new JsonObject
            {
                ["MultiplierRetention"] = settings.MultiplierRetention, ["InitialErrorFraction"] = 0,
                ["ContactNormalPenalty"] = 1e6f, ["ContactTangentPenalty"] = 1e6f,
                ["JointPenalty"] = 1e6f, ["DistanceRangePenalty"] = 1e6f,
                ["MatchDistance"] = .02f, ["MinimumNormalDot"] = .99f, ["WarmStart"] = true,
            },
            ["Activity"] = new JsonObject
            {
                ["SpeedThreshold"] = settings.Activity.SpeedThreshold, ["QuietDuration"] = settings.Activity.QuietDuration,
                ["LinearErrorThreshold"] = settings.Activity.LinearErrorThreshold, ["AngularErrorThreshold"] = settings.Activity.AngularErrorThreshold,
            },
            ["JointBounds"] = new JsonObject
            {
                ["Levels"] = settings.JointBounds.Levels, ["PenaltyScale"] = settings.JointBounds.PenaltyScale,
                ["MultiplierRetention"] = settings.JointBounds.MultiplierRetention, ["InitialErrorFraction"] = settings.JointBounds.InitialErrorFraction,
            },
            ["ContactSupportParameters"] = settings.ContactSupports is { } supports ? new JsonObject
            {
                ["UpDirection"] = new JsonArray(supports.UpDirection.X, supports.UpDirection.Y, supports.UpDirection.Z),
                ["DistanceTolerance"] = supports.DistanceTolerance, ["MotionTolerance"] = supports.MotionTolerance,
                ["AngularMotionTolerance"] = supports.AngularMotionTolerance, ["StickDistance"] = supports.StickDistance,
                ["MultiplierRetention"] = supports.MultiplierRetention, ["InitialErrorFraction"] = supports.InitialErrorFraction,
                ["RelativePivotTolerance"] = supports.RelativePivotTolerance,
            } : null,
            ["Materials"] = new JsonObject
            {
                ["Model"] = data.MaterialModel.ToString(), ["FrictionCombine"] = "GeometricMean",
                ["MinimumFriction"] = data.Bodies.Min(body => body.Friction), ["MaximumFriction"] = data.Bodies.Max(body => body.Friction),
                ["ContactStiffness"] = (data.Materials?.ContactStiffness ?? double.PositiveInfinity).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["JointLinearStiffness"] = (data.Materials?.JointLinearStiffness ?? double.PositiveInfinity).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["JointAngularStiffness"] = (data.Materials?.JointAngularStiffness ?? double.PositiveInfinity).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ContactDamping"] = data.Materials?.ContactDamping ?? 0,
            },
            ["Graph"] = new JsonObject { ["Order"] = settings.Graph.Order.ToString(),
                ["SmallIslandBodyLimit"] = settings.Graph.SmallIslandBodyLimit, ["BodiesPerBatch"] = settings.Graph.BodiesPerBatch,
                ["LargeIslandBodyLimit"] = settings.Graph.LargeIslandBodyLimit, ["RowsPerBlock"] = settings.Graph.RowsPerBlock,
                ["UseSimd"] = settings.Graph.UseSimd },
            ["Gravity"] = new JsonArray(data.Gravity.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["Events"] = new JsonArray(data.Events.Select(change => (JsonNode)new JsonObject
            {
                ["Step"] = change.Frame, ["Body"] = change.Body, ["RemovedJoint"] = change.RemovedJoint,
                ["VelocityIncrement"] = change.Velocity is null ? null
                    : new JsonArray(change.Velocity.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["Position"] = change.Position is null ? null
                    : new JsonArray(change.Position.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            }).ToArray()),
        };
        if (_devicePhysics) return CreateDeviceSimulation(world, description, data, settings);
        var events = new CountScenePhysicsEvents(_events);
        JobResourceAccess[] eventAccesses = [JobResourceAccess.Write(WorldJobs.GetScheduler(world).GetResourceToken(_events))];
        return description.Dimension == 2
            ? PhysicsScenes.CreateCpu(world, description, data, new PlanarBoxCollision(1e-5f), settings, events, eventAccesses)
            : PhysicsScenes.CreateCpu(world, description, data, new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64)), settings, events, eventAccesses);
    }

    private void Initialize(ApplicationFrame frame)
    {
        PhysicsSceneSimulation simulation = CreateSimulation(frame.World);
        frame.Systems.Physics = simulation;
        Entity[] previousMeshes = frame.Scene.MeshEntities.ToArray();
        StandardMaterial fixedMaterial = CreateMaterial(_displayMaterial, new(.12f, .16f, .21f, 1));
        StandardMaterial movingMaterial = CreateMaterial(_displayMaterial, new(.9f, .32f, .08f, 1));
        Mesh box = CreateBox();
        foreach (Entity body in simulation.Bodies.Span)
        {
            Material material = frame.World.Read<BodyMass>(body).Motion == BodyMotion.Static ? fixedMaterial : movingMaterial;
            frame.World.Add(body, new WorldTransform { Qvvs = frame.World.Read<LocalTransform>(body).Value });
            frame.World.Add(body, new MeshInstance { Mesh = box });
            frame.World.AddBuffer<MeshMaterialBinding>(body);
            frame.World.ExecuteBufferWrite<MeshMaterialBinding>(body, materials => materials.Add(new(material)));
        }
        foreach (Entity mesh in previousMeshes) frame.Scene.DeleteEntity(mesh);

        Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        foreach (Entity body in simulation.Bodies.Span)
        {
            if (frame.World.Read<BodyMass>(body).Motion == BodyMotion.Static) continue;
            Vector3 position = frame.World.Read<BodyPose>(body).Position;
            float radius = frame.World.Read<BoxGeometry>(body).HalfExtents.Length();
            minimum = Vector3.Min(minimum, position - new Vector3(radius));
            maximum = Vector3.Max(maximum, position + new Vector3(radius));
        }
        Vector3 center = (minimum + maximum) * .5f;
        float extent = MathF.Max(4, (maximum - minimum).Length());
        Vector3 camera = center + (description.Dimension == 2 ? new Vector3(0, 0, -1.2f) : new Vector3(.8f, -1.1f, .7f)) * extent;
        Vector3 forward = Vector3.Normalize(center - camera);
        Vector3 right = Vector3.Normalize(Vector3.Cross(description.Dimension == 2 ? Vector3.UnitY : Vector3.UnitZ, forward));
        Vector3 up = Vector3.Cross(forward, right);
        Quaternion rotation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0, forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1));
        frame.Scene.SetCameraTransform(camera, rotation);
    }

    private static StandardMaterial CreateMaterial(StandardMaterial source, Vector4 color) => new()
    {
        Shader = source.Shader, AlbedoMap = source.AlbedoMap, NormalMap = source.NormalMap, ARMMap = source.ARMMap,
        MaterialSampler = source.MaterialSampler, BaseColorTint = color, Roughness = .7f,
        EmissiveFactor = new Vector3(color.X, color.Y, color.Z) * .12f,
    };

    private static Mesh CreateBox()
    {
        Vector3[] normals = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        Vector2[] uv = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        var positions = new Vector3[24];
        var attributes = new StandardClusterAttributes[24];
        var indices = new uint[36];
        for (int face = 0; face < normals.Length; face++)
        {
            Vector3 normal = normals[face], horizontal = MathF.Abs(normal.Z) > .5f ? Vector3.UnitX : Vector3.UnitZ;
            Vector3 vertical = Vector3.Cross(normal, horizontal);
            for (int corner = 0; corner < 4; corner++)
            {
                positions[face * 4 + corner] = normal * .5f + horizontal * (uv[corner].X - .5f) + vertical * (uv[corner].Y - .5f);
                attributes[face * 4 + corner] = new(normal, new Vector4(horizontal, 1), uv[corner]);
            }
            uint first = (uint)face * 4;
            indices[face * 6] = first; indices[face * 6 + 1] = first + 1; indices[face * 6 + 2] = first + 2;
            indices[face * 6 + 3] = first; indices[face * 6 + 4] = first + 2; indices[face * 6 + 5] = first + 3;
        }
        return ClusterMeshBuilder.Build(positions, attributes, indices, [new ClusterMaterialGroup(0, 36, 0)],
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime, "Physics box");
    }
}

internal sealed class ScenePhysicsEventCounts
{
    private readonly Dictionary<(PhysicsEventKind Kind, PhysicsEventPhase Phase), ScenePhysicsEventCount> _counts = [];
    public void Clear() => _counts.Clear();
    public void Add(in PhysicsStepEvents step, in PhysicsEvent value)
    {
        var key = (value.Kind, value.Phase);
        if (_counts.TryGetValue(key, out ScenePhysicsEventCount count))
            _counts[key] = count with { Count = count.Count + 1, LastStep = step.Number, LastTime = value.Time };
        else _counts[key] = new(1, step.Number, step.Number, value.Time, value.Time);
    }
    public JsonArray Describe() => new(_counts.Select(pair => (JsonNode)new JsonObject
    {
        ["Kind"] = pair.Key.Kind.ToString(), ["Phase"] = pair.Key.Phase.ToString(), ["Count"] = pair.Value.Count,
        ["FirstStep"] = pair.Value.FirstStep, ["LastStep"] = pair.Value.LastStep,
        ["FirstTime"] = pair.Value.FirstTime, ["LastTime"] = pair.Value.LastTime,
    }).ToArray());
}

internal readonly record struct ScenePhysicsEventCount(int Count, long FirstStep, long LastStep, double FirstTime, double LastTime);

internal readonly struct CountScenePhysicsEvents(ScenePhysicsEventCounts counts) : IPhysicsEventConsumer
{
    public void Consume(scoped in PhysicsStepEvents step, scoped ReadOnlySpan<PhysicsEvent> events, ref JobCommandWriter commands)
    { foreach (PhysicsEvent value in events) counts.Add(in step, in value); }
}
