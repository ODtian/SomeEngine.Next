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
    public void DeviceAvbdUsesTheOriginalPlanarInertiaForItsOffCenterSpring()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity anchor = CreateBody(world, bodies, equations, 0, Vector3.UnitX, BodyMotion.Static);
        Entity moving = CreateBody(world, bodies, equations, 1, Vector3.Zero, BodyMotion.Dynamic);
        world.Replace(moving, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1,
            InverseInertia = new Matrix4x4 { M33 = 1 } });
        using var parameters = new DeviceConstraintData<DeviceElasticConstraint>(backend, device, 1, "DeviceElasticConstraints");
        using var histories = new DeviceConstraintState<DeviceAvbdElasticState>(backend, device, 1, "DeviceAvbdElasticStates");
        Entity spring = world.CreateRelation(anchor, moving, new ElasticConnection
            { SecondAnchor = Vector3.UnitY, RestRotation = Quaternion.Identity, LinearStiffness = 1 }).Entity;
        world.Add(spring, new AvbdElasticState { LinearPenalty = 1e6f });
        world.Add(spring, new DeviceConstraintBinding<DeviceElasticConstraint>(parameters, 0));
        world.Add(spring, new DeviceAvbdState<DeviceAvbdElasticState>(histories));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>().All<DeviceAvbdEquationBinding>());
        QueryHandle springs = world.Query(world.QueryDefinition().Read<ElasticConnection>().Read<UndirectedRelationEndpoints<ElasticConnection>>());
        QueryHandle states = world.Query(world.QueryDefinition().Read<AvbdElasticState>()
            .Read<DeviceConstraintBinding<DeviceElasticConstraint>>().Read<DeviceAvbdState<DeviceAvbdElasticState>>());
        using var inputs = parameters.WriteInputs<ElasticConnection, DeviceElasticSource>(world, springs, default, 64);
        using var stateInputs = new DeviceAvbdStateInputs<AvbdElasticState, DeviceElasticConstraint, DeviceAvbdElasticState, DeviceAvbdElasticStateSource>(
            backend, device, world, states, parameters, histories, default);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); histories.AddBindings(bindings);
        using var selected = new DeviceConstraintSelection<ElasticConnection, DeviceElasticConstraint>(backend, device,
            world, springs, parameters, "DeviceElasticConstraintsSelection");
        using var selectedStates = new DeviceConstraintSelection<ElasticConnection, DeviceElasticConstraint, DeviceAvbdStateRows<DeviceAvbdElasticState>>(
            backend, device, world, springs, parameters, new(histories), "DeviceElasticAvbdSelection");
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileGraph(), new(1, 2),
            bindings, parameters.GetReadAccesses(jobs), 64, new[] { selected });
        using var solve = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileAvbd(), new(1, 1, 0, 0),
            bindings, parameters.GetReadAccesses(jobs).Append(histories.GetWriteAccess(jobs)).ToArray(), 64, new[] { selectedStates });
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs()).Then(parameters.BeginInputs()).Then(inputs)
            .Then(parameters.UploadInputs()).Then(stateInputs.Upload()).Then(dynamics.Predict(Vector3.Zero))
            .Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, 1).Complete();

        // Unit mass/inertia and the offset spring give the first Newton change (1/3, -1/2, -1/3 rad).
        BodyPose pose = world.Read<BodyPose>(moving);
        Assert.InRange(pose.Position.X, .33332f, .33335f); Assert.InRange(pose.Position.Y, -.50001f, -.49999f);
        Assert.Equal(0, pose.Position.Z); Assert.Equal(0, pose.Rotation.X); Assert.Equal(0, pose.Rotation.Y);
        Assert.InRange(pose.Rotation.Z, -.16441f, -.16438f);
        Assert.InRange(world.Read<BodyVelocity>(moving).Angular.Z, -.33031f, -.33028f);
        world.ReleaseQuery(states); world.ReleaseQuery(springs); world.ReleaseQuery(members);
    }
}
