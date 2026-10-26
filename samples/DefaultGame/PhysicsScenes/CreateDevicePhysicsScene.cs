using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

public static partial class PhysicsScenes
{
    public static PhysicsSceneSimulation CreateDevice(World world, PhysicsSceneDescription description,
        IGraphicsBackend backend, Device device, PhysicsSceneSettings settings, DeviceGridSettings gridSettings,
        DeviceGridCapacity gridCapacity, int contactCapacity, float contactDistance,
        ReadOnlySpan<int> readbackBodyIndices, string shaderDirectory) =>
        CreateDevice(world, description, Create(description), backend, device, settings, gridSettings, gridCapacity,
            contactCapacity, contactDistance, readbackBodyIndices, shaderDirectory);

    internal static PhysicsSceneSimulation CreateDevice(World world, PhysicsSceneDescription description, PhysicsSceneData data,
        IGraphicsBackend backend, Device device, PhysicsSceneSettings settings, DeviceGridSettings gridSettings,
        DeviceGridCapacity gridCapacity, int contactCapacity, float contactDistance,
        ReadOnlySpan<int> readbackBodyIndices, string shaderDirectory)
    {
        var shaders = new PhysicsSceneShaders(device.Capabilities.CompileTarget, shaderDirectory);
        DeviceGridKernels gridKernels = shaders.Grid();
        DeviceBodyKernels bodyKernels = shaders.Bodies();
        DeviceBodyInputKernels bodyInputKernels = shaders.BodyInputs();
        ComputePipelineDesc geometryKernel = shaders.Geometry(contactDistance), detectionKernel = shaders.Detection();
        DeviceContactMatchKernels matchKernels = shaders.Matching();
        ComputePipelineDesc contactGeometryKernel = shaders.ContactGeometry();
        DeviceGraphKernels graphKernels = shaders.Graph();
        DeviceAvbdKernels avbdKernels = shaders.Avbd();
        DeviceContactKernels responseKernels = shaders.Responses();

        PhysicsSceneEntities created = CreateEntities(world, data);
        Scheduler scheduler = WorldJobs.GetScheduler(world);
        const int threads = 64;
        var bodies = new DeviceBodies(backend, device, created.Bodies.Length);
        var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        var grid = new DeviceCollisionGrid(backend, device, gridKernels, gridSettings, gridCapacity);
        var geometry = new DeviceCollisionGeometry(backend, device, new(created.Bodies.Length, 0, 0, 0), bodies);
        for (int index = 0; index < created.Bodies.Length; index++)
        {
            Entity entity = created.Bodies[index];
            world.Add(entity, new DeviceBodyBinding(bodies, index));
            world.Add(entity, new DeviceAvbdEquationBinding(equations, index));
            world.Add(entity, new DeviceCollisionBody(grid, index));
            world.Add(entity, new DeviceGeometryBinding(geometry, index));
        }
        foreach (int index in readbackBodyIndices) world.Add(created.Bodies[index], new SceneReadbackBody());
        QueryDefinition filter = new QueryDefinitionBuilder().All<ScenePhysicsBody>().Build();
        QueryDefinition selectedFilter = new QueryDefinitionBuilder().All<SceneReadbackBody>().Build();
        QueryHandle members = world.Query(filter.WithAccess(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().Build()));
        QueryHandle shapes = world.Query(filter.WithAccess(new QueryDefinitionBuilder().Read<BoxGeometry>().Build()));
        QueryHandle poses = world.Query(filter.WithAccess(new QueryDefinitionBuilder().Read<BodyPose>().Read<BodyMass>().Read<BodyStepState>().Build()));
        QueryHandle points = world.Query(new QueryDefinitionBuilder().All<ScenePhysicsContact>().Read<ContactPoint>());
        QueryHandle states = world.Query(new QueryDefinitionBuilder().All<ScenePhysicsContact>().Read<AvbdContactState>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceAvbdContactState>>());
        QueryHandle observed = world.Query(selectedFilter.WithAccess(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().Build()));
        var pairs = world.Within(shapes);
        var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        var timedInputs = new DeviceBodyInputBuffers(backend, device, world, members, bodies, bodyInputKernels, threads);
        var sceneInputs = new ApplySceneInputChanges(world, created.HasInputChanges);
        var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, bodyKernels, threads);
        var shapeInputs = grid.WriteGeometryReferences<BoxGeometry, DeviceComponentSource<BoxGeometry, DeviceBoxReader>>(pairs, default, 0, threads);
        var updateGeometry = new UpdateDeviceGeometry(backend, device, grid, geometry, geometryKernel, 0, DeviceInputTransfer.Upload);
        var detection = new DeviceCollisionDetection(backend, device, grid, geometry, detectionKernel, 0,
            new(gridCapacity.Pairs, 4, Math.Min(gridCapacity.Pairs, 4096)), new(1e-5f, 32, 64), contactDistance,
            DeviceInputTransfer.Resident, DeviceCollisionReadback.Contacts);
        var detected = new ContactPointBuffer(scheduler, contactCapacity, contactCapacity * 2);
        var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, contactCapacity, "DeviceContacts");
        var anchors = new DeviceContactBodies(backend, device, contactCapacity, 2);
        var histories = new DeviceConstraintState<DeviceAvbdContactState>(backend, device, contactCapacity, "DeviceAvbdContactStates");
        var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, contactCapacity, "DeviceContactResponses");
        var matching = new DeviceContactMatches(backend, device, world, points, parameters, detection, matchKernels,
            new(.02f, .99f, true), contactCapacity * 2 + 1, threads);
        var currentGeometry = matching.CreateGeometryUpdate(bodies, anchors, contactGeometryKernel);
        var refresh = ContactPoints.Create(world, poses, points, detected, new SceneContactMaterials(),
            matching.Components(new SceneDeviceContactComponents(histories, responses)), new(.02f, .99f, true));
        var contactInputs = new DeviceContactInputs(world, points, parameters, anchors);
        var stateInputs = new DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint, DeviceAvbdContactState, DeviceAvbdContactStateSource>(
            backend, device, world, states, parameters, histories, default);
        var selected = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device, world, points, parameters, "DeviceContactsSelection");
        var selectedStates = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, points, parameters, new(histories), "DeviceContactAvbdSelection");
        var selectedResponses = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, points, parameters, new(responses), "DeviceContactResponsesSelection");
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); anchors.AddBindings(bindings); histories.AddBindings(bindings); responses.AddBindings(bindings);
        var joints = new DeviceSceneJoints(backend, device, world, created.Joints);
        joints.AddBindings(bindings);
        var ranges = new DeviceSceneDistanceRanges(backend, device, world, created.DistanceRanges);
        ranges.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(scheduler), anchors.GetReadAccess(scheduler),
            .. joints.Data.GetReadAccesses(scheduler), .. ranges.Data.GetReadAccesses(scheduler)];
        var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, graphKernels,
            new(contactCapacity + joints.Data.Capacity + ranges.Data.Capacity, 2), bindings, reads,
            threads, new IDeviceConstraintSelection[] { selected, joints.Selection, ranges.Selection });
        JobResourceAccess[] constraintAccesses = [.. reads, histories.GetWriteAccess(scheduler), responses.GetWriteAccess(scheduler),
            joints.States.GetWriteAccess(scheduler), ranges.States.GetWriteAccess(scheduler)];
        var solve = new DeviceAvbdSolver(backend, device, bodies, equations, graph, avbdKernels,
            new(settings.Iterations, settings.BodyIterations, settings.MultiplierRetention, 0), bindings, constraintAccesses, threads,
            new IDeviceConstraintSelection[] { selectedStates, selectedResponses, joints.StateSelection, ranges.StateSelection });
        var response = new DeviceContactActions(backend, device, bodies, graph, responses, responseKernels, bindings, reads,
            threads, new IDeviceConstraintSelection[] { selectedResponses });
        var results = new DeviceBodyResults(backend, device, bodies, world, observed, readbackBodyIndices.Length);
        var contactPenetration = new DeviceSceneContactPenetration(backend, device, bodies, parameters, selected, graph,
            shaders.ContactPenetration(), bindings, reads);
        PhysicsSceneTimings? timings = settings.Timings;
        var stages = sceneInputs.Measure(timings, "Scene inputs")
            .Then(bodyInputs.Upload().Measure(timings, "Body inputs"))
            .Then(joints.Upload().Measure(timings, "Joint inputs"))
            .Then(ranges.Upload().Measure(timings, "Distance inputs"))
            .Then(timedInputs.WriteInputs().Measure(timings, "Timed body inputs"))
            .Then(timedInputs.Apply().Measure(timings, "Body impulses"))
            .Then(timedInputs.ApplyKinematicTargets().Measure(timings, "Kinematic targets"))
            .Then(dynamics.ApplyGyroscopicTorque().Measure(timings, "Gyroscopic torque"))
            .Then(dynamics.Predict(Vector(data.Gravity)).Measure(timings, "Body prediction"))
            .Then(grid.BeginInputs().Then(shapeInputs).Measure(timings, "Geometry references"))
            .Then(updateGeometry.Update().Measure(timings, "Dynamic geometry and bounds"))
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None)
                .Measure(timings, "Grid, pairs and type bins"))
            .Then(new ClearDeviceSceneContacts(detected).Then(detection.Detect()).Measure(timings, "Narrow phase"))
            .Then(detection.WriteContacts(new ContactPointOutput(detected), new[] { detected.WriteAccess })
                .Measure(timings, "Contact geometry readback"))
            .Then(matching.Match().Then(refresh.Refresh(currentGeometry.Results)).Measure(timings, "Persistent contacts"))
            .Then(contactInputs.Upload().Then(stateInputs.Upload()).Measure(timings, "Contact inputs and history"))
            .Then(currentGeometry.Update().Then(response.Initialize()).Measure(timings, "Contact response inputs"))
            .Then(solve.Solve().Measure(timings, "Constraint graph and AVBD"))
            .Then(dynamics.UpdateVelocities().Measure(timings, "Body velocities"))
            .Then(response.ApplyRestitution(1).Measure(timings, "Restitution"));
        var simulation = PhysicsSimulation.Create(world, filter, stages, data.Substeps);
        var consumer = results.Readback(threads).Measure(timings, "Body readback")
            .Then(contactPenetration.Readback().Measure(timings, "Completed contact gap"))
            .Then(new CopyObservedSceneTransforms(selectedFilter).Measure(timings, "Body transforms"));
        var frame = simulation.CreateFrame(consumer, data.StepSeconds, 0, 0, 0);
        var initialize = new PhysicsStepContext(world, filter, 0, 0, 1, 0, data.StepSeconds);
        parameters.BeginInputs().Then(parameters.UploadInputs()).Schedule(in initialize, default).Complete();
        return new PhysicsSceneSimulation(world, description, settings, created.Bodies, created.Joints, created.DistanceRanges,
            data.StepSeconds, frame, frame.CompleteForRead, [members, shapes, poses, points, states, observed],
            [bodies, equations, grid, geometry, bodyInputs, timedInputs, sceneInputs, dynamics, shapeInputs, updateGeometry,
                detection, detected, parameters, anchors, histories, responses, matching, currentGeometry, refresh,
                contactInputs, stateInputs, selected, selectedStates, selectedResponses, joints, ranges, graph, solve, response, results, contactPenetration],
            () => contactPenetration.Value);
    }
}

internal readonly struct SceneReadbackBody : IComponent;

internal readonly struct ClearDeviceSceneContacts(ContactPointBuffer contacts) : IPhysicsStage, IJob
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        WorldJobs.GetScheduler(context.World).Schedule(this, new[] { contacts.WriteAccess }, dependency);
    public void Execute() => contacts.Clear();
}

internal readonly struct CopyObservedSceneTransforms(QueryDefinition selected) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        PhysicsStepContext observed = context with { Bodies = selected };
        return new CopySceneBodyTransforms().Schedule(in observed, dependency);
    }
}

internal readonly struct SceneDeviceContactComponents(DeviceConstraintState<DeviceAvbdContactState> histories,
    DeviceConstraintState<DeviceContactResponseState> responses) : IContactPointComponents
{
    public void AddReads(World world, List<JobResourceAccess> accesses) { }
    public void Add(ref JobCommandWriter commands, Entity point, in ContactPoint geometry,
        ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second, in PhysicsStepContext context)
    {
        commands.Add(point, new ScenePhysicsContact());
        commands.Add(point, new AvbdContactState { NormalPenalty = 1e6f, TangentPenalty = 1e6f, MultiplierNormal = geometry.Normal });
        commands.Add(point, new DeviceAvbdState<DeviceAvbdContactState>(histories));
        commands.Add(point, new DeviceContactResponseBinding(responses));
    }
}
