using System.Numerics;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceAvbdTests
{
    [Theory]
    [InlineData(3, 2.5f, -5)]
    [InlineData(.5f, .75f, 2.5f)]
    public void DeviceDistanceRangeUsesTheOriginalRadialMaterialInItsCompletedBodyMotion(float start, float expectedPosition, float expectedVelocity)
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new() { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        var anchor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        var moving = CreateBody(world, bodies, equations, 1, new(start, 0, 0), BodyMotion.Dynamic);
        world.Replace(moving, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        using var parameters = new DeviceConstraintData<DeviceDistanceRangeConstraint>(backend, device, 2, "DeviceDistanceRanges");
        using var histories = new DeviceConstraintState<DeviceAvbdDistanceRangeState>(backend, device, 2, "DeviceAvbdDistanceRangeStates");
        var range = world.CreateRelation(anchor, moving, new DistanceRange { Minimum = 1, Maximum = 2, Stiffness = 100 }).Entity;
        world.Add(range, new AvbdDistanceRangeState { Penalty = 900 });
        world.Add(range, new DeviceConstraintBinding<DeviceDistanceRangeConstraint>(parameters, 1));
        world.Add(range, new DeviceAvbdState<DeviceAvbdDistanceRangeState>(histories));
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().All<DeviceAvbdEquationBinding>());
        QueryHandle ranges = world.Query(new QueryDefinitionBuilder().Read<DistanceRange>().Read<UndirectedRelationEndpoints<DistanceRange>>());
        QueryHandle states = world.Query(new QueryDefinitionBuilder().Read<AvbdDistanceRangeState>()
            .Read<DeviceConstraintBinding<DeviceDistanceRangeConstraint>>().Read<DeviceAvbdState<DeviceAvbdDistanceRangeState>>());
        using var inputs = parameters.WriteInputs<DistanceRange, DeviceDistanceRangeSource>(world, ranges, default, 64);
        using var stateInputs = new DeviceAvbdStateInputs<AvbdDistanceRangeState, DeviceDistanceRangeConstraint,
            DeviceAvbdDistanceRangeState, DeviceAvbdDistanceRangeStateSource>(backend, device, world, states, parameters, histories, default);
        using var selected = new DeviceConstraintSelection<DistanceRange, DeviceDistanceRangeConstraint>(backend, device,
            world, ranges, parameters, "DeviceDistanceRangesSelection");
        using var selectedStates = new DeviceConstraintSelection<DistanceRange, DeviceDistanceRangeConstraint, DeviceAvbdStateRows<DeviceAvbdDistanceRangeState>>(
            backend, device, world, ranges, parameters, new(histories), "DeviceDistanceRangeAvbdSelection");
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); histories.AddBindings(bindings);
        JobResourceAccess[] reads = parameters.GetReadAccesses(jobs);
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileDistanceRangeGraph(), new(2, 2),
            bindings, reads, 64, new[] { selected });
        using var solve = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileDistanceRangeAvbd(),
            new(32, 2, 0, 0), bindings, reads.Append(histories.GetWriteAccess(jobs)).ToArray(), 64, new[] { selectedStates });
        var steps = bodies.WriteInputs(64).Then(bodies.UploadInputs()).Then(parameters.BeginInputs()).Then(inputs)
            .Then(parameters.UploadInputs()).Then(stateInputs.Upload()).Then(dynamics.Predict(Vector3.Zero))
            .Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, steps, 1).Schedule(4, 2, .1).Complete();

        Assert.InRange(world.Read<BodyPose>(moving).Position.X, expectedPosition - 1e-4f, expectedPosition + 1e-4f);
        Assert.InRange(world.Read<BodyVelocity>(moving).Linear.X, expectedVelocity - .001f, expectedVelocity + .001f);
        Assert.Equal(Vector3.Zero, world.Read<BodyPose>(anchor).Position);
        Assert.Equal(4, world.Read<BodyStepState>(moving).Number);
        Assert.Equal(2.1, world.Read<BodyStepState>(moving).Time);
        world.ReleaseQuery(states); world.ReleaseQuery(ranges); world.ReleaseQuery(members);
    }

    private DeviceGraphKernels CompileDistanceRangeGraph()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("distance_range_graph", """
            #include "physics_distance_range_bodies.slang"
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID)
            { DeviceDistanceRangeBodies rows; writeDeviceConstraintGraphRows(id.x,rows); }
            [shader("compute")][numthreads(1,1,1)]
            void BuildGraph(uint3 id : SV_DispatchThreadID) { buildDeviceConstraintGraph(); }
            """, [new("WriteRows", SlangStage.Compute), new("BuildGraph", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1));
    }

    private DeviceAvbdKernels CompileDistanceRangeAvbd()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("distance_range_avbd", """
            #include "physics_avbd_distance_range.slang"
            [shader("compute")][numthreads(64,1,1)]
            void ClearBodyIndices(uint3 id : SV_DispatchThreadID) { clearDeviceAvbdBodyIndices(id.x); }
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID)
            { DeviceDistanceRangeAvbdConstraints rows; initializeDeviceAvbdRows(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id : SV_DispatchThreadID)
            { DeviceDistanceRangeAvbdConstraints rows; updateDeviceAvbdBody(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateDuals(uint3 id : SV_DispatchThreadID)
            { DeviceDistanceRangeAvbdConstraints rows; updateDeviceAvbdDuals(id.x,rows); }
            """, [new("ClearBodyIndices", SlangStage.Compute), new("Initialize", SlangStage.Compute),
                new("UpdateBody", SlangStage.Compute), new("UpdateDuals", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 1), Entry(shader, 2), Entry(shader, 3), ClearBodyIndices: Entry(shader, 0));
    }
}
