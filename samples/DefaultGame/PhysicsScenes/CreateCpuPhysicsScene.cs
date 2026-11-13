using System.Numerics;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Spatial;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

public static partial class PhysicsScenes
{
    public static PhysicsSceneSimulation CreateCpu<TCollision>(World world, PhysicsSceneDescription description,
        TCollision collision, PhysicsSceneSettings settings)
        where TCollision : struct, ICollisionNarrowPhase<BoxGeometry, BoxGeometry> =>
        CreateCpu(world, description, Create(description), collision, settings);

    internal static PhysicsSceneSimulation CreateCpu<TCollision>(World world, PhysicsSceneDescription description,
        PhysicsSceneData data, TCollision collision, PhysicsSceneSettings settings)
        where TCollision : struct, ICollisionNarrowPhase<BoxGeometry, BoxGeometry> =>
        CreateCpu(world, description, data, collision, settings, new AcknowledgeSceneEvents(), default);

    public static PhysicsSceneSimulation CreateCpu<TCollision, TEvents>(World world, PhysicsSceneDescription description,
        TCollision collision, PhysicsSceneSettings settings, TEvents events, ReadOnlySpan<JobResourceAccess> eventAccesses = default)
        where TCollision : struct, ICollisionNarrowPhase<BoxGeometry, BoxGeometry>
        where TEvents : struct, IScenePhysicsEvents =>
        CreateCpu(world, description, Create(description), collision, settings, events, eventAccesses);

    internal static PhysicsSceneSimulation CreateCpu<TCollision, TEvents>(World world, PhysicsSceneDescription description,
        PhysicsSceneData data, TCollision collision, PhysicsSceneSettings settings, TEvents events,
        ReadOnlySpan<JobResourceAccess> eventAccesses)
        where TCollision : struct, ICollisionNarrowPhase<BoxGeometry, BoxGeometry>
        where TEvents : struct, IScenePhysicsEvents
    {
        PhysicsSceneEntities created = CreateEntities(world, data, default(SceneBoxGeometry),
            new SceneCpuBodyBindings(data, settings.ContactSupports.HasValue, events.ReadsEvents));
        Entity[] entities = created.Bodies, jointEntities = created.Joints, rangeEntities = created.DistanceRanges;
        if (settings.JointBounds.Levels > 0)
        {
            using var structure = world.OpenStructureWrite();
            foreach (Entity entity in jointEntities) world.AddBuffer<LongRangeJointBound>(entity);
        }
        var inputs = new ApplySceneInputChanges(world, created.HasInputChanges);
        Scheduler scheduler = world.Execution!.Scheduler!;
        Query filter = world.Query(new QueryDescription().With<ScenePhysicsBody>());
        Query shapes = world.Query(new QueryDescription().With<ScenePhysicsBody>().With<CollisionBounds>());
        Query bodies = world.Query(new QueryDescription().With<ScenePhysicsBody>().With<BodyPose>().With<BodyMass>()
            .With<BodyStepState>().With<BodyVelocity>().With<AvbdBodyEquation>());
        Query supportBodies = world.Query(new QueryDescription().With<ScenePhysicsBody>().With<BodyMass>()
            .With<LongRangeBodySupport>().With<Buffer<LongRangeSupportLoad>>().With<Buffer<LongRangeSupportEquation>>());
        Query joints = world.Query(new QueryDescription().With<ScenePhysicsJoint>().With<JointConstraint>()
            .With<RelationEndpoints<JointConstraint>>().With<AvbdJointState>());
        Query breakableJoints = world.Query(new QueryDescription().With<ScenePhysicsJoint>().With<ConstraintBreakSettings>()
            .With<JointConstraint>().With<RelationEndpoints<JointConstraint>>().With<AvbdJointState>());
        Query ranges = world.Query(new QueryDescription().With<DistanceRange>()
            .With<RelationEndpoints<DistanceRange>>().With<AvbdDistanceRangeState>());
        Query jointBounds = world.Query(new QueryDescription().With<ScenePhysicsJoint>().With<JointConstraint>()
            .With<RelationEndpoints<JointConstraint>>().With<Buffer<LongRangeJointBound>>());
        var eventBuffer = new PhysicsEventBuffer(world, entities.Length);
        var candidates = Collision.Candidates<CollisionBounds, CollisionBoundsReader>(world, shapes, new BoundsTree(), default);
        var maintenance = candidates.CreateMaintenance(scheduler);
        var bins = new CollisionPairBins<BoundsNodePair>(scheduler, entities.Length, 2);
        var detected = new ContactPointBuffer(scheduler, collision.ContactCapacity, entities.Length * 4);
        var narrow = CollisionGeometry<BoxGeometry, BoxGeometry>.Predict(new ComponentGeometryMotion<BoxGeometry>(),
            new ComponentGeometryMotion<BoxGeometry>(), collision, new ContactPointOutput(detected), settings.ContactDistance);
        var detection = DetectContactPoints.Create(world, maintenance.Query, bins, new SceneBoxPairs(),
            StepContactNarrowPhase.Create(0, narrow), detected, [maintenance.ReadAccess]);
        var consumption = new ConsumeSceneEvents<TEvents>(scheduler, eventBuffer, events, eventAccesses);
        if (settings.ContactSupports is { } supports)
        {
            var manifolds = new ContactManifolds<AvbdSupportedContactState>(scheduler, new()
            {
                Contact = new AvbdContactState { NormalPenalty = settings.ContactNormalPenalty,
                    TangentPenalty = settings.ContactTangentPenalty, FrictionRegularizationSpeed = settings.FrictionRegularizationSpeed },
            });
            var contacts = new AvbdContactConstraints<AvbdSupportedContactState, SceneSupportedContactPenaltyMath>(manifolds);
            return Build(manifolds.Columns, new RefreshContactManifolds<AvbdSupportedContactState, SceneContactMaterials>(world,
                bodies, manifolds, detected, new(), new(.02f, .99f, true)),
                AvbdGraphConstraints.Combine(contacts, new ContactLongRangeConstraints(supportBodies, manifolds, supports)), manifolds);
        }
        else
        {
            var manifolds = new ContactManifolds<AvbdContactState>(scheduler, new()
            {
                NormalPenalty = settings.ContactNormalPenalty, TangentPenalty = settings.ContactTangentPenalty,
                FrictionRegularizationSpeed = settings.FrictionRegularizationSpeed,
            });
            return Build(manifolds.Columns, new RefreshContactManifolds<AvbdContactState, SceneContactMaterials>(world,
                bodies, manifolds, detected, new(), new(.02f, .99f, true)),
                new AvbdContactConstraints<AvbdContactState, SceneContactPenaltyMath>(manifolds), manifolds);
        }

        PhysicsSceneSimulation Build<TRefresh, TContacts>(ContactColumns columns, TRefresh refresh, TContacts contactConstraints,
            IDisposable manifolds) where TRefresh : struct, IPhysicsStage where TContacts : struct, IAvbdGraphConstraints
        {
            var ordinary = AvbdGraphConstraints.Combine(AvbdGraphConstraints.Combine(contactConstraints,
                new AvbdConstraints<JointConstraint, AvbdJointMath>(joints)),
                new AvbdConstraints<DistanceRange, AvbdDistanceRangeMath>(ranges));
            var constraints = AvbdGraphConstraints.Combine(ordinary, new JointLongRangeConstraints(jointBounds, settings.JointBounds));
            var solve = SolveAvbdGraph.Create(world, bodies, constraints, settings.Graph, settings.Iterations,
                settings.MultiplierRetention, 0, bodyIterations: settings.BodyIterations);
            var activity = solve.Activity(settings.Activity, eventBuffer);
            var breakJoints = new BreakConstraints<JointConstraint, AvbdJointMath>(world, bodies, breakableJoints, eventBuffer);
            var prediction = new PredictActiveBodies(world, bodies, Vector(data.Gravity), 64);
            var contactEvents = new ContactEvents(world, bodies, columns.Contacts, eventBuffer, .001f);
            var responses = new InitializeContactResponses(world, bodies, columns);
            PhysicsSceneTimings? timings = settings.Timings;
            var refreshContacts = new UpdatePredictedBounds<BoxGeometry>(settings.ContactDistance, 64).Measure(timings, "Dynamic bounds")
                .Then(new RefitSceneBounds(maintenance).Measure(timings, "Spatial maintenance"))
                .Then(detection.Measure(timings, "Pairs, type bins and narrow phase"))
                .Then(refresh.Measure(timings, "Persistent contacts"))
                .Then(activity.WakeConnected().Measure(timings, "Contact wake"))
                .Then(prediction.Resume().Measure(timings, "Resumed prediction"))
                .Then(responses.Measure(timings, "Contact response inputs"));
            var stages = inputs.Measure(timings, "Scene inputs")
                .Then(new ApplyBodyInputs(64).Measure(timings, "Body inputs"))
                .Then(activity.WakeConnected().Measure(timings, "Input wake"))
                .Then(new ApplyGyroscopicTorque(64).Measure(timings, "Gyroscopic torque"))
                .Then(prediction.Measure(timings, "Active prediction"))
                .Then(refreshContacts)
                .Then(PhysicsIterations.Create(solve, refreshContacts, settings.Iterations, data.ContactDetectionInterval).Measure(timings, "Constraint graph and AVBD"))
                .Then(breakJoints.Measure(timings, "Joint breaks"))
                .Then(new UpdateBodyVelocities(64).Measure(timings, "Body velocities"))
                .Then(contactEvents.Measure(timings, "Contact events"))
                .Then(activity.Finish().Measure(timings, "Activity results"))
                .Then(eventBuffer.Emit().Measure(timings, "Completed events"));
            var simulation = PhysicsSimulation.Create(world, filter, stages, data.Substeps);
            var frame = simulation.CreateFrame(new CopySceneBodyTransforms().Measure(timings, "Body transforms"),
                consumption.Read().Measure(timings, "Event consumption"), data.StepSeconds, settings.MaximumResultDelay, 0, 0);
            return new PhysicsSceneSimulation(world, description, settings, entities, jointEntities, rangeEntities,
                data.StepSeconds, frame, [shapes, bodies, supportBodies, joints, breakableJoints, ranges, jointBounds],
                [inputs, maintenance, bins, detected, solve, activity, breakJoints, prediction,
                    eventBuffer, contactEvents, consumption, manifolds], contactColumns: columns);
        }
    }

    internal static PhysicsSceneEntities CreateEntities(World world, PhysicsSceneData data) =>
        CreateEntities(world, data, default(SceneBoxGeometry));

    internal static PhysicsSceneEntities CreateEntities<TGeometry>(World world, PhysicsSceneData data, TGeometry geometry)
        where TGeometry : struct, ISceneBodyGeometry =>
        CreateEntities(world, data, geometry, default(SceneBodyBindings));

    internal static PhysicsSceneEntities CreateEntities<TGeometry, TBindings>(World world, PhysicsSceneData data,
        TGeometry geometry, TBindings bindings)
        where TGeometry : struct, ISceneBodyGeometry
        where TBindings : struct, ISceneBodyBindings
    {
        using var structure = world.OpenStructureWrite();
        Entity[] entities = CreateBodies(world, data, geometry, bindings);

        var jointEntities = new Entity[data.Joints.Length];
        for (int index = 0; index < jointEntities.Length; index++)
        {
            SceneJointData input = data.Joints[index];
            var source = new JointFrame(Vector(input.PointA), Quaternion.Identity);
            Quaternion sourceRotation = world.Read<BodyPose>(entities[input.BodyA]).Rotation;
            Quaternion targetRotation = world.Read<BodyPose>(entities[input.BodyB]).Rotation;
            var target = new JointFrame(Vector(input.PointB), Quaternion.Normalize(Quaternion.Conjugate(targetRotation) * sourceRotation));
            float linear = (float)(data.Materials?.JointLinearStiffness ?? double.PositiveInfinity);
            float angular = (float)(data.Materials?.JointAngularStiffness ?? double.PositiveInfinity);
            JointConstraint joint = input.AngularFixed ? JointConstraints.CreateFixed(source, target, linear, angular)
                : JointConstraints.CreateBall(source, target, linear);
            var endpoints = new RelationEndpoints<JointConstraint>(entities[input.BodyA], entities[input.BodyB]);
            var state = new AvbdJointState { LinearPenalty = new Vector3(1e6f), AngularPenalty = new Vector3(1e6f) };
            jointEntities[index] = input.BreakLimits is { } limits
                ? world.Create(endpoints, joint, new ScenePhysicsJoint(), state, limits)
                : world.Create(endpoints, joint, new ScenePhysicsJoint(), state);
        }
        foreach (SceneCollisionPair pair in data.CollisionExclusions)
            world.CreateRelation(entities[pair.BodyA], entities[pair.BodyB], new SceneCollisionExclusion());
        var rangeEntities = new Entity[data.DistanceRanges?.Length ?? 0];
        for (int index = 0; index < rangeEntities.Length; index++)
        {
            SceneDistanceRange input = data.DistanceRanges![index];
            rangeEntities[index] = world.Create(new RelationEndpoints<DistanceRange>(entities[input.BodyA], entities[input.BodyB]),
                new DistanceRange { FirstAnchor = Vector(input.PointA), SecondAnchor = Vector(input.PointB),
                    Minimum = (float)input.Minimum, Maximum = (float)input.Maximum, Stiffness = float.PositiveInfinity },
                new AvbdDistanceRangeState { Penalty = 1e6f });
        }
        var inputChanges = new List<SceneInputChange>();
        foreach (SceneInputEvent change in data.Events.OrderBy(change => change.Frame))
        {
            if (change.RemovedJoint is int joint)
                inputChanges.Add(new(change.Frame, jointEntities[joint], SceneInputChangeKind.RemoveJoint));
            if (change.Body is int body && change.Position is { } position)
                inputChanges.Add(new(change.Frame, entities[body], SceneInputChangeKind.MoveBody, Vector(position)));
        }
        if (inputChanges.Count > 0)
        {
            Entity changes = world.Create(new SceneInputIndex(), default(Buffer<SceneInputChange>));
            using var values = world.GetBuffer<SceneInputChange>(changes);
            foreach (SceneInputChange change in inputChanges) values.Add(change);
        }

        return new(entities, jointEntities, rangeEntities, inputChanges.Count > 0);
    }

    private static Vector3 Vector(double[] value) => new((float)value[0], (float)value[1], (float)value[2]);
    private static Quaternion Rotation(double[] value) => new((float)value[0], (float)value[1], (float)value[2], (float)value[3]);
    private static float Inverse(double value) => value == 0 ? 0 : (float)(1 / value);
}

internal readonly struct ScenePhysicsBody : IComponent;
internal readonly struct ScenePhysicsContact : IComponent;
internal readonly struct ScenePhysicsJoint : IComponent;

[RelationSchema(RelationDirection.Undirected)]
internal readonly struct SceneCollisionExclusion : IComponent;

internal readonly struct SceneBoxPairs : ICollisionPairClassifier
{
    public QueryDescription AddReads(QueryDescription definition) => definition.Optional<BodyMass>();
    public CollisionPairType Classify(World world, Entity first, Entity second) =>
        world.Read<BodyMass>(first).Motion == BodyMotion.Static && world.Read<BodyMass>(second).Motion == BodyMotion.Static
            || Relations<SceneCollisionExclusion>.FindPair(world, first, second).Entity != Entity.Null
            ? new(1, 0, 0) : new(0, 0, 0);
}

internal readonly struct SceneContactMaterials : IContactMaterialCombine
{
    public ContactMaterial Combine(in ContactMaterial first, in ContactMaterial second)
    {
        ContactMaterial combined = new AverageContactMaterials().Combine(in first, in second);
        combined.StaticFriction = MathF.Sqrt(first.StaticFriction * second.StaticFriction);
        combined.DynamicFriction = MathF.Sqrt(first.DynamicFriction * second.DynamicFriction);
        return combined;
    }
}

internal readonly struct RefitSceneBounds(SpatialMaintenance<CollisionBounds, CollisionBoundsReader, SpatialAll, GroupMatch> maintenance) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => maintenance.Schedule(dependency);
}

internal readonly struct CopySceneBodyTransforms : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        new CopySceneTransformJob().ScheduleParallel(context.World,
            new JobEntityScheduleOptions(filter: context.Bodies), dependency);
}

internal readonly partial struct CopySceneTransformJob : IJobEntity
{
    public void Execute(in BodyPose pose, ref LocalTransform transform)
    {
        transform.Value.Position = pose.Position;
        transform.Value.Rotation = pose.Rotation;
    }
}
