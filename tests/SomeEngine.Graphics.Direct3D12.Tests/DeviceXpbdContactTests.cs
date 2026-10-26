using System.Numerics;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceXpbdTests
{
    [Fact]
    public void DeviceXpbdRestitutionUsesTheCompletedContactForceAndSelectedBodyVelocity()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        Entity floor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Dynamic);
        Entity moving = AddBody(world, bodies, 1, Vector3.UnitY, BodyMotion.Dynamic);
        world.Add(moving, new SelectedXpbdBody());
        world.Replace(moving, new BodyVelocity { Linear = new(0, -2, 0) });
        Entity point = world.CreateRelation(floor, moving, new ContactPoint
            { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1 }).Entity;
        var first = world.CreateRelation(point, floor, new ContactParticipant());
        var second = world.CreateRelation(point, moving, new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point);
        world.ExecuteBufferWrite<ContactBodyLink>(point, links =>
        {
            links.Add(new(first, Vector3.Zero, 1)); links.Add(new(second, Vector3.Zero, 1));
        });
        world.Add(point, new ContactMaterial { NormalStiffness = 100, Restitution = .5f, RestitutionThreshold = .1f });
        world.Add(point, new ContactResponse());
        using var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 1, "DeviceContacts");
        using var side = new DeviceContactBodies(backend, device, 1, 2);
        using var histories = new DeviceConstraintState<XpbdContactState>(backend, device, 1, "DeviceXpbdContactStates");
        using var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, 1, "DeviceContactResponses");
        world.Add(point, new DeviceConstraintBinding<DeviceContactConstraint>(parameters, 0));
        world.Add(point, new DeviceXpbdState<XpbdContactState>(histories)); world.Add(point, new DeviceContactResponseBinding(responses));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>().All<SelectedXpbdBody>());
        QueryHandle contacts = world.Query(world.QueryDefinition().Read<ContactPoint>().Read<ContactMaterial>().ReadBuffer<ContactBodyLink>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().All<DeviceXpbdState<XpbdContactState>>());
        using var write = parameters.WriteInputs<ContactPoint, DeviceContactSource>(world, contacts, new(side), 64, [side.GetUploadAccess(jobs)]);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend, device, parameters, responses);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); side.AddBindings(bindings); histories.AddBindings(bindings); responses.AddBindings(bindings);
        using var selected = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceXpbdStateRows<XpbdContactState>>(
            backend, device, world, contacts, parameters, new(histories), "DeviceContactsSelection");
        using var selectedResponses = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, contacts, parameters, new(responses), "DeviceContactResponsesSelection");
        JobResourceAccess[] inputs = [.. parameters.GetReadAccesses(jobs), side.GetReadAccess(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileXpbdContactGraph(),
            new(1, 2), bindings, inputs, 64, new[] { selected });
        using var solve = new DeviceXpbdSolver(backend, device, bodies, graph, CompileXpbdContact(),
            new(Iterations: 2, RowsPerBlock: 4), bindings, inputs.Append(responses.GetWriteAccess(jobs)).ToArray(),
            new[] { histories.States }, 64, new[] { selectedResponses });
        using var actions = new DeviceContactActions(backend, device, bodies, graph, responses, CompileXpbdContactActions(),
            bindings, inputs, 64, new[] { selectedResponses });
        var stages = parameters.BeginInputs().Then(write).Then(parameters.UploadInputs()).Then(side.UploadInputs())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(actions.Initialize()).Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(actions.ApplyRestitution(1))
            .Then(results.Readback(64)).Then(contactResults.Readback(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(3, 1, .1).Complete();

        Near(new(0, .9f, 0), world.Read<BodyPose>(moving).Position);
        Near(Vector3.UnitY, world.Read<BodyVelocity>(moving).Linear);
        Near(Vector3.Zero, world.Read<BodyPose>(floor).Position);
        Near(Vector3.Zero, world.Read<BodyVelocity>(floor).Linear);
        Near(new(0, 10, 0), world.Read<ContactResponse>(point).ForceOnSecond);
        Assert.InRange(world.Read<ContactResponse>(point).RestitutionImpulse, 1.99999f, 2.00001f);
        Assert.Equal(1.1, world.Read<ContactResponse>(point).EndTime);
        world.ReleaseQuery(contacts); world.ReleaseQuery(members);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(.2f, 0, 0, -2)]
    [InlineData(1, .075f, .75f, -2.5f)]
    public void DeviceXpbdContactCombinesRepeatedBodyAnchorsAndReturnsThePhysicalForce(
        float tangentialSpeed, float expectedX, float expectedVelocity, float expectedForce)
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        Entity floor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = AddBody(world, bodies, 1, Vector3.UnitY, BodyMotion.Dynamic);
        world.Replace(moving, new BodyVelocity { Linear = new(tangentialSpeed, 0, 0) });
        Entity point = world.CreateRelation(floor, moving, new ContactPoint
            { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1 }).Entity;
        var fixedAnchor = world.CreateRelation(point, floor, new ContactParticipant());
        var firstAnchor = world.CreateRelation(point, moving, new ContactParticipant());
        var secondAnchor = world.CreateRelation(point, moving, new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point);
        world.ExecuteBufferWrite<ContactBodyLink>(point, links =>
        {
            links.Add(new(fixedAnchor, Vector3.Zero, 1));
            links.Add(new(firstAnchor, -Vector3.UnitX, .5f));
            links.Add(new(secondAnchor, Vector3.UnitX, .5f));
        });
        world.Add(point, new ContactMaterial { NormalStiffness = 100, StaticFriction = 1, DynamicFriction = .5f });
        world.Add(point, new ContactResponse());
        using var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 1, "DeviceContacts");
        using var side = new DeviceContactBodies(backend, device, 1, 3);
        using var histories = new DeviceConstraintState<XpbdContactState>(backend, device, 1, "DeviceXpbdContactStates");
        using var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, 1, "DeviceContactResponses");
        world.Add(point, new DeviceConstraintBinding<DeviceContactConstraint>(parameters, 0));
        world.Add(point, new DeviceXpbdState<XpbdContactState>(histories));
        world.Add(point, new DeviceContactResponseBinding(responses));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle contacts = world.Query(world.QueryDefinition().Read<ContactPoint>().Read<ContactMaterial>().ReadBuffer<ContactBodyLink>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().All<DeviceXpbdState<XpbdContactState>>());
        using var write = parameters.WriteInputs<ContactPoint, DeviceContactSource>(world, contacts, new(side), 64, [side.GetUploadAccess(jobs)]);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend, device, parameters, responses);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); side.AddBindings(bindings); histories.AddBindings(bindings); responses.AddBindings(bindings);
        using var selected = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceXpbdStateRows<XpbdContactState>>(
            backend, device, world, contacts, parameters, new(histories), "DeviceContactsSelection");
        using var selectedResponses = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, contacts, parameters, new(responses), "DeviceContactResponsesSelection");
        JobResourceAccess[] inputs = [.. parameters.GetReadAccesses(jobs), side.GetReadAccess(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileXpbdContactGraph(),
            new(1, 3), bindings, inputs, 64, new[] { selected });
        using var solve = new DeviceXpbdSolver(backend, device, bodies, graph, CompileXpbdContact(),
            new(Iterations: 2, RowsPerBlock: 4), bindings, inputs.Append(responses.GetWriteAccess(jobs)).ToArray(),
            new[] { histories.States }, 64, new[] { selectedResponses });
        using var actions = new DeviceContactActions(backend, device, bodies, graph, responses, CompileXpbdContactActions(),
            bindings, inputs, 64, new[] { selectedResponses });
        var stages = parameters.BeginInputs().Then(write).Then(parameters.UploadInputs()).Then(side.UploadInputs())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(new(0, -10, 0)))
            .Then(actions.Initialize()).Then(solve.Solve()).Then(dynamics.UpdateVelocities())
            .Then(results.Readback(64)).Then(contactResults.Readback(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(3, 1, .1).Complete();

        Near(new(expectedX, .95f, 0), world.Read<BodyPose>(moving).Position);
        Near(new(expectedVelocity, -.5f, 0), world.Read<BodyVelocity>(moving).Linear);
        Near(Vector3.Zero, world.Read<BodyVelocity>(moving).Angular);
        Near(new(expectedForce, 5, 0), world.Read<ContactResponse>(point).ForceOnSecond);
        Assert.Equal(1.1, world.Read<ContactResponse>(point).EndTime);
        world.ReleaseQuery(contacts); world.ReleaseQuery(members);
    }

    private DeviceGraphKernels CompileXpbdContactGraph()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("xpbd_contact_graph", """
            #include "physics_contact_data.slang"
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID) { DeviceContactBodies rows; writeDeviceConstraintGraphRows(id.x, rows); }
            [shader("compute")][numthreads(1,1,1)]
            void BuildGraph(uint3 id : SV_DispatchThreadID) { buildDeviceConstraintGraph(); }
            """, [new("WriteRows", SlangStage.Compute), new("BuildGraph", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }
    private DeviceXpbdKernels CompileXpbdContact()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("xpbd_contact", """
            #include "physics_xpbd_graph.slang"
            #include "physics_xpbd_contact.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
            {
                DeviceXpbdConstraintRows<DeviceContactBodies, DeviceXpbdContactProjection<DeviceGraphBodies>> rows;
                initializeDeviceXpbdGraph(group.x, thread.x, rows);
            }
            [shader("compute")][numthreads(64,1,1)]
            void Project(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
            {
                DeviceXpbdConstraintRows<DeviceContactBodies, DeviceXpbdContactProjection<DeviceGraphBodies>> rows;
                projectDeviceXpbdGraph(group.x, thread.x, rows);
            }
            """, [new("Initialize", SlangStage.Compute), new("Project", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }
    private DeviceContactKernels CompileXpbdContactActions()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("xpbd_contact_actions", """
            #include "physics_contact_actions.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID) { DeviceContactResponseActions<DeviceGraphBodies> actions; initializeDeviceContactResponses(id.x, actions); }
            [shader("compute")][numthreads(64,1,1)]
            void Restore(uint3 id : SV_DispatchThreadID) { DeviceContactResponseActions<DeviceGraphBodies> actions; applyDeviceContactRestitution(id.x, actions); }
            """, [new("Initialize", SlangStage.Compute), new("Restore", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }
}
