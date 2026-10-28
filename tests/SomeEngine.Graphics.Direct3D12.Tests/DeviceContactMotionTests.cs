using System.Numerics;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
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
    public void DeviceContactsKeepTheirEntityAndResidentForceWhenTheirBodiesMoveTogether()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        using var grid = new DeviceCollisionGrid(backend, device, CompileHistoryGrid(),
            new(1, 3), new(2, 256, 257, 4, 1));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(2, 0, 0, 0), bodies);
        Entity floor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = CreateBody(world, bodies, equations, 1, new(1.5f, 0, 0), BodyMotion.Dynamic);
        foreach (var (entity, index) in new[] { (floor, 0), (moving, 1) })
        {
            world.Add(entity, new SphereGeometry(1));
            world.Add(entity, new ContactMaterial { NormalStiffness = 100 });
            world.Add(entity, new DeviceCollisionBody(grid, index));
            world.Add(entity, new DeviceGeometryBinding(geometry, index));
        }
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>());
        QueryHandle spheres = world.Query(new QueryDefinitionBuilder().Read<SphereGeometry>());
        QueryHandle poses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>());
        QueryHandle points = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>());
        QueryHandle stateRows = world.Query(new QueryDefinitionBuilder().Read<AvbdContactState>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceAvbdContactState>>());
        var pairs = world.Within(spheres);
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry, CompileHistoryReferencedDetection(), 0,
            new DeviceDetectionCapacity(4, 4, 1) { BodiesPerContact = 2 }, new(1e-5f, 32, 64), 0,
            DeviceInputTransfer.Resident, DeviceCollisionReadback.Contacts);
        using var detected = new ContactPointBuffer(jobs, 4, 8);
        using var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 4, "DeviceContacts");
        using var anchors = new DeviceContactBodies(backend, device, 4, 2);
        using var histories = new DeviceConstraintState<DeviceAvbdContactState>(backend, device, 4, "DeviceAvbdContactStates");
        using var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, 4, "DeviceContactResponses");
        using var matching = new DeviceContactMatches(backend, device, world, points, parameters, detection, bodies, anchors,
            CompileContactMatching(), new(.02f, .99f, true), bucketCount: 17, threadsPerGroup: 64);
        using var currentContacts = matching.CreateGeometryUpdate(bodies, anchors, CompileCurrentContactGeometry());
        using var refresh = ContactPoints.Create(world, poses, points, detected, new AverageContactMaterials(),
            matching.Components(new DeviceHistoryComponents(histories, responses)), new(.02f, .99f, true));
        using var input = parameters.WriteInputs<ContactPoint, DeviceContactSource>(world, points, new(anchors), 64, [anchors.GetUploadAccess(jobs)]);
        using var stateInput = new DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint, DeviceAvbdContactState, DeviceAvbdContactStateSource>(
            backend, device, world, stateRows, parameters, histories, default);
        using var selected = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device, world, points, parameters, "DeviceContactsSelection");
        using var selectedState = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, points, parameters, new(histories), "DeviceContactAvbdSelection");
        using var selectedResponse = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, points, parameters, new(responses), "DeviceContactResponsesSelection");
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var sphereInputs = grid.WriteGeometryReferences<SphereGeometry, DeviceComponentSource<SphereGeometry, DeviceSphereReader>>(pairs, default, 0, 64);
        using var update = new UpdateDeviceGeometry(backend, device, grid, geometry, CompileHistoryGeometryUpdate(), 0, DeviceInputTransfer.Upload);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend, device, parameters, responses);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); anchors.AddBindings(bindings); histories.AddBindings(bindings); responses.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), anchors.GetReadAccess(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileContactGraph(), new(4, 2), bindings, reads, 64, new[] { selected });
        using var solver = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileContactAvbd(), new(1, 1, 1, 0), bindings,
            reads.Concat(new[] { histories.GetWriteAccess(jobs), responses.GetWriteAccess(jobs) }).ToArray(), 64,
            new IDeviceConstraintSelection[] { selectedState, selectedResponse });
        using var response = new DeviceContactActions(backend, device, bodies, graph, responses, CompileContactActions(), bindings, reads, 64, new[] { selectedResponse });
        var stages = bodyInputs.Upload().Then(grid.BeginInputs()).Then(sphereInputs).Then(update.Update())
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None)).Then(detection.Detect())
            .Then(detection.WriteContacts(new ContactPointOutput(detected), new JobResourceAccess[] { detected.WriteAccess }))
            .Then(matching.Match()).Then(refresh.Refresh(matching.Results))
            .Then(parameters.BeginInputs()).Then(input).Then(parameters.UploadInputs()).Then(anchors.UploadInputs()).Then(stateInput.Upload())
            .Then(currentContacts.Update()).Then(dynamics.Predict(Vector3.Zero)).Then(response.Initialize())
            .Then(solver.Solve()).Then(dynamics.UpdateVelocities());
        var simulation = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);
        var initialize = new PhysicsStepContext(world, QueryDefinition.Empty, 0, 0, 1, 0, .1);
        parameters.BeginInputs().Then(parameters.UploadInputs()).Schedule(in initialize, default).Complete();

        PhysicsStepResult firstStep = simulation.Schedule(0, 0, .1);
        results.Readback(64).Schedule(in initialize, firstStep.Completion).Complete();
        Entity original = SinglePoint(world, points);
        foreach (Entity body in new[] { floor, moving })
        {
            BodyPose pose = world.Read<BodyPose>(body);
            pose.Position += new Vector3(0, .1f, 0);
            world.Replace(body, pose);
        }
        detected.Clear();
        PhysicsStepResult completed = simulation.Schedule(1, .1, .1);
        var outputContext = new PhysicsStepContext(world, QueryDefinition.Empty, 1, 0, 1, .1, .1);
        results.Readback(64).Then(contactResults.Readback(64)).Schedule(in outputContext, completed.Completion).Complete();

        Assert.Equal(original, SinglePoint(world, points));
        Assert.InRange(world.Read<BodyPose>(moving).Position.Y, .09999f, .10001f);
        Assert.InRange(world.Read<BodyPose>(moving).Position.X, 1.94443f, 1.94446f);
        Assert.InRange(world.Read<BodyVelocity>(moving).Linear.X, 2.7776f, 2.7780f);
        Assert.InRange(world.Read<ContactResponse>(original).ForceOnSecond.X, 11.110f, 11.113f);
        Assert.Equal(1, world.Read<BodyStepState>(moving).Number);
        world.ReleaseQuery(stateRows); world.ReleaseQuery(points); world.ReleaseQuery(poses);
        world.ReleaseQuery(spheres); world.ReleaseQuery(members);
    }

}
