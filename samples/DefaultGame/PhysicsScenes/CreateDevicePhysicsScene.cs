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

    public static PhysicsSceneSimulation CreateDevice<TEvents>(World world, PhysicsSceneDescription description,
        IGraphicsBackend backend, Device device, PhysicsSceneSettings settings, DeviceGridSettings gridSettings,
        DeviceGridCapacity gridCapacity, int contactCapacity, float contactDistance,
        ReadOnlySpan<int> readbackBodyIndices, string shaderDirectory, TEvents events,
        ReadOnlySpan<JobResourceAccess> eventAccesses = default)
        where TEvents : struct, IPhysicsEventConsumer =>
        CreateDevice(world, description, Create(description), backend, device, settings, gridSettings, gridCapacity,
            contactCapacity, contactDistance, readbackBodyIndices, shaderDirectory, events, eventAccesses);

    internal static PhysicsSceneSimulation CreateDevice(World world, PhysicsSceneDescription description, PhysicsSceneData data,
        IGraphicsBackend backend, Device device, PhysicsSceneSettings settings, DeviceGridSettings gridSettings,
        DeviceGridCapacity gridCapacity, int contactCapacity, float contactDistance,
        ReadOnlySpan<int> readbackBodyIndices, string shaderDirectory) =>
        CreateDevice(world, description, data, backend, device, settings, gridSettings, gridCapacity, contactCapacity,
            contactDistance, readbackBodyIndices, shaderDirectory, new AcknowledgeSceneEvents(), default, false);

    internal static PhysicsSceneSimulation CreateDevice<TEvents>(World world, PhysicsSceneDescription description, PhysicsSceneData data,
        IGraphicsBackend backend, Device device, PhysicsSceneSettings settings, DeviceGridSettings gridSettings,
        DeviceGridCapacity gridCapacity, int contactCapacity, float contactDistance,
        ReadOnlySpan<int> readbackBodyIndices, string shaderDirectory, TEvents events, ReadOnlySpan<JobResourceAccess> eventAccesses,
        bool contactEvents = true)
        where TEvents : struct, IPhysicsEventConsumer =>
        CreateDevice<SceneBoxGeometry, DeviceSceneGeometryInputs, TEvents>(world, description, data, default,
            backend, device, settings, gridSettings, gridCapacity, contactCapacity, contactDistance,
            readbackBodyIndices, shaderDirectory, events, eventAccesses, contactEvents);

    internal static PhysicsSceneSimulation CreateDevice<TGeometry, TInputs, TEvents>(World world,
        PhysicsSceneDescription description, PhysicsSceneData data, TGeometry geometrySource,
        IGraphicsBackend backend, Device device, PhysicsSceneSettings settings, DeviceGridSettings gridSettings,
        DeviceGridCapacity gridCapacity, int contactCapacity, float contactDistance,
        ReadOnlySpan<int> readbackBodyIndices, string shaderDirectory, TEvents events,
        ReadOnlySpan<JobResourceAccess> eventAccesses, bool contactEvents)
        where TGeometry : struct, ISceneDeviceGeometry<TInputs>
        where TInputs : class, IPhysicsStage, IDisposable
        where TEvents : struct, IPhysicsEventConsumer
    {
        if (settings.JointBounds.Levels > 0 && settings.JointBoundsPerJoint == 0)
            settings = settings with { JointBoundsPerJoint = Math.Max(1, data.Joints.Length - 1) };
        var shaders = new PhysicsSceneShaders(device.Capabilities.CompileTarget, shaderDirectory);
        DeviceGridKernels gridKernels = shaders.Grid();
        DeviceBodyKernels bodyKernels = shaders.Bodies();
        DeviceBodyInputKernels bodyInputKernels = shaders.BodyInputs();
        ComputePipelineDesc[] detectionKernels = geometrySource.Detection(shaders);
        DeviceContactMatchKernels matchKernels = shaders.Matching();
        ComputePipelineDesc contactGeometryKernel = shaders.ContactGeometry();
        bool useJointBounds = settings.JointBounds.Levels > 0 && data.Joints.Length > 0;
        DeviceGraphKernels graphKernels = shaders.Graph(settings.ContactSupports.HasValue, useJointBounds);
        DeviceAvbdKernels avbdKernels = shaders.Avbd(settings.ContactSupports.HasValue, useJointBounds,
            data.Bodies.Length, contactCapacity);
        DeviceContactKernels responseKernels = shaders.Responses();

        var bodies = new DeviceBodies(backend, device, data.Bodies.Length);
        var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        var geometry = new DeviceCollisionGeometry(backend, device, geometrySource.GetCapacity(data.Bodies.Length), bodies);
        PhysicsSceneEntities created = CreateEntities(world, data, geometrySource,
            new SceneDeviceBodyBindings(bodies, equations, geometry));
        Scheduler scheduler = WorldJobs.GetScheduler(world);
        const int threads = 64;
        bool allBodies = readbackBodyIndices.Length == created.Bodies.Length;
        var readbackBodies = new Entity[readbackBodyIndices.Length];
        for (int index = 0; index < readbackBodyIndices.Length; index++)
            readbackBodies[index] = created.Bodies[readbackBodyIndices[index]];
        if (!allBodies)
            foreach (Entity body in readbackBodies) world.Add(body, new SceneReadbackBody());
        QueryDefinition filter = new QueryDefinitionBuilder().All<ScenePhysicsBody>().Build();
        QueryDefinition selectedFilter = allBodies ? filter : new QueryDefinitionBuilder().All<SceneReadbackBody>().Build();
        QueryHandle members = world.Query(filter.WithAccess(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().Build()));
        QueryHandle shapes = world.Query(filter);
        QueryHandle poses = world.Query(filter.WithAccess(new QueryDefinitionBuilder().Read<BodyPose>().Read<BodyMass>().Read<BodyStepState>().Build()));
        QueryHandle points = world.Query(new QueryDefinitionBuilder().All<ScenePhysicsContact>().Read<ContactPoint>());
        QueryHandle states = world.Query(new QueryDefinitionBuilder().All<ScenePhysicsContact>().Read<AvbdContactState>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceAvbdContactState>>());
        QueryHandle observed = world.Query(selectedFilter.WithAccess(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().Build()));
        QueryHandle eventRows = world.Query(new QueryDefinitionBuilder().All<ScenePhysicsEventStream>()
            .Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>());
        var pairs = world.Within(shapes);
        var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        var timedInputs = new DeviceBodyInputBuffers(backend, device, world, members, bodies, bodyInputKernels, threads);
        var sceneInputs = new ApplySceneInputChanges(world, created.HasInputChanges);
        var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        foreach (Entity body in created.Bodies) world.Add(body, new DeviceBodyActivityBinding(activities));
        var bindings = new Dictionary<string, ResourceBinding[]>();
        activities.AddBindings(bindings);
        var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, bodyKernels, threads,
            bindings, activities.GetWriteAccesses(scheduler));
        var detected = new ContactPointBuffer(scheduler, contactCapacity, contactCapacity * 2);
        var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, contactCapacity, "DeviceContacts");
        var anchors = new DeviceContactBodies(backend, device, contactCapacity, 2);
        var histories = new DeviceConstraintState<DeviceAvbdContactState>(backend, device, contactCapacity, "DeviceAvbdContactStates");
        var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, contactCapacity, "DeviceContactResponses");
        var supports = new DeviceSceneContactSupports(backend, device, world, created.Bodies, bodies, parameters,
            settings.ContactSupports);
        var contactInputs = new DeviceContactInputs(world, points, parameters, anchors);
        var stateInputs = new DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint, DeviceAvbdContactState, DeviceAvbdContactStateSource>(
            backend, device, world, states, parameters, histories, default);
        var selected = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device, world, points, parameters, "DeviceContactsSelection");
        var selectedStates = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, points, parameters, new(histories), "DeviceContactAvbdSelection");
        var selectedResponses = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, points, parameters, new(responses), "DeviceContactResponsesSelection");
        parameters.AddBindings(bindings); anchors.AddBindings(bindings); histories.AddBindings(bindings); responses.AddBindings(bindings);
        supports.AddBindings(bindings);
        var joints = new DeviceSceneJoints(backend, device, world, created.Joints);
        joints.AddBindings(bindings);
        var jointBounds = new DeviceSceneJointBounds(backend, device, world, members, created.Joints,
            joints.Data, settings.JointBounds, settings.JointBoundsPerJoint);
        jointBounds.AddBindings(bindings);
        var ranges = new DeviceSceneDistanceRanges(backend, device, world, created.DistanceRanges);
        ranges.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(scheduler), anchors.GetReadAccess(scheduler),
            .. joints.Data.GetReadAccesses(scheduler), .. ranges.Data.GetReadAccesses(scheduler),
            .. supports.GetReadAccesses(scheduler), .. jointBounds.GetReadAccesses(scheduler),
            .. activities.GetReadAccesses(scheduler)];
        var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, graphKernels,
            new(contactCapacity + joints.Data.Capacity + ranges.Data.Capacity + jointBounds.Capacity +
                (settings.ContactSupports.HasValue ? bodies.Capacity : 0), 2), bindings, reads,
            threads, new IDeviceConstraintSelection[] { selected, joints.Selection, ranges.Selection }
                .Concat(jointBounds.Selections).Concat(supports.Selections).ToArray());
        var supportGroups = new DeviceSceneSupportGroups(backend, device, bodies.Capacity, contactCapacity,
            settings.ContactSupports.HasValue);
        supportGroups.AddBindings(bindings);
        var pairExclusions = new DeviceScenePairExclusions(backend, device, world, created.Bodies,
            data.CollisionExclusions.Length);
        var gridBindings = new Dictionary<string, ResourceBinding[]>();
        graph.AddBindings(gridBindings);
        pairExclusions.AddBindings(gridBindings);
        var grid = new DeviceCollisionGrid(backend, device, gridKernels, gridSettings, gridCapacity, gridBindings,
            new JobResourceAccess[] { bodies.GetReadAccess(scheduler), pairExclusions.ReadAccess(scheduler) });
        for (int index = 0; index < created.Bodies.Length; index++)
            world.Add(created.Bodies[index], new DeviceCollisionBody(grid, index));
        TInputs geometryInputs = geometrySource.CreateInputs(backend, device, grid, geometry, shaders, pairs,
            settings, contactDistance, threads);
        var detection = new DeviceCollisionDetection(backend, device, grid, geometry, detectionKernels, 0,
            new(gridCapacity.Pairs, 4, Math.Min(gridCapacity.Pairs, 4096)) { BodiesPerContact = 2 }, new(1e-5f, 32, 64), contactDistance,
            DeviceInputTransfer.Resident, DeviceCollisionReadback.Contacts);
        var matching = new DeviceContactMatches(backend, device, world, points, parameters, detection, bodies, anchors, matchKernels,
            new(.02f, .99f, true), contactCapacity * 2 + 1, threads);
        var currentGeometry = matching.CreateGeometryUpdate(bodies, anchors, contactGeometryKernel);
        var refresh = ContactPoints.Create(world, poses, points, detected, new SceneContactMaterials(),
            matching.Components(new SceneDeviceContactComponents(histories, responses, supports, settings, contactEvents)), new(.02f, .99f, true));
        JobResourceAccess[] constraintAccesses = [.. reads, histories.GetWriteAccess(scheduler), responses.GetWriteAccess(scheduler),
            joints.States.GetWriteAccess(scheduler), ranges.States.GetWriteAccess(scheduler),
            .. supports.GetWriteAccesses(scheduler), .. jointBounds.GetWriteAccesses(scheduler),
            .. supportGroups.GetWriteAccesses(scheduler)];
        var solve = new DeviceAvbdSolver(backend, device, bodies, equations, graph, avbdKernels,
            new(settings.Iterations, settings.BodyIterations, settings.MultiplierRetention, 0), bindings, constraintAccesses, threads,
            new IDeviceConstraintSelection[] { selectedStates, selectedResponses, joints.StateSelection, ranges.StateSelection });
        var equationInputs = new DeviceSceneEquationInputs(solve, settings.EquationBindingsFixed, settings.Iterations);
        var response = new DeviceContactActions(backend, device, bodies, graph, responses, responseKernels, bindings, reads,
            threads, new IDeviceConstraintSelection[] { selectedResponses });
        var activityBindings = new Dictionary<string, ResourceBinding[]>(bindings);
        timedInputs.AddBindings(activityBindings);
        var activity = new DevicePhysicsActivity(backend, device, activities, graph,
            shaders.Activity(settings.ContactSupports.HasValue, useJointBounds), settings.Activity,
            activityBindings, reads.Concat(timedInputs.GetReadAccesses(scheduler)).ToArray(), threads);
        var completedEvents = new PhysicsEventBuffer(scheduler, created.Bodies.Length);
        var breakJoints = new DeviceSceneJointBreaks(backend, device, world, bodies, members, created.Joints,
            joints, shaders.JointForces(), bindings, completedEvents, threads);
        var contacts = new DeviceSceneContactEvents(backend, device, world, bodies, members, points, parameters, responses,
            shaders, bindings, reads, new IDeviceConstraintSelection[] { selected, selectedResponses }, completedEvents, contactEvents, threads);
        var eventConsumption = new ConsumePhysicsEvents<TEvents>(world, eventRows, events, eventAccesses);
        var results = allBodies
            ? new DeviceBodyResults(backend, device, bodies)
            : new DeviceBodyResults(backend, device, bodies, world, observed, readbackBodyIndices.Length);
        var statistics = new DeviceSceneStatistics(backend, device, bodies, parameters, selected, graph,
            shaders.Statistics(), bindings, reads);
        PhysicsSceneTimings? timings = settings.Timings;
        var stages = sceneInputs.Measure(timings, "Scene inputs")
            .Then(bodyInputs.Upload().Measure(timings, "Body inputs"))
            .Then(pairExclusions.Upload().Measure(timings, "Pair exclusions"))
            .Then(jointBounds.Build().Measure(timings, "Joint bound topology"))
            .Then(joints.Upload().Measure(timings, "Joint inputs"))
            .Then(jointBounds.Upload().Measure(timings, "Joint bound inputs"))
            .Then(ranges.Upload().Measure(timings, "Distance inputs"))
            .Then(timedInputs.WriteInputs().Measure(timings, "Timed body inputs"))
            .Then(timedInputs.Apply().Measure(timings, "Body impulses"))
            .Then(activity.UploadInputs().Measure(timings, "Activity inputs"))
            .Then(dynamics.UpdateSelection().Then(graph.BuildConnectivity()).Then(activity.WakeConnected(buildGraph: false))
                .Measure(timings, "Input wake"))
            .Then(dynamics.ApplyGyroscopicTorque().Measure(timings, "Gyroscopic torque"))
            .Then(dynamics.Predict(Vector(data.Gravity)).Measure(timings, "Body prediction"))
            .Then(timedInputs.ApplyKinematicTargets().Measure(timings, "Kinematic targets"))
            .Then(new UpdateDeviceSceneGeometry<TInputs>(geometryInputs))
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None)
                .Measure(timings, "Grid, pairs and type bins"))
            .Then(new ClearDeviceSceneContacts(detected).Then(detection.Detect()).Measure(timings, "Narrow phase"))
            .Then(detection.WriteContacts(new ContactPointOutput(detected), new[] { detected.WriteAccess })
                .Measure(timings, "Contact geometry readback"))
            .Then(matching.Match().Then(refresh.Refresh(currentGeometry.Results)).Measure(timings, "Persistent contacts"))
            .Then(contactInputs.Upload().Then(stateInputs.Upload()).Measure(timings, "Contact inputs and history"))
            .Then(supports.Upload().Measure(timings, "Contact support inputs"))
            .Then(supportGroups.Initialize().Measure(timings, "Support group buffers"))
            .Then(currentGeometry.Update().Then(graph.BuildConnectivity()).Then(activity.WakeConnected(buildGraph: false))
                .Measure(timings, "Contact wake"))
            .Then(dynamics.Resume(Vector(data.Gravity)).Then(graph.UpdateActiveWork()).Then(response.Initialize())
                .Measure(timings, "Resumed prediction and response inputs"))
            .Then(equationInputs.Solve().Measure(timings, "Constraint graph and AVBD"))
            .Then(breakJoints.BreakOverloaded().Measure(timings, "Joint forces and breaks"))
            .Then(dynamics.UpdateVelocities().Measure(timings, "Body velocities"))
            .Then(response.ApplyRestitution(1, buildGraph: false).Measure(timings, "Restitution"))
            .Then(activity.Finish().Then(activity.Readback(completedEvents)).Measure(timings, "Completed activity"))
            .Then(contacts.Observe().Measure(timings, "Completed contact events"))
            .Then(completedEvents.Emit(new SceneEventComponents()).Measure(timings, "Completed events"));
        var simulation = PhysicsSimulation.Create(world, filter, stages, data.Substeps);
        var consumer = results.Readback(threads).Measure(timings, "Body readback")
            .Then(statistics.Readback().Measure(timings, "Completed scene statistics"))
            .Then(new CopyObservedSceneTransforms(selectedFilter).Measure(timings, "Body transforms"))
            .Then(eventConsumption.Measure(timings, "Event consumption"));
        var frame = simulation.CreateFrame(consumer, data.StepSeconds, 0, 0, 0);
        var initialize = new PhysicsStepContext(world, filter, 0, 0, 1, 0, data.StepSeconds);
        parameters.BeginInputs().Then(parameters.UploadInputs()).Schedule(in initialize, default).Complete();
        return new PhysicsSceneSimulation(world, description, settings, created.Bodies, created.Joints, created.DistanceRanges,
            data.StepSeconds, frame, frame.CompleteForRead, [members, shapes, poses, points, states, observed, eventRows],
            [bodies, equations, grid, geometry, bodyInputs, timedInputs, sceneInputs, dynamics, geometryInputs,
                detection, detected, parameters, anchors, histories, responses, matching, currentGeometry, refresh,
                contactInputs, stateInputs, selected, selectedStates, selectedResponses, joints, ranges, graph, solve, response,
                results, statistics, activities, activity, completedEvents, eventConsumption, breakJoints, supports,
                pairExclusions, contacts, jointBounds, supportGroups],
            statistics, readbackBodies);
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
    DeviceConstraintState<DeviceContactResponseState> responses, DeviceSceneContactSupports supports,
    PhysicsSceneSettings settings, bool events) : IContactPointComponents
{
    public void AddReads(World world, List<JobResourceAccess> accesses) { }
    public void Add(ref JobCommandWriter commands, Entity point, in ContactPoint geometry,
        ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second, in PhysicsStepContext context)
    {
        commands.Add(point, new ScenePhysicsContact());
        new AvbdContactComponents(new AvbdContactState
        {
            NormalPenalty = settings.ContactNormalPenalty, TangentPenalty = settings.ContactTangentPenalty,
            FrictionRegularizationSpeed = settings.FrictionRegularizationSpeed,
        }).Add(ref commands, point, in geometry, first, second, in context);
        commands.Add(point, new DeviceAvbdState<DeviceAvbdContactState>(histories));
        commands.Add(point, new DeviceContactResponseBinding(responses));
        supports.AddContact(ref commands, point);
        if (events) commands.Add(point, new ContactEventState<ScenePhysicsEventStream>());
    }
}
