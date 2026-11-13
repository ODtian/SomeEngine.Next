using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
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
        where TEvents : struct, IScenePhysicsEvents =>
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
        where TEvents : struct, IScenePhysicsEvents =>
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
        where TEvents : struct, IScenePhysicsEvents
    {
        if (settings.JointBounds.Levels > 0 && settings.JointBoundsPerJoint == 0)
            settings = settings with { JointBoundsPerJoint = settings.JointBounds.Levels + 1 };
        var shaders = new PhysicsSceneShaders(device.Capabilities.CompileTarget, shaderDirectory);
        DeviceGridKernels gridKernels = shaders.Grid();
        DeviceBodyKernels bodyKernels = shaders.Bodies();
        DeviceBodyInputKernels bodyInputKernels = shaders.BodyInputs();
        ComputePipelineDesc[] detectionKernels = geometrySource.Detection(shaders);
        DeviceContactMatchKernels matchKernels = shaders.Matching();
        bool useJointBounds = settings.JointBounds.Levels > 0 && data.Joints.Length > 0;
        DeviceGraphKernels graphKernels = shaders.Graph(settings.ContactSupports.HasValue, useJointBounds);
        AvbdGraphKernels avbdKernels = shaders.Avbd(settings.ContactSupports.HasValue, useJointBounds,
            data.Bodies.Length, contactCapacity, hasJoints: data.Joints.Length > 0,
            hasDistanceRanges: data.DistanceRanges is { Length: > 0 });
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
        {
            using var commands = new CommandBuffer(world);
            foreach (Entity body in readbackBodies) commands.Add(body, new SceneReadbackBody());
            commands.Playback();
        }
        QueryDescription filter = new QueryDescription().WithStored<ScenePhysicsBody>();
        QueryDescription selectedFilter = allBodies ? filter : new QueryDescription().WithStored<SceneReadbackBody>();
        Query members = world.Query(new QueryDescription().WithStored<ScenePhysicsBody>().WithAccess(new QueryDescription().WithStored<DeviceBodyBinding>()));
        Query shapes = world.Query(filter);
        Query observed = world.Query(selectedFilter.WithAccess(new QueryDescription().WithStored<DeviceBodyBinding>()));
        var pairs = world.Within(shapes);
        var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        var timedInputs = new DeviceBodyInputBuffers(backend, device, world, members, bodies, bodyInputKernels, threads);
        var sceneInputs = new ApplySceneInputChanges(world, created.HasInputChanges);
        var activities = new DeviceBodyActivities(backend, device, world, members, bodies, resultMembers: observed);
        using (var commands = new CommandBuffer(world))
        {
            foreach (Entity body in created.Bodies) commands.Add(body, new DeviceBodyActivityBinding(activities));
            commands.Playback();
        }
        var bindings = new Dictionary<string, ResourceBinding[]>();
        activities.AddBindings(bindings);
        var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, bodyKernels, threads,
            bindings, activities.GetWriteAccesses(scheduler));
        var manifolds = new DeviceContactManifolds<DeviceAvbdContactState>(backend, device,
            new(gridCapacity.Pairs, contactCapacity, contactCapacity * 2) { SupportStates = settings.ContactSupports.HasValue }, new()
            {
                NormalPenalty = settings.ContactNormalPenalty,
                TangentPenalty = settings.ContactTangentPenalty,
                FrictionRegularizationSpeed = settings.FrictionRegularizationSpeed
            });
        var joints = new DeviceSceneJoints(backend, device, world, created.Joints);
        joints.AddBindings(bindings);
        var jointBounds = new DeviceSceneJointBounds(backend, device, world, members, created.Joints,
            joints.Data, settings.JointBounds, settings.JointBoundsPerJoint);
        jointBounds.AddBindings(bindings);
        var ranges = new DeviceSceneDistanceRanges(backend, device, world, created.DistanceRanges);
        ranges.AddBindings(bindings);
        var graphCapacity = new DeviceGraphCapacity(contactCapacity + joints.Data.Capacity + ranges.Data.Capacity +
            jointBounds.Capacity + (settings.ContactSupports.HasValue ? bodies.Capacity : 0), 2);
        var supports = new DeviceSceneContactSupports(backend, device, world, created.Bodies, bodies, manifolds,
            settings.ContactSupports, graphCapacity);
        manifolds.Current.AddBindings(bindings);
        supports.AddBindings(bindings);
        JobResourceAccess[] reads = [..joints.Data.GetReadAccesses(scheduler),..ranges.Data.GetReadAccesses(scheduler),
            ..supports.GetReadAccesses(scheduler),..jointBounds.GetReadAccesses(scheduler),..activities.GetReadAccesses(scheduler)];
        var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, graphKernels,
            graphCapacity, bindings, reads,
            threads, new IDeviceConstraintSelection[] { joints.Selection, ranges.Selection }
                .Concat(jointBounds.Selections).Concat(supports.Selections).ToArray(), contacts: manifolds);
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
        using (var commands = new CommandBuffer(world))
        {
            for (int index = 0; index < created.Bodies.Length; index++)
                commands.Add(created.Bodies[index], new DeviceCollisionBody(grid, index));
            commands.Playback();
        }
        TInputs geometryInputs = geometrySource.CreateInputs(backend, device, grid, geometry, shaders, pairs,
            settings, contactDistance, threads);
        var detection = new DeviceCollisionDetection(backend, device, grid, geometry, detectionKernels, 0,
            new(gridCapacity.Pairs, 4, Math.Min(gridCapacity.Pairs, 4096)) { BodiesPerContact = 2, Contacts = contactCapacity, Distances = false }, new(1e-5f, 32, 64), contactDistance,
            DeviceInputTransfer.Resident, DeviceCollisionReadback.None);
        var matching = new DeviceContactMatches<DeviceAvbdContactState>(backend, device, manifolds, detection, bodies, matchKernels,
            new(.02f, .99f, true), new() { NormalStiffness = float.PositiveInfinity }, Math.Max(257, bodies.Capacity * 4 + 1));
        var stateInputs = new ContactStateInitialization<DeviceAvbdContactState>(backend, device, matching, shaders.ContactStateInitialization());
        JobResourceAccess[] constraintAccesses = [..reads,joints.States.GetWriteAccess(scheduler),ranges.States.GetWriteAccess(scheduler),
            ..supports.GetWriteAccesses(scheduler),..jointBounds.GetWriteAccesses(scheduler),..supportGroups.GetWriteAccesses(scheduler)];
        var solve = new AvbdGraphCommands(backend, device, bodies, equations, graph, avbdKernels,
            new(settings.Iterations, settings.BodyIterations, settings.MultiplierRetention, 0), bindings, constraintAccesses, threads,
            new IDeviceConstraintSelection[] { joints.StateSelection, ranges.StateSelection },
            equationBindingsFixed: settings.EquationBindingsFixed);
        var equationInputs = SolveAvbdGraph.Create(solve, buildGraph: false);
        var response = new DeviceContactActions(backend, device, bodies, graph, manifolds, responseKernels, bindings, reads,
            threads, Array.Empty<IDeviceConstraintSelection>());
        var activityBindings = new Dictionary<string, ResourceBinding[]>(bindings);
        timedInputs.AddBindings(activityBindings);
        var activity = new DevicePhysicsActivity(backend, device, activities, graph,
            shaders.Activity(settings.ContactSupports.HasValue, useJointBounds), settings.Activity,
            activityBindings, reads.Concat(timedInputs.GetReadAccesses(scheduler)).ToArray(), threads);
        var completedEvents = new PhysicsEventBuffer(world, created.Bodies.Length);
        var breakJoints = new DeviceSceneJointBreaks(backend, device, world, bodies, members, created.Joints,
            joints, shaders.JointForces(), bindings, completedEvents, threads);
        DeviceContactResults? contactResults = settings.ReadContacts || contactEvents && events.ReadsEvents && events.RequiresContactDetails
            ? new(backend, device, scheduler) : null;
        var contacts = new DeviceSceneContactEvents(backend, device, world, bodies, manifolds, contactResults, shaders, completedEvents,
            contactEvents && events.ReadsEvents, events.RequiresContactDetails);
        var eventConsumption = new ConsumeSceneEvents<TEvents>(scheduler, completedEvents, events, eventAccesses);
        var results = allBodies
            ? new DeviceBodyResults(backend, device, bodies)
            : new DeviceBodyResults(backend, device, bodies, world, observed, readbackBodyIndices.Length);
        var statistics = new DeviceSceneStatistics(backend, device, bodies, manifolds, graph,
            shaders.Statistics(), bindings, reads);
        PhysicsSceneTimings? timings = settings.Timings;
        var refreshContacts = new UpdateDeviceSceneGeometry<TInputs>(geometryInputs)
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None)
                .Measure(timings, "Grid, pairs and type bins"))
            .Then(detection.Detect().Measure(timings, "Narrow phase"))
            .Then(matching.Match().Measure(timings, "Contact matching"))
            .Then(supportGroups.Initialize().Measure(timings, "Support group buffers"))
            .Then(stateInputs.Initialize().Then(supports.Upload())
                .Then(graph.BuildConnectivity()).Then(activity.WakeConnected(buildGraph: false))
                .Measure(timings, "Contact wake"))
            .Then(dynamics.Resume(Vector(data.Gravity)).Then(graph.UpdateActiveWork()).Then(response.Initialize())
                .Measure(timings, "Resumed prediction and response inputs"));
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
            .Then(refreshContacts)
            .Then(PhysicsIterations.Create(equationInputs, refreshContacts, settings.Iterations, data.ContactDetectionInterval).Measure(timings, "Constraint graph and AVBD"))
            .Then(breakJoints.BreakOverloaded().Measure(timings, "Joint forces and breaks"))
            .Then(dynamics.UpdateVelocities().Measure(timings, "Body velocities"))
            .Then(response.ApplyRestitution(1, buildGraph: false).Measure(timings, "Restitution"))
            .Then(activity.Finish().Then(activity.Readback(completedEvents)).Measure(timings, "Completed activity"))
            .Then(contacts.Observe().Measure(timings, "Completed contact events"))
            .Then(completedEvents.Emit().Measure(timings, "Completed events"));
        var simulation = PhysicsSimulation.Create(world, shapes, stages, data.Substeps);
        var capture = results.Readback(threads).Measure(timings, "Body readback")
            .Then(new CaptureDeviceSceneContacts(contactResults, manifolds, settings.ReadContacts))
            .Then(statistics.Readback().Measure(timings, "Completed scene statistics"))
            .Then(new CopyObservedSceneTransforms(selectedFilter).Measure(timings, "Body transforms"));
        var frame = simulation.CreateFrame(capture, eventConsumption.Read().Measure(timings, "Event consumption"),
            data.StepSeconds, settings.MaximumResultDelay, 0, 0);
        IDisposable[] resources = [bodies,equations,grid,geometry,bodyInputs,timedInputs,sceneInputs,dynamics,geometryInputs,
            detection,manifolds,matching,stateInputs,joints,ranges,graph,solve,response,results,statistics,activities,activity,
            completedEvents,eventConsumption,breakJoints,supports,pairExclusions,contacts,jointBounds,supportGroups];
        if (contactResults is not null) resources = [.. resources, contactResults];
        return new PhysicsSceneSimulation(world, description, settings, created.Bodies, created.Joints, created.DistanceRanges,
            data.StepSeconds, frame, [members, shapes, observed],
            resources,
            statistics, readbackBodies, contacts: contactResults?.Contacts);
    }

}

internal readonly struct SceneReadbackBody : IComponent;

internal readonly struct ClearDeviceSceneContacts(ContactPointBuffer contacts) : IPhysicsStage, IJob
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        WorldJobs.GetScheduler(context.World).Schedule(this, new[] { contacts.WriteAccess }, dependency);
    public void Execute() => contacts.Clear();
}

internal readonly struct CopyObservedSceneTransforms(QueryDescription selected) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        PhysicsStepContext observed = context with { Bodies = context.World.Query(selected) };
        return new CopySceneBodyTransforms().Schedule(in observed, dependency);
    }
}


internal readonly struct CaptureDeviceSceneContacts(DeviceContactResults? results, DeviceContactManifolds<DeviceAvbdContactState> contacts, bool enabled) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        if (!enabled) return dependency;
        if (results!.Contacts.Step.Number == context.Number && results.Contacts.Step.EndTime == context.EndTime)
            return WorldJobs.GetScheduler(context.World).CombineDependencies([dependency, results.Contacts.Access.Completed]);
        return results.Readback(contacts).Schedule(in context, dependency);
    }
}
