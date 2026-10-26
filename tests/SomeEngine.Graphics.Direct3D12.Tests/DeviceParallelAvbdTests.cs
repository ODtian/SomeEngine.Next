using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceAvbdTests
{
    [Fact]
    public void ParallelDeviceBodyColorsFeedTheFullInertiaAvbdResponse()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity anchor = CreateBody(world, bodies, equations, 0, new(2, 1, 0), BodyMotion.Static);
        Entity moving = CreateBody(world, bodies, equations, 1, Vector3.Zero, BodyMotion.Dynamic);
        world.Replace(moving, new BodyMass
        {
            Motion = BodyMotion.Dynamic, InverseMass = 1, CenterOfMass = Vector3.UnitX,
            InverseInertia = new(1, 0, .5f, 0, 0, 1, 0, 0, .5f, 0, 1, 0, 0, 0, 0, 1),
        });
        using var parameters = new DeviceConstraintData<DeviceElasticConstraint>(backend, device, 1, "DeviceElasticConstraints");
        using var histories = new DeviceConstraintState<DeviceAvbdElasticState>(backend, device, 1, "DeviceAvbdElasticStates");
        Entity relation = world.CreateRelation(anchor, moving, new ElasticConnection
        { SecondAnchor = Vector3.UnitY, RestRotation = Quaternion.Identity, LinearStiffness = 100 }).Entity;
        world.Add(relation, new AvbdElasticState { LinearPenalty = 900 });
        world.Add(relation, new DeviceConstraintBinding<DeviceElasticConstraint>(parameters, 0));
        world.Add(relation, new DeviceAvbdState<DeviceAvbdElasticState>(histories));
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().All<DeviceAvbdEquationBinding>());
        QueryHandle connections = world.Query(new QueryDefinitionBuilder().Read<ElasticConnection>().Read<UndirectedRelationEndpoints<ElasticConnection>>());
        QueryHandle stateRows = world.Query(new QueryDefinitionBuilder().Read<AvbdElasticState>()
            .Read<DeviceConstraintBinding<DeviceElasticConstraint>>().Read<DeviceAvbdState<DeviceAvbdElasticState>>());
        using var inputs = parameters.WriteInputs<ElasticConnection, DeviceElasticSource>(world, connections, default, 64);
        using var stateInputs = new DeviceAvbdStateInputs<AvbdElasticState, DeviceElasticConstraint,
            DeviceAvbdElasticState, DeviceAvbdElasticStateSource>(backend, device, world, stateRows, parameters, histories, default);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); histories.AddBindings(bindings);
        using var selected = new DeviceConstraintSelection<ElasticConnection, DeviceElasticConstraint>(backend, device,
            world, connections, parameters, "DeviceElasticConstraintsSelection");
        using var selectedStates = new DeviceConstraintSelection<ElasticConnection, DeviceElasticConstraint, DeviceAvbdStateRows<DeviceAvbdElasticState>>(
            backend, device, world, connections, parameters, new(histories), "DeviceElasticAvbdSelection");
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileParallelElasticGraph(),
            new(1, 2), bindings, parameters.GetReadAccesses(jobs), 64, selections: new[] { selected });
        using var solve = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileAvbd(), new(1, 1, 0, 0), bindings,
            parameters.GetReadAccesses(jobs).Append(histories.GetWriteAccess(jobs)).ToArray(), 64, selections: new[] { selectedStates });
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs())
            .Then(parameters.BeginInputs()).Then(inputs).Then(parameters.UploadInputs()).Then(stateInputs.Upload())
            .Then(dynamics.Predict(Vector3.Zero)).Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        BodyPose pose = world.Read<BodyPose>(moving);
        BodyMass mass = world.Read<BodyMass>(moving);
        // One point spring and inertial energy have this closed-form six-dimensional Schur response.
        Assert.InRange(Vector3.Distance(new(1.7243428f, -.2230256f, .0609710f), pose.GetCenterOfMass(in mass)), 0, 1e-5f);
        Assert.InRange(MathF.Abs(Quaternion.Dot(new(-.0918891f, .0295358f, -.2280818f, .9688461f), pose.Rotation)), .99999f, 1.00001f);
        Assert.InRange(Vector3.Distance(new(7.243428f, -2.230256f, .609710f), world.Read<BodyVelocity>(moving).Linear), 0, 1e-4f);
        world.ReleaseQuery(stateRows); world.ReleaseQuery(connections); world.ReleaseQuery(members);
    }

    private DeviceGraphKernels CompileParallelElasticGraph() => DeviceGraphTestKernels.CompileParallel(shaderCompiler, "parallel_elastic_graph", """
        #include "physics_elastic_data.slang"
        [shader("compute")][numthreads(64,1,1)]
        void WriteRows(uint3 id : SV_DispatchThreadID) { DeviceElasticBodies rows; writeDeviceConstraintGraphRows(id.x,rows); }
        """, ShaderDirectory());
}