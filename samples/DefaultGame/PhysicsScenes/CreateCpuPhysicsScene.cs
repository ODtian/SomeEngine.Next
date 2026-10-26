using System.Numerics;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
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
        where TCollision : struct, ICollisionNarrowPhase<BoxGeometry, BoxGeometry>
    {
        PhysicsSceneEntities created = CreateEntities(world, data);
        Entity[] entities = created.Bodies, jointEntities = created.Joints, rangeEntities = created.DistanceRanges;
        foreach (Entity entity in entities) world.Add(entity, new AvbdBodyEquation());
        var inputs = new ApplySceneInputChanges(world, created.HasInputChanges);
        Scheduler scheduler = WorldJobs.GetScheduler(world);
        QueryDefinition filter = new QueryDefinitionBuilder().All<ScenePhysicsBody>().Build();
        QueryHandle shapes = world.Query(filter.WithAccess(new QueryDefinitionBuilder().Read<CollisionBounds>().Build()));
        QueryHandle poses = world.Query(filter.WithAccess(new QueryDefinitionBuilder().Read<BodyPose>().Read<BodyMass>().Read<BodyStepState>().Build()));
        QueryHandle bodies = world.Query(filter.WithAccess(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>().Build()));
        QueryHandle scope = world.Query(new QueryDefinitionBuilder().All<ScenePhysicsContact>().Read<ContactPoint>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().All<ScenePhysicsContact>().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().All<ScenePhysicsJoint>().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<AvbdJointState>());
        QueryHandle ranges = world.Query(new QueryDefinitionBuilder().Read<DistanceRange>()
            .Read<UndirectedRelationEndpoints<DistanceRange>>().ReadWrite<AvbdDistanceRangeState>());
        var candidates = Collision.Candidates<CollisionBounds, CollisionBoundsReader>(world, shapes, new CollisionTree(), default);
        var maintenance = candidates.CreateMaintenance(scheduler);
        var bins = new CollisionPairBins<CollisionNodePair>(scheduler, entities.Length, 2);
        var detected = new ContactPointBuffer(scheduler, collision.ContactCapacity, entities.Length * 4);
        var narrow = CollisionGeometry<BoxGeometry, BoxGeometry>.Predict(new ComponentGeometryMotion<BoxGeometry>(),
            new ComponentGeometryMotion<BoxGeometry>(), collision, new ContactPointOutput(detected), 0);
        var detection = DetectContactPoints.Create(world, maintenance.Query, bins, new SceneBoxPairs(world),
            StepContactNarrowPhase.Create(0, narrow), detected, [maintenance.ReadAccess]);
        var points = ContactPoints.Create(world, poses, scope, detected, new SceneContactMaterials(), new SceneContactComponents(),
            new ContactRefreshSettings(.02f, .99f, true));
        var constraints = AvbdGraphConstraints.Combine(
            AvbdGraphConstraints.Combine(new AvbdGraphConstraints<ContactPoint, AvbdContactMath>(contacts),
                new AvbdGraphConstraints<JointConstraint, AvbdJointMath>(joints)),
            new AvbdGraphConstraints<DistanceRange, AvbdDistanceRangeMath>(ranges));
        var solve = SolveAvbdGraph.Create(world, bodies, constraints,
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 16, 16, 64, 4, false), settings.Iterations, .8f, 0,
            bodyIterations: settings.BodyIterations);
        var stages = inputs.Then(new ApplyBodyInputs(64)).Then(new ApplyGyroscopicTorque(64)).Then(new PredictBodies(Vector(data.Gravity), 64))
            .Then(new UpdatePredictedBounds<BoxGeometry>(0, 64)).Then(new RefitSceneBounds(maintenance))
            .Then(detection).Then(points.Refresh()).Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(solve).Then(new UpdateBodyVelocities(64));
        var simulation = PhysicsSimulation.Create(world, filter, stages, data.Substeps);
        var frame = simulation.CreateFrame(new CopySceneBodyTransforms(), data.StepSeconds, 0, 0, 0);
        return new PhysicsSceneSimulation(world, description, settings, entities, jointEntities, rangeEntities, data.StepSeconds, frame, frame.CompleteForRead,
            [shapes, poses, bodies, scope, contacts, joints, ranges], [inputs, maintenance, bins, detected, detection, points, solve]);
    }

    internal static PhysicsSceneEntities CreateEntities(World world, PhysicsSceneData data)
    {
        var entities = new Entity[data.Bodies.Length];
        for (int index = 0; index < entities.Length; index++)
        {
            SceneBodyData input = data.Bodies[index];
            Entity entity = entities[index] = world.CreateEntity(new ScenePhysicsBody());
            var pose = new BodyPose { Position = Vector(input.Position), Rotation = Rotation(input.Rotation) };
            world.Add(entity, pose);
            world.Add(entity, new BodyMass
            {
                Motion = input.Mass == 0 ? BodyMotion.Static : BodyMotion.Dynamic,
                InverseMass = Inverse(input.Mass),
                InverseInertia = new Matrix4x4 { M11 = Inverse(input.Inertia[0]), M22 = Inverse(input.Inertia[1]), M33 = Inverse(input.Inertia[2]) },
            });
            world.Add(entity, new BodyVelocity { Linear = Vector(input.Velocity), Angular = Vector(input.AngularVelocity) });
            world.Add(entity, new BodyStepState());
            world.Add(entity, new BoxGeometry(Vector(input.Size) * .5f)); world.Add(entity, new CollisionBounds());
            world.Add(entity, new ContactMaterial { NormalStiffness = (float)(data.Materials?.ContactStiffness ?? double.PositiveInfinity),
                NormalDamping = (float)(data.Materials?.ContactDamping ?? 0),
                StaticFriction = (float)input.Friction, DynamicFriction = (float)input.Friction });
            world.Add(entity, new LocalTransform { Value = new TransformQvvs(pose.Position, pose.Rotation) { Stretch = Vector(input.Size) } });
        }

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
            Entity entity = jointEntities[index] = world.CreateRelation(entities[input.BodyA], entities[input.BodyB], joint).Entity;
            world.Add(entity, new ScenePhysicsJoint());
            world.Add(entity, new AvbdJointState { LinearPenalty = new Vector3(1e6f), AngularPenalty = new Vector3(1e6f) });
        }
        foreach (SceneCollisionPair pair in data.CollisionExclusions)
            world.CreateRelation(entities[pair.BodyA], entities[pair.BodyB], new SceneCollisionExclusion());
        var rangeEntities = new Entity[data.DistanceRanges?.Length ?? 0];
        for (int index = 0; index < rangeEntities.Length; index++)
        {
            SceneDistanceRange input = data.DistanceRanges![index];
            Entity entity = rangeEntities[index] = world.CreateRelation(entities[input.BodyA], entities[input.BodyB], new DistanceRange
            {
                FirstAnchor = Vector(input.PointA), SecondAnchor = Vector(input.PointB),
                Minimum = (float)input.Minimum, Maximum = (float)input.Maximum, Stiffness = float.PositiveInfinity,
            }).Entity;
            world.Add(entity, new AvbdDistanceRangeState { Penalty = 1e6f });
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
            Entity changes = world.CreateEntity(new SceneInputIndex());
            world.AddBuffer<SceneInputChange>(changes);
            world.ExecuteBufferWrite<SceneInputChange>(changes, buffer =>
            {
                foreach (SceneInputChange change in inputChanges) buffer.Add(change);
            });
        }
        foreach (var bodyEvents in data.Events.Where(change => change.Body.HasValue && change.Velocity is not null).GroupBy(change => change.Body!.Value))
        {
            Entity body = entities[bodyEvents.Key];
            float mass = (float)data.Bodies[bodyEvents.Key].Mass;
            world.AddBuffer<BodyImpulse>(body);
            world.ExecuteBufferWrite<BodyImpulse>(body, impulses =>
            {
                foreach (SceneInputEvent change in bodyEvents)
                    impulses.Add(new BodyImpulse { Linear = Vector(change.Velocity!) * mass, Time = change.Frame * data.StepSeconds });
            });
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

internal readonly struct SceneBoxPairs(World world) : ICollisionPairClassifier
{
    public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<BodyMass>();
    public CollisionPairType Classify(scoped QueryRow first, scoped QueryRow second) =>
        first.ReadRef<BodyMass>().Motion == BodyMotion.Static && second.ReadRef<BodyMass>().Motion == BodyMotion.Static
            || world.GetRelationEdgesBetween<SceneCollisionExclusion>(first.Entity, second.Entity).Count > 0
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

internal readonly struct SceneContactComponents : IContactPointComponents
{
    public void AddReads(World world, List<JobResourceAccess> accesses) { }
    public void Add(ref JobCommandWriter commands, Entity point, in ContactPoint geometry,
        ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second, in PhysicsStepContext context)
    {
        commands.Add(point, new ScenePhysicsContact());
        commands.Add(point, new AvbdContactState { NormalPenalty = 1e6f, TangentPenalty = 1e6f, MultiplierNormal = geometry.Normal });
    }
}

internal readonly struct RefitSceneBounds(CollisionMaintenance<CollisionBounds, CollisionBoundsReader, SpatialAll, GroupMatch> maintenance) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => maintenance.Schedule(dependency);
}

internal readonly struct CopySceneBodyTransforms : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        new CopySceneTransformJob().ScheduleParallel(context.World,
            new JobEntityScheduleOptions(rowsPerPacket: 64, filter: context.Bodies), dependency);
}

internal readonly partial struct CopySceneTransformJob : IJobEntity
{
    public void Execute(in BodyPose pose, ref LocalTransform transform)
    {
        transform.Value.Position = pose.Position;
        transform.Value.Rotation = pose.Rotation;
    }
}
