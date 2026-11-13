using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
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
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new(execution:new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 3);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity floor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = CreateBody(world, bodies, equations, 1, Vector3.UnitY, BodyMotion.Dynamic);
        Entity anchor = CreateBody(world, bodies, equations, 2, new(0, 2, 0), BodyMotion.Static);
        using var source=CreateAvbdContactValues(jobs,floor,moving,new() { Normal=Vector3.UnitY,BodyAnchorDelta=Vector3.UnitY,FirstBodyCount=1 },new() { NormalStiffness=100 },
            [new(floor,Vector3.Zero,1),new(moving,Vector3.Zero,1)],new() { NormalPenalty=900 });
        using var contacts=new DeviceContactManifolds<DeviceAvbdContactState>(backend,device,new(1,1,2),default);
        using var writeContacts=new DeviceContactInputs<DeviceAvbdContactState>(backend,device,world,source,contacts);
        using var lengths = new DeviceConstraintData<DeviceLengthConstraint>(backend, device, 1, "DeviceLengths");
        using var lengthStates = new DeviceConstraintState<float>(backend, device, 1, "DeviceLengthMultipliers");
        Entity length = world.CreateRelation(anchor, moving, new XpbdLength { RestLength = 1, Compliance = .01f }).Entity;
        world.Add(length, new DeviceConstraintBinding<DeviceLengthConstraint>(lengths, 0));
        world.Add(length, new DeviceXpbdState<float>(lengthStates));
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>().With<DeviceAvbdEquationBinding>());
        Query lengthRows = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>()
            .WithStored<DeviceConstraintBinding<DeviceLengthConstraint>>().With<DeviceXpbdState<float>>());
        using var writeLengths = lengths.WriteInputs<XpbdLength, DeviceLengthSource>(world, lengthRows, default, 64);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var selectedLengths = new DeviceConstraintSelection<XpbdLength, DeviceLengthConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, lengthRows, lengths, new(lengthStates), "DeviceLengthsSelection");
        var bindings = new Dictionary<string, ResourceBinding[]>();
        contacts.Current.AddBindings(bindings);
        lengths.AddBindings(bindings); lengthStates.AddBindings(bindings);
        JobResourceAccess[] reads = [.. lengths.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileComplementaryGraph(), new(2, 2),
            bindings, reads, 64, new IDeviceConstraintSelection[] { selectedLengths },contacts:contacts);
        JobResourceAccess[] solveAccesses = [.. reads, lengthStates.GetReadAccess(jobs)];
        using var avbd = new AvbdGraphCommands(backend, device, bodies, equations, graph, CompileComplementaryAvbd(), new(1, 1, 0, 0),
            bindings, solveAccesses, 64,
            Array.Empty<IDeviceConstraintSelection>());
        using var xpbd = new DeviceXpbdProjection<DeviceLengthConstraint, float>(backend, device, world, lengthRows,
            bodies, dynamics, lengths, lengthStates, CompileComplementaryLength(), 64);
        using var response = new DeviceContactActions(backend, device, bodies, graph, contacts, CompileContactActions(), bindings,
            reads, 64, Array.Empty<IDeviceConstraintSelection>());
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend,device,jobs);

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
        Query poses = world.Query(new QueryDescription().WithStored<BodyPose>());
        ReadPhysicsPose poseRead;
        using(var instances=world.GetBuffer<PhysicsPoseInstance>(animation)) poseRead=instances[0].BindBodies(poses,[moving]);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, nodes: static nodes => nodes.Add(new PhysicsPoseSystem()));
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs())
            .Then(writeContacts.Upload())
            .Then(writeLengths).Then(lengths.UploadInputs())
            .Then(dynamics.Predict(new(0, -10, 0))).Then(response.Initialize()).Then(avbd.Initialize()).Then(xpbd.Initialize())
            .Then(new AlternateContactAndLength(avbd.Iterate(1), xpbd.Iterate(1), 64)).Then(dynamics.UpdateVelocities())
            .Then(results.Readback(64)).Then(contactResults.Readback(contacts)).Then(poseRead);

        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), stages, 1).Schedule(7, 1, .1).Complete();
        var frame = new ImmediateSystemContext(world, 0, 0, .1);
        systems.Update(ref frame);

        // The 100 N/m contact and length share the inertial prediction .9: 300*y = 290.
        Assert.InRange(world.ReadComponentData<BodyPose>(moving).Position.Y, .96665f, .96668f);
        Assert.InRange(world.ReadComponentData<BodyVelocity>(moving).Linear.Y, -.3335f, -.3331f);
        Assert.InRange(contactResults.Contacts.OpenView().Responses[0].ForceOnSecond.Y, 3.332f, 3.335f);
        Assert.InRange(instance.ModelPose.Transforms[0].Translation.Y, .96665f, .96668f);
        Assert.Equal(7, world.ReadComponentData<BodyStepState>(moving).Number);
        Assert.Equal(1.1, world.ReadComponentData<BodyStepState>(moving).Time, 8);
        Assert.Equal(Vector3.Zero, world.ReadComponentData<BodyPose>(floor).Position);
        Assert.Equal(new(0, 2, 0), world.ReadComponentData<BodyPose>(anchor).Position);
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
    private AvbdGraphKernels CompileComplementaryAvbd()
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
    private XpbdGraphKernels CompileComplementaryLength()
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
    private readonly struct AlternateContactAndLength(IterateAvbdGraph contacts,
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
