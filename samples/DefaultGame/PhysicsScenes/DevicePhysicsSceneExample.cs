using System.Text.Json.Nodes;
using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.Graphics;
using SomeEngine.Physics;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Runtime;

namespace DefaultGame;

public sealed partial class PhysicsSceneExample : IApplicationGeometryFeature
{
    private bool _devicePhysics;
    private IGraphicsBackend? _backend;
    private Device? _device;

    public static PhysicsSceneExample CreateDevice(PhysicsSceneDescription description, IGraphicsBackend backend,
        Device device, PhysicsSceneTimings? timings = null)
    {
        PhysicsSceneExample example = CreateDevice(description, timings);
        example._backend = backend;
        example._device = device;
        return example;
    }

    internal static PhysicsSceneExample CreateDevice(PhysicsSceneDescription description, PhysicsSceneTimings? timings = null) =>
        new(description, new PhysicsSceneSettings(Iterations: 16, BodyIterations: 2, StepBudgetMilliseconds: 1000d / 60)
        {
            ContactDistance = .02f,
            GeometryInputsFixed = true,
            EquationBindingsFixed = true,
            ContactNormalPenalty = 100,
            ContactTangentPenalty = 100,
            JointBounds = new JointLongRangeSettings(description.Scene < 400 ? 6 : 0, 32, .99f, .99f),
            ContactSupports = new ContactSupportSettings(description.Dimension == 2 ? Vector3.UnitY : Vector3.UnitZ,
                1e-5f, 1e-4f, 1e-4f, 1e-4f, .99f, .99f, 1e-6f),
            Timings = timings,
        }) { _devicePhysics = true };

    IDisposable? IApplicationGeometryFeature.Install(ApplicationGeometryContext context)
    {
        _backend = context.Backend;
        _device = context.Device;
        return null;
    }

    private PhysicsSceneSimulation CreateDeviceSimulation(World world, PhysicsSceneDescription description,
        PhysicsSceneData data, PhysicsSceneSettings settings)
    {
        if (settings.JointBounds.Levels > 0 && settings.JointBoundsPerJoint == 0)
            settings = settings with { JointBoundsPerJoint = Math.Max(1, data.Joints.Length - 1) };
        int bodyCount = data.Bodies.Length;
        bool silo = _siloParticles > 0;
        var gridSettings = new DeviceGridSettings(silo ? .5f : 1, 16);
        var capacity = new DeviceGridCapacity(bodyCount, bodyCount * 8, bodyCount * 16 + 1,
            bodyCount * (silo ? 16 : 32), silo ? 6 : 1);
        int contacts = silo ? Math.Max(128, _siloParticles * 12 + PhysicsSiloScenes.StaticBodies * 4) : bodyCount * 32;
        _workload["Device"] = "GPU";
        _workload["Adapter"] = _device!.Adapter.Name;
        _workload["Driver"] = _device.Adapter.DriverVersion;
        _workload["DedicatedVideoMemoryBytes"] = _device.Adapter.DedicatedVideoMemory;
        _workload["BroadPhase"] = "Hierarchical grid";
        _workload["NarrowPhase"] = silo ? "Sphere, Box and Hull: six concrete bidirectional bins"
            : "DeviceConvexCollision<DeviceBox, DeviceBox>";
        _workload["BodiesPerContact"] = 2;
        _workload["GeometryInputsFixed"] = settings.GeometryInputsFixed;
        _workload["EquationBindingsFixed"] = settings.EquationBindingsFixed;
        _workload["Graph"] = new JsonObject { ["Order"] = "Device islands and colors" };
        JsonObject solver = (JsonObject)_workload["Solver"]!;
        solver["ContactConstraint"] = "DeviceContactPotentialAvbdConstraints";
        solver["ContactPenaltyConditioning"] = "Inverse squared step and contact Jacobian mobility";
        solver["ContactPenaltyMinimum"] = 1;
        solver["ContactPenaltyMaximum"] = 1e9f;
        solver["ContactPenaltyIncreasePerMeter"] = 100000;
        solver["ContactPressureLengthScale"] = .0005f;
        solver["JointBoundsPerJoint"] = settings.JointBoundsPerJoint;
        _workload["Grid"] = new JsonObject
        {
            ["CellSize"] = gridSettings.CellSize, ["Levels"] = gridSettings.LevelCount,
            ["Bodies"] = capacity.Bodies, ["Cells"] = capacity.Cells, ["Buckets"] = capacity.Buckets,
            ["Pairs"] = capacity.Pairs, ["Bins"] = capacity.Bins, ["ContactPoints"] = contacts,
        };
        int[] readback = silo && bodyCount > 256
            ? [.. Enumerable.Range(0, PhysicsSiloScenes.StaticBodies + 3), bodyCount - 3, bodyCount - 2, bodyCount - 1]
            : Enumerable.Range(0, bodyCount).ToArray();
        var events = new CountScenePhysicsEvents(_events);
        JobResourceAccess[] eventAccesses = [JobResourceAccess.Write(WorldJobs.GetScheduler(world).GetResourceToken(_events))];
        string shaders = Path.Combine(AppContext.BaseDirectory, "PhysicsShaders");
        if (silo)
        {
            _workload["ShapeDistribution"] = new JsonObject
            {
                ["Sphere"] = (_siloParticles + 2) / 3, ["Box"] = (_siloParticles + 1) / 3,
                ["Hull"] = _siloParticles / 3, ["Radius"] = PhysicsSiloScenes.Radius,
                ["HullVertices"] = PhysicsSiloScenes.Hull.Vertices.Length,
                ["HullFaces"] = PhysicsSiloScenes.Hull.Faces.Length,
                ["StaticBoxes"] = PhysicsSiloScenes.StaticBodies,
            };
            _workload["ReadbackBodyIndices"] = new JsonArray(readback.Select(index => (JsonNode?)JsonValue.Create(index)).ToArray());
            _workload["CompletedEventKinds"] = "Sleep and Wake";
            return PhysicsScenes.CreateDevice<SceneSiloGeometry, DeviceSiloGeometryInputs, CountScenePhysicsEvents>(
                world, description, data, default, _backend!, _device!, settings, gridSettings, capacity, contacts,
                settings.ContactDistance, readback, shaders, events, eventAccesses, contactEvents: false);
        }
        return PhysicsScenes.CreateDevice(world, description, data, _backend!, _device!, settings, gridSettings,
            capacity, contacts, settings.ContactDistance, readback, shaders, events, eventAccesses);
    }
}
