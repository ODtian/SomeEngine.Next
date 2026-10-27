using System.Numerics;
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
    [Fact]
    public void ProjectConstraintSelectionTracksItsDeclaredComponentChanges()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        var anchor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        var moving = CreateBody(world, bodies, equations, 1, new(2, 0, 0), BodyMotion.Dynamic);
        using var parameters = new DeviceConstraintData<DeviceElasticConstraint>(backend, device, 1, "DeviceElasticConstraints");
        using var histories = new DeviceConstraintState<DeviceAvbdElasticState>(backend, device, 1, "DeviceAvbdElasticStates");
        var connection = world.CreateRelation(anchor, moving, new ElasticConnection
        {
            FirstAnchor = Vector3.UnitX, RestRotation = Quaternion.Identity,
            LinearStiffness = float.PositiveInfinity,
        });
        world.Add(connection.Entity, new AvbdElasticState { LinearPenalty = 100 });
        world.Add(connection.Entity, new DeviceConstraintBinding<DeviceElasticConstraint>(parameters, 0));
        world.Add(connection.Entity, new DeviceAvbdState<DeviceAvbdElasticState>(histories));
        world.Add(connection.Entity, new ProjectConstraintFlag { Selected = true });
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().All<DeviceAvbdEquationBinding>());
        QueryHandle connections = world.Query(new QueryDefinitionBuilder().Read<ElasticConnection>()
            .Read<UndirectedRelationEndpoints<ElasticConnection>>());
        QueryHandle stateRows = world.Query(new QueryDefinitionBuilder().Read<AvbdElasticState>()
            .Read<DeviceConstraintBinding<DeviceElasticConstraint>>().Read<DeviceAvbdState<DeviceAvbdElasticState>>());
        using var input = parameters.WriteInputs<ElasticConnection, DeviceElasticSource>(world, connections, default, 64);
        using var stateInput = new DeviceAvbdStateInputs<AvbdElasticState, DeviceElasticConstraint,
            DeviceAvbdElasticState, DeviceAvbdElasticStateSource>(backend, device, world, stateRows, parameters, histories, default);
        using var selected = new DeviceConstraintSelection<ElasticConnection, DeviceElasticConstraint, ProjectConstraintRows>(
            backend, device, world, connections, parameters, default, "DeviceElasticConstraintsSelection");
        using var selectedStates = new DeviceConstraintSelection<ElasticConnection, DeviceElasticConstraint,
            DeviceAvbdStateRows<DeviceAvbdElasticState>>(backend, device, world, connections, parameters,
                new(histories), "DeviceElasticAvbdSelection");
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); histories.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(scheduler), histories.GetReadAccess(scheduler)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileGraph(), new(1, 2),
            bindings, reads, 64, new[] { selected });
        using var solve = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileAvbd(),
            new(1, 1, 0, 0), bindings,
            parameters.GetReadAccesses(scheduler).Append(histories.GetWriteAccess(scheduler)).ToArray(), 64,
            new[] { selectedStates });
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs())
            .Then(parameters.BeginInputs()).Then(input).Then(parameters.UploadInputs()).Then(stateInput.Upload())
            .Then(dynamics.Predict(Vector3.Zero)).Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));
        var simulation = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);

        simulation.Schedule(0, 0, .1).Complete();
        Assert.InRange(world.Read<BodyPose>(moving).Position.X, 1.49999f, 1.50001f);
        world.Replace(connection.Entity, new ProjectConstraintFlag { Selected = false });
        world.Replace(moving, new BodyPose { Position = new(2, 0, 0), Rotation = Quaternion.Identity });
        world.Replace(moving, new BodyVelocity());
        simulation.Schedule(1, .1, .1).Complete();
        Assert.InRange(world.Read<BodyPose>(moving).Position.X, 1.99999f, 2.00001f);

        world.Replace(connection.Entity, new ProjectConstraintFlag { Selected = true });
        world.Replace(moving, new BodyPose { Position = new(2, 0, 0), Rotation = Quaternion.Identity });
        world.Replace(moving, new BodyVelocity());
        simulation.Schedule(2, .2, .1).Complete();
        Assert.InRange(world.Read<BodyPose>(moving).Position.X, 1.49999f, 1.50001f);
        world.Replace(moving, new BodyPose { Position = new(2, 0, 0), Rotation = Quaternion.Identity });
        world.Replace(moving, new BodyVelocity());
        simulation.Schedule(3, .3, .1).Complete();

        Assert.InRange(world.Read<BodyPose>(moving).Position.X, 1.49999f, 1.50001f);
        Assert.Equal(3, world.Read<BodyStepState>(moving).Number);
        world.ReleaseQuery(stateRows); world.ReleaseQuery(connections); world.ReleaseQuery(members);
    }

    private struct ProjectConstraintFlag : IComponent { public bool Selected; }
    private readonly struct ProjectConstraintRows : IDeviceConstraintRowSelection
    {
        public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<ProjectConstraintFlag>();
        public void AddReadResources(QueryReadResources resources) { }
        public bool Matches(scoped QueryRow row) => row.TryRead<ProjectConstraintFlag>(out var flag) && flag.Selected;
        public bool HasChangedInputs(scoped QueryChunkView chunk) => !chunk.Has<ProjectConstraintFlag>()
            || chunk.HasChangedSinceLastSystemVersion<ProjectConstraintFlag>();
    }
}
