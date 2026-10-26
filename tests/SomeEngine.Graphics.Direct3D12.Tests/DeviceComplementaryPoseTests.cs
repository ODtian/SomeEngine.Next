using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
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
    public void DeviceContactAndInternalLengthShareTheirSubstepAndDeliverTheCompletedBone()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 3);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity floor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = CreateBody(world, bodies, equations, 1, Vector3.UnitY, BodyMotion.Dynamic);
        Entity anchor = CreateBody(world, bodies, equations, 2, new(0, 2, 0), BodyMotion.Static);
        Entity point = world.CreateRelation(floor, moving, new ContactPoint
            { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1 }).Entity;
        var first = world.CreateRelation(point, floor, new ContactParticipant());
        var second = world.CreateRelation(point, moving, new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point);
        world.ExecuteBufferWrite<ContactBodyLink>(point, links =>
        { links.Add(new(first, Vector3.Zero, 1)); links.Add(new(second, Vector3.Zero, 1)); });
        world.Add(point, new ContactMaterial { NormalStiffness = 100 });
        world.Add(point, new ContactResponse());
        world.Add(point, new AvbdContactState { NormalPenalty = 900 });
        using var contacts = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 1, "DeviceContacts");
        using var ends = new DeviceContactBodies(backend, device, 1, 2);
        using var contactStates = new DeviceConstraintState<DeviceAvbdContactState>(backend, device, 1, "DeviceAvbdContactStates");
        using var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, 1, "DeviceContactResponses");
        world.Add(point, new DeviceConstraintBinding<DeviceContactConstraint>(contacts, 0));
        world.Add(point, new DeviceAvbdState<DeviceAvbdContactState>(contactStates));
        world.Add(point, new DeviceContactResponseBinding(responses));
        using var lengths = new DeviceConstraintData<DeviceLengthConstraint>(backend, device, 1, "DeviceLengths");
        using var lengthStates = new DeviceConstraintState<float>(backend, device, 1, "DeviceLengthMultipliers");
        Entity length = world.CreateRelation(anchor, moving, new XpbdLength { RestLength = 1, Compliance = .01f }).Entity;
        world.Add(length, new DeviceConstraintBinding<DeviceLengthConstraint>(lengths, 0));
        world.Add(length, new DeviceXpbdState<float>(lengthStates));
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().All<DeviceAvbdEquationBinding>());
        QueryHandle contactRows = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>().ReadBuffer<ContactBodyLink>());
        QueryHandle contactStateRows = world.Query(new QueryDefinitionBuilder().Read<AvbdContactState>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceAvbdContactState>>());
        QueryHandle lengthRows = world.Query(new QueryDefinitionBuilder().Read<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>()
            .Read<DeviceConstraintBinding<DeviceLengthConstraint>>().All<DeviceXpbdState<float>>());
        using var writeContacts = contacts.WriteInputs<ContactPoint, DeviceContactSource>(world, contactRows, new(ends), 64,
            [ends.GetUploadAccess(jobs)]);
        using var writeStates = new DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint,
            DeviceAvbdContactState, DeviceAvbdContactStateSource>(backend, device, world, contactStateRows, contacts, contactStates, default);
        using var writeLengths = lengths.WriteInputs<XpbdLength, DeviceLengthSource>(world, lengthRows, default, 64);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var selectedContacts = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device, world,
            contactRows, contacts, "DeviceContactsSelection");
        using var selectedContactStates = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, contactRows, contacts, new(contactStates), "DeviceContactAvbdSelection");
        using var selectedResponses = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, contactRows, contacts, new(responses), "DeviceContactResponsesSelection");
        using var selectedLengths = new DeviceConstraintSelection<XpbdLength, DeviceLengthConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, lengthRows, lengths, new(lengthStates), "DeviceLengthsSelection");
        var bindings = new Dictionary<string, ResourceBinding[]>();
        contacts.AddBindings(bindings); ends.AddBindings(bindings); contactStates.AddBindings(bindings); responses.AddBindings(bindings);
        lengths.AddBindings(bindings); lengthStates.AddBindings(bindings);
        JobResourceAccess[] reads = [.. contacts.GetReadAccesses(jobs), ends.GetReadAccess(jobs), .. lengths.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileComplementaryGraph(), new(2, 2),
            bindings, reads, 64, new IDeviceConstraintSelection[] { selectedContacts, selectedLengths });
        JobResourceAccess[] solveAccesses = [.. reads, contactStates.GetWriteAccess(jobs), responses.GetWriteAccess(jobs), lengthStates.GetReadAccess(jobs)];
        using var avbd = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileComplementaryAvbd(), new(1, 1, 0, 0),
            bindings, solveAccesses, 64,
            new IDeviceConstraintSelection[] { selectedContactStates, selectedResponses });
        using var xpbd = new DeviceXpbdProjection<DeviceLengthConstraint, float>(backend, device, world, lengthRows,
            bodies, dynamics, lengths, lengthStates, CompileComplementaryLength(), 64);
        using var response = new DeviceContactActions(backend, device, bodies, graph, responses, CompileContactActions(), bindings,
            reads, 64, new[] { selectedResponses });
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend, device, contacts, responses);

        BoneTransform rest = new() { Translation = Vector3.UnitY, Rotation = Quaternion.Identity, Scale = Vector3.One };
        var skeleton = new Skeleton([-1], [rest], [1]);
        using SkeletonInstance instance = new PoseStorage().CreateInstance(skeleton);
        Entity target = PoseAnimation.CreateTarget(world, instance, resultCount: 2, output: new(1));
        var clip = new AnimationClip(skeleton, 1, ClipSampler.Create<RawClipData, RawClipSampler>(new(1, [])));
        Entity animation;
        using (var builder = new AnimationBuilder(world))
        {
            using (builder.Parallel())
            {
                builder.BlendPhysicsPose(target, new(new(0), new(1), new PhysicsPoseMap(skeleton, [new(0, Matrix4x4.Identity)]), new BoneMask([1])));
                builder.Leaf<ClipParameters, ClipLeaf>(target, new(clip, new(0), Loop: true));
            }
            animation = builder.Play();
        }
        QueryHandle poses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>());
        var mapping = (Body: moving, Query: poses, Read: default(ReadPhysicsPose));
        world.ExecuteBufferWrite<PhysicsPoseInstance, (Entity Body, QueryHandle Query, ReadPhysicsPose Read)>(animation, ref mapping,
            static (instances, ref mapping) => mapping.Read = instances[0].BindBodies(mapping.Query, [mapping.Body]));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, nodes: static nodes => nodes.Add(new PhysicsPoseSystem()));
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs())
            .Then(contacts.BeginInputs()).Then(writeContacts).Then(contacts.UploadInputs()).Then(ends.UploadInputs()).Then(writeStates.Upload())
            .Then(lengths.BeginInputs()).Then(writeLengths).Then(lengths.UploadInputs())
            .Then(dynamics.Predict(new(0, -10, 0))).Then(response.Initialize()).Then(avbd.Initialize()).Then(xpbd.Initialize())
            .Then(new AlternateContactAndLength(avbd.Iterate(1), xpbd.Iterate(1), 64)).Then(dynamics.UpdateVelocities())
            .Then(results.Readback(64)).Then(contactResults.Readback(64)).Then(mapping.Read);

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(7, 1, .1).Complete();
        var frame = new ImmediateSystemContext(world, 0, 0, .1);
        systems.Update(ref frame);

        // The 100 N/m contact and length share the inertial prediction .9: 300*y = 290.
        Assert.InRange(world.Read<BodyPose>(moving).Position.Y, .96665f, .96668f);
        Assert.InRange(world.Read<BodyVelocity>(moving).Linear.Y, -.3335f, -.3331f);
        Assert.InRange(world.Read<ContactResponse>(point).ForceOnSecond.Y, 3.332f, 3.335f);
        Assert.InRange(instance.ModelPose.Transforms[0].Translation.Y, .96665f, .96668f);
        Assert.Equal(7, world.Read<BodyStepState>(moving).Number);
        Assert.Equal(1.1, world.Read<BodyStepState>(moving).Time, 8);
        Assert.Equal(Vector3.Zero, world.Read<BodyPose>(floor).Position);
        Assert.Equal(new(0, 2, 0), world.Read<BodyPose>(anchor).Position);
        world.ReleaseQuery(poses); world.ReleaseQuery(lengthRows); world.ReleaseQuery(contactStateRows);
        world.ReleaseQuery(contactRows); world.ReleaseQuery(members);
    }

    private DeviceGraphKernels CompileComplementaryGraph()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("contact_length_graph", """
            #include "physics_contact_data.slang"
            #include "physics_length_graph.slang"
            typedef DeviceConstraintBodyGroups<DeviceContactBodies, DeviceLengthBodies> Rows;
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID) { Rows rows; writeDeviceConstraintGraphRows(id.x, rows); }
            [shader("compute")][numthreads(1,1,1)]
            void BuildGraph(uint3 id : SV_DispatchThreadID) { buildDeviceConstraintGraph(); }
            """, [new("WriteRows", SlangStage.Compute), new("BuildGraph", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1));
    }
    private DeviceAvbdKernels CompileComplementaryAvbd()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("contact_length_avbd", """
            #include "physics_avbd_contact.slang"
            #include "physics_xpbd_length_forces.slang"
            typedef DeviceAvbdConstraintGroups<DeviceContactAvbdConstraints, DeviceXpbdLengthForces> Rows;
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID) { Rows rows; initializeDeviceAvbd(id.x, rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id : SV_DispatchThreadID) { Rows rows; updateDeviceAvbdBody(id.x, rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateDuals(uint3 id : SV_DispatchThreadID) { Rows rows; updateDeviceAvbdDuals(id.x, rows); }
            """, [new("Initialize", SlangStage.Compute), new("UpdateBody", SlangStage.Compute), new("UpdateDuals", SlangStage.Compute)],
            "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1), Entry(shader, 2));
    }
    private DeviceXpbdKernels CompileComplementaryLength()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("contact_internal_length", """
            #include "physics_xpbd_length.slang"
            #include "physics_xpbd.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID) { initializeDeviceXpbd<DeviceXpbdLength>(id.x); }
            [shader("compute")][numthreads(64,1,1)]
            void Project(uint3 id : SV_DispatchThreadID) { projectDeviceXpbd<DeviceXpbdLength>(id.x); }
            """, [new("Initialize", SlangStage.Compute), new("Project", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1));
    }
    private readonly struct AlternateContactAndLength(IterateDeviceAvbd contacts,
        IterateDeviceXpbd<DeviceLengthConstraint, float> lengths, int iterations) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
        {
            for (int iteration = 0; iteration < iterations; iteration++)
            { dependency = contacts.Schedule(in context, dependency); dependency = lengths.Schedule(in context, dependency); }
            return dependency;
        }
    }
}
