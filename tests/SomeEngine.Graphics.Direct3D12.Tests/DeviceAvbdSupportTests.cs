using System.Numerics;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;
using SomeEngine.ECS.Systems;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceAvbdTests
{
    [Fact]
    public void DeviceContactSupportsKeepTheOffsetStackLevelAndTransferItsLoadToTheFloor()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 4);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity floor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        Entity lower = CreateBody(world, bodies, equations, 1, Vector3.UnitY, BodyMotion.Dynamic);
        Entity upper = CreateBody(world, bodies, equations, 2, new(0, 3, 0), BodyMotion.Dynamic);
        Entity falling = CreateBody(world, bodies, equations, 3, new(5, 10, 0), BodyMotion.Dynamic);
        foreach (Entity body in new[] { lower, upper })
        {
            world.Replace(body, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1,
                CenterOfMass = new(.25f, 0, .2f), InverseInertia = Matrix4x4.Identity });
            world.Add(body, new LongRangeBodySupport());
            world.AddBuffer<LongRangeSupportLoad>(body); world.AddBuffer<LongRangeSupportEquation>(body);
        }
        using var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 8, "DeviceContacts");
        using var links = new DeviceContactBodies(backend, device, 8, 2);
        using var histories = new DeviceConstraintState<DeviceAvbdContactState>(backend, device, 8, "DeviceAvbdContactStates");
        using var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, 8, "DeviceContactResponses");
        QueryHandle supportBodies = world.Query(new QueryDefinitionBuilder().Read<LongRangeBodySupport>()
            .ReadBuffer<LongRangeSupportLoad>().ReadBuffer<LongRangeSupportEquation>());
        using var supports = new DeviceContactSupports(backend, device, world, supportBodies, bodies, parameters,
            new(Vector3.UnitY, 1e-5f, 1e-4f, 1e-4f, 1e-4f, .99f, .99f, 1e-6f), contactsPerBody: 8);
        world.Add(lower, new DeviceSupportBodyBinding(supports)); world.Add(upper, new DeviceSupportBodyBinding(supports));
        Entity[] contacts = new Entity[8];
        int row = 0;
        for (int layer = 0; layer < 2; layer++)
            foreach (Vector2 corner in new[] { new Vector2(-1, -1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(1, 1) })
            {
                Entity firstBody = layer == 0 ? floor : lower, secondBody = layer == 0 ? lower : upper;
                Entity point = world.CreateRelation(firstBody, secondBody, new ContactPoint
                { Normal = Vector3.UnitY, FirstBodyCount = 1, WorldPoint = new(corner.X, layer * 2, corner.Y) }).Entity;
                var first = world.CreateRelation(point, firstBody, new ContactParticipant());
                var second = world.CreateRelation(point, secondBody, new ContactParticipant());
                world.AddBuffer<ContactBodyLink>(point);
                world.ExecuteBufferWrite<ContactBodyLink>(point, buffer =>
                {
                    buffer.Add(new(first, new(corner.X, layer == 0 ? 0 : 1, corner.Y), 1));
                    buffer.Add(new(second, new(corner.X, -1, corner.Y), 1));
                });
                world.Add(point, new ContactMaterial { NormalStiffness = float.PositiveInfinity, StaticFriction = 1, DynamicFriction = .5f });
                world.Add(point, new ContactResponse());
                world.Add(point, new AvbdContactState { NormalPenalty = 100, TangentPenalty = 100, FrictionRegularizationSpeed = .01f });
                world.Add(point, new LongRangeContactSupport()); world.AddBuffer<LongRangeSupportLoad>(point);
                world.Add(point, new DeviceConstraintBinding<DeviceContactConstraint>(parameters, row));
                world.Add(point, new DeviceAvbdState<DeviceAvbdContactState>(histories));
                world.Add(point, new DeviceAvbdState<DeviceContactSupportState>(supports.ContactStates));
                world.Add(point, new DeviceContactResponseBinding(responses));
                contacts[row++] = point;
            }
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>());
        QueryHandle points = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>().ReadBuffer<ContactBodyLink>());
        QueryHandle stateRows = world.Query(new QueryDefinitionBuilder().Read<AvbdContactState>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceAvbdContactState>>());
        QueryHandle supportRows = world.Query(new QueryDefinitionBuilder().Read<LongRangeContactSupport>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceContactSupportState>>());
        using var input = parameters.WriteInputs<ContactPoint, DeviceContactSource>(world, points, new(links), 64, [links.GetUploadAccess(jobs)]);
        using var stateInput = new DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint, DeviceAvbdContactState, DeviceAvbdContactStateSource>(
            backend, device, world, stateRows, parameters, histories, default);
        using var supportInput = new DeviceAvbdStateInputs<LongRangeContactSupport, DeviceContactConstraint, DeviceContactSupportState, DeviceContactSupportStateSource>(
            backend, device, world, supportRows, parameters, supports.ContactStates, default);
        using var selected = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device, world, points, parameters, "DeviceContactsSelection");
        using var selectedState = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, points, parameters, new(histories), "DeviceContactAvbdSelection");
        using var selectedSupport = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceContactSupportState>>(
            backend, device, world, points, parameters, new(supports.ContactStates), "DeviceContactSupportSelection");
        using var selectedResponse = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, points, parameters, new(responses), "DeviceContactResponsesSelection");
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend, device, parameters, responses);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); links.AddBindings(bindings); histories.AddBindings(bindings);
        responses.AddBindings(bindings); supports.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), links.GetReadAccess(jobs), .. supports.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileSupportGraph(), new(12, 2), bindings, reads,
            64, new IDeviceConstraintSelection[] { selected, selectedSupport, supports });
        using var solver = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileSupportAvbd(), new(1, 1, 0, 0), bindings,
            parameters.GetReadAccesses(jobs).Append(links.GetReadAccess(jobs)).Append(histories.GetWriteAccess(jobs)).Append(responses.GetWriteAccess(jobs))
                .Concat(supports.GetWriteAccesses(jobs)).ToArray(), 64, new IDeviceConstraintSelection[] { selectedState, selectedResponse });
        using var response = new DeviceContactActions(backend, device, bodies, graph, responses, CompileContactActions(), bindings, reads,
            64, new[] { selectedResponse });
        var step = parameters.BeginInputs().Then(input).Then(parameters.UploadInputs()).Then(links.UploadInputs())
            .Then(stateInput.Upload()).Then(supportInput.Upload()).Then(supports.InitializeBuffers())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(new(0, -10, 0)))
            .Then(response.Initialize()).Then(solver.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64)).Then(contactResults.Readback(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, step, 1).Schedule(0, 0, .1).Complete();

        Assert.InRange(MathF.Abs(world.Read<BodyPose>(lower).Position.Y - 1), 0, 1e-4f);
        Assert.InRange(MathF.Abs(world.Read<BodyPose>(upper).Position.Y - 3), 0, 1e-4f);
        Assert.InRange(world.Read<BodyVelocity>(lower).Angular.Length(), 0, 1e-4f);
        Assert.InRange(world.Read<BodyVelocity>(upper).Angular.Length(), 0, 1e-4f);
        Assert.InRange(contacts.Take(4).Sum(point => world.Read<ContactResponse>(point).ForceOnSecond.Y), 19.99f, 20.01f);
        Assert.InRange(world.Read<BodyPose>(falling).Position.Y, 9.8999f, 9.9001f);
        world.ReleaseQuery(supportRows); world.ReleaseQuery(stateRows); world.ReleaseQuery(points);
        world.ReleaseQuery(members); world.ReleaseQuery(supportBodies);
    }
    private DeviceGraphKernels CompileSupportGraph()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("contact_support_graph", """
            #include "physics_contact_data.slang"
            #include "physics_contact_support_bodies.slang"
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID)
            { DeviceConstraintBodyGroups<DeviceContactBodies,DeviceSupportBodies> rows; writeDeviceConstraintGraphRows(id.x,rows); }
            [shader("compute")][numthreads(1,1,1)]
            void BuildGraph(uint3 id : SV_DispatchThreadID) { buildDeviceConstraintGraph(); }
            """, [new("WriteRows", SlangStage.Compute), new("BuildGraph", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1));
    }
    private DeviceAvbdKernels CompileSupportAvbd()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("contact_support_avbd", """
            #include "physics_avbd_contact_support.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID)
            { DeviceAvbdConstraintGroups<DeviceSupportedContactAvbdConstraints,DeviceContactSupportAvbdConstraints> rows; initializeDeviceAvbd(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id : SV_DispatchThreadID)
            { DeviceAvbdConstraintGroups<DeviceSupportedContactAvbdConstraints,DeviceContactSupportAvbdConstraints> rows; updateDeviceAvbdBody(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateDuals(uint3 id : SV_DispatchThreadID)
            { DeviceAvbdConstraintGroups<DeviceSupportedContactAvbdConstraints,DeviceContactSupportAvbdConstraints> rows; updateDeviceAvbdDuals(id.x,rows); }
            [shader("compute")][numthreads(1,1,1)]
            void InitializeGroups(uint3 id : SV_DispatchThreadID) { initializeDeviceContactSupports(AvbdStep); }
            [shader("compute")][numthreads(1,1,1)]
            void UpdateGroups(uint3 id : SV_DispatchThreadID) { updateDeviceContactSupports(AvbdStep); }
            """, [new("Initialize", SlangStage.Compute), new("UpdateBody", SlangStage.Compute), new("UpdateDuals", SlangStage.Compute),
                new("InitializeGroups", SlangStage.Compute), new("UpdateGroups", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1), Entry(shader, 2), Entry(shader, 3), Entry(shader, 4));
    }
}
