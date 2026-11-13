using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly partial struct AvbdJointMath : IAvbdConstraint<JointConstraint>, IPhysicsConstraintForces<JointConstraint>
{
    private readonly ConstraintColumns<JointConstraint, AvbdJointState> _fields;
    public AvbdJointMath() { _fields = new(); }
    ref readonly JointConstraint IAvbdConstraint<JointConstraint>.ReadConstraint(Entity entity) => ref _fields.ReadConstraint(entity);
    public static void AddAccesses(World world, Query members, List<JobResourceAccess> accesses)
    {
        accesses.AddRange(ConstraintColumns<JointConstraint, AvbdJointState>.Accesses(world, members));
    }
    void IAvbdConstraint<JointConstraint>.Bind(World world, Query members)
    {
        _fields.Bind(world, members);
    }
    public static int GetBodyCount(in JointConstraint constraint, World world, Entity entity, in PhysicsStepContext context) =>
        2;
    public static Entity GetBody(in JointConstraint constraint, World world, Entity entity, int index, in PhysicsStepContext context)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<JointConstraint>>(entity);
        return index == 0 ? endpoints.Source : endpoints.Target;
    }
    public static PhysicsConstraintError GetActivityError(in JointConstraint constraint, World world, Entity entity,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<JointConstraint>>(entity);
        return GetActivityError(in constraint, in endpoints, bodies, in context);
    }
    public static void GetBodyForce(in JointConstraint constraint, World world, Entity entity, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        var state = world.ReadComponentData<AvbdJointState>(entity);
        var endpoints = world.ReadComponentData<RelationEndpoints<JointConstraint>>(entity);
        GetBodyForce(in constraint, in endpoints, ref state, body, bodies, in context, out force, out torque);
    }
    void IAvbdConstraint<JointConstraint>.Initialize(in JointConstraint constraint, World world, Entity entity, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention)
    {
        ref readonly var endpoints = ref _fields.ReadEndpoints(entity);
        ref var state = ref _fields.WriteState(entity);
        Initialize(in constraint, in endpoints, ref state, bodies, in context, retention);
    }
    void IAvbdConstraint<JointConstraint>.AccumulateBodyEquation(in JointConstraint constraint, World world, Entity entity, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        ref readonly var endpoints = ref _fields.ReadEndpoints(entity);
        var state = _fields.ReadState(entity);
        AccumulateBodyEquation(in constraint, in endpoints, ref state, body, bodies, in context, initialErrorFraction, ref equation);
    }
    void IAvbdConstraint<JointConstraint>.UpdateDuals(in JointConstraint constraint, World world, Entity entity, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction)
    {
        ref readonly var endpoints = ref _fields.ReadEndpoints(entity);
        ref var state = ref _fields.WriteState(entity);
        UpdateDuals(in constraint, in endpoints, ref state, bodies, in context, initialErrorFraction);
    }
}

public readonly partial struct AvbdDistanceRangeMath : IAvbdConstraint<DistanceRange>, IPhysicsConstraintForces<DistanceRange>
{
    private readonly ConstraintColumns<DistanceRange, AvbdDistanceRangeState> _fields;
    public AvbdDistanceRangeMath() { _fields = new(); }
    ref readonly DistanceRange IAvbdConstraint<DistanceRange>.ReadConstraint(Entity entity) => ref _fields.ReadConstraint(entity);
    public static void AddAccesses(World world, Query members, List<JobResourceAccess> accesses)
    {
        accesses.AddRange(ConstraintColumns<DistanceRange, AvbdDistanceRangeState>.Accesses(world, members));
    }
    void IAvbdConstraint<DistanceRange>.Bind(World world, Query members)
    {
        _fields.Bind(world, members);
    }
    public static int GetBodyCount(in DistanceRange constraint, World world, Entity entity, in PhysicsStepContext context) =>
        2;
    public static Entity GetBody(in DistanceRange constraint, World world, Entity entity, int index, in PhysicsStepContext context)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<DistanceRange>>(entity);
        return index == 0 ? endpoints.Source : endpoints.Target;
    }
    public static PhysicsConstraintError GetActivityError(in DistanceRange constraint, World world, Entity entity,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<DistanceRange>>(entity);
        return GetActivityError(in constraint, in endpoints, bodies, in context);
    }
    public static void GetBodyForce(in DistanceRange constraint, World world, Entity entity, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        var state = world.ReadComponentData<AvbdDistanceRangeState>(entity);
        var endpoints = world.ReadComponentData<RelationEndpoints<DistanceRange>>(entity);
        GetBodyForce(in constraint, in endpoints, ref state, body, bodies, in context, out force, out torque);
    }
    void IAvbdConstraint<DistanceRange>.Initialize(in DistanceRange constraint, World world, Entity entity, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention)
    {
        ref readonly var endpoints = ref _fields.ReadEndpoints(entity);
        ref var state = ref _fields.WriteState(entity);
        Initialize(in constraint, in endpoints, ref state, bodies, in context, retention);
    }
    void IAvbdConstraint<DistanceRange>.AccumulateBodyEquation(in DistanceRange constraint, World world, Entity entity, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        ref readonly var endpoints = ref _fields.ReadEndpoints(entity);
        var state = _fields.ReadState(entity);
        AccumulateBodyEquation(in constraint, in endpoints, ref state, body, bodies, in context, initialErrorFraction, ref equation);
    }
    void IAvbdConstraint<DistanceRange>.UpdateDuals(in DistanceRange constraint, World world, Entity entity, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction)
    {
        ref readonly var endpoints = ref _fields.ReadEndpoints(entity);
        ref var state = ref _fields.WriteState(entity);
        UpdateDuals(in constraint, in endpoints, ref state, bodies, in context, initialErrorFraction);
    }
}

public readonly partial struct AvbdElasticConnectionMath : IAvbdConstraint<ElasticConnection>, IPhysicsConstraintForces<ElasticConnection>
{
    private readonly ConstraintColumns<ElasticConnection, AvbdElasticState> _fields;
    public AvbdElasticConnectionMath() { _fields = new(); }
    ref readonly ElasticConnection IAvbdConstraint<ElasticConnection>.ReadConstraint(Entity entity) => ref _fields.ReadConstraint(entity);
    public static void AddAccesses(World world, Query members, List<JobResourceAccess> accesses)
    {
        accesses.AddRange(ConstraintColumns<ElasticConnection, AvbdElasticState>.Accesses(world, members));
    }
    void IAvbdConstraint<ElasticConnection>.Bind(World world, Query members)
    {
        _fields.Bind(world, members);
    }
    public static int GetBodyCount(in ElasticConnection constraint, World world, Entity entity, in PhysicsStepContext context) =>
        2;
    public static Entity GetBody(in ElasticConnection constraint, World world, Entity entity, int index, in PhysicsStepContext context)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<ElasticConnection>>(entity);
        return index == 0 ? endpoints.Source : endpoints.Target;
    }
    public static PhysicsConstraintError GetActivityError(in ElasticConnection constraint, World world, Entity entity,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<ElasticConnection>>(entity);
        return GetActivityError(in constraint, in endpoints, bodies, in context);
    }
    public static void GetBodyForce(in ElasticConnection constraint, World world, Entity entity, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        var state = world.ReadComponentData<AvbdElasticState>(entity);
        var endpoints = world.ReadComponentData<RelationEndpoints<ElasticConnection>>(entity);
        GetBodyForce(in constraint, in endpoints, ref state, body, bodies, in context, out force, out torque);
    }
    void IAvbdConstraint<ElasticConnection>.Initialize(in ElasticConnection constraint, World world, Entity entity, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention)
    {
        ref readonly var endpoints = ref _fields.ReadEndpoints(entity);
        ref var state = ref _fields.WriteState(entity);
        Initialize(in constraint, in endpoints, ref state, bodies, in context, retention);
    }
    void IAvbdConstraint<ElasticConnection>.AccumulateBodyEquation(in ElasticConnection constraint, World world, Entity entity, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        ref readonly var endpoints = ref _fields.ReadEndpoints(entity);
        var state = _fields.ReadState(entity);
        AccumulateBodyEquation(in constraint, in endpoints, ref state, body, bodies, in context, initialErrorFraction, ref equation);
    }
    void IAvbdConstraint<ElasticConnection>.UpdateDuals(in ElasticConnection constraint, World world, Entity entity, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction)
    {
        ref readonly var endpoints = ref _fields.ReadEndpoints(entity);
        ref var state = ref _fields.WriteState(entity);
        UpdateDuals(in constraint, in endpoints, ref state, bodies, in context, initialErrorFraction);
    }
}

public readonly partial struct AvbdJointMotorMath : IAvbdConstraint<JointConstraint>, IPhysicsConstraintForces<JointConstraint>
{
    private readonly ConstraintColumns<JointConstraint, AvbdMotorState> _fields;
    private readonly JointMotorColumns _motors;
    public AvbdJointMotorMath() { _fields = new(); _motors = new(); }
    ref readonly JointConstraint IAvbdConstraint<JointConstraint>.ReadConstraint(Entity entity) => ref _fields.ReadConstraint(entity);
    public static void AddAccesses(World world, Query members, List<JobResourceAccess> accesses)
    {
        accesses.AddRange(ConstraintColumns<JointConstraint, AvbdMotorState>.Accesses(world, members));
        accesses.AddRange(JointMotorColumns.Accesses(world, members));
    }
    void IAvbdConstraint<JointConstraint>.Bind(World world, Query members)
    {
        _fields.Bind(world, members); _motors.Bind(world, members);
    }
    public static int GetBodyCount(in JointConstraint constraint, World world, Entity entity, in PhysicsStepContext context) =>
        world.ReadComponentData<JointMotor>(entity).Response == JointMotorResponse.Both ? 2 : 1;
    public static Entity GetBody(in JointConstraint constraint, World world, Entity entity, int index, in PhysicsStepContext context)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<JointConstraint>>(entity);
        return world.ReadComponentData<JointMotor>(entity).Response == JointMotorResponse.Both && index == 0 ? endpoints.Source : endpoints.Target;
    }
    public static int GetReadBodyCount(in JointConstraint constraint, World world, Entity entity, in PhysicsStepContext context) =>
        world.ReadComponentData<JointMotor>(entity).Response == JointMotorResponse.Both ? 0 : 1;
    public static Entity GetReadBody(in JointConstraint constraint, World world, Entity entity, int index, in PhysicsStepContext context) =>
        world.ReadComponentData<RelationEndpoints<JointConstraint>>(entity).Source;
    public static ulong GetInputVersion(in JointConstraint constraint, World world, Entity entity) =>
        Math.Max(world.ComponentDataWriteVersion<JointConstraint>(entity), Math.Max(world.ComponentDataWriteVersion<JointMotor>(entity),
            world.ComponentDataWriteVersion<JointMotorTarget>(entity)));
    public static PhysicsConstraintError GetActivityError(in JointConstraint constraint, World world, Entity entity,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<JointConstraint>>(entity);
        var motor = world.ReadComponentData<JointMotor>(entity);
        var target = world.ReadComponentData<JointMotorTarget>(entity);
        return JointMotorMath.GetActivityError(in constraint, in endpoints, in motor, in target, bodies, in context);
    }
    public static void GetBodyForce(in JointConstraint constraint, World world, Entity entity, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        var state = world.ReadComponentData<AvbdMotorState>(entity);
        var endpoints = world.ReadComponentData<RelationEndpoints<JointConstraint>>(entity);
        var motor = world.ReadComponentData<JointMotor>(entity);
        var target = world.ReadComponentData<JointMotorTarget>(entity);
        GetBodyForce(in constraint, in endpoints, in motor, in target, ref state, body, bodies, in context, out force, out torque);
    }
    void IAvbdConstraint<JointConstraint>.Initialize(in JointConstraint constraint, World world, Entity entity, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention)
    {
        ref readonly var endpoints = ref _fields.ReadEndpoints(entity);
        ref readonly var motor = ref _motors.ReadMotor(entity);
        ref readonly var target = ref _motors.ReadTarget(entity);
        ref var state = ref _fields.WriteState(entity);
        Initialize(in constraint, in endpoints, in motor, in target, ref state, bodies, in context, retention);
    }
    void IAvbdConstraint<JointConstraint>.AccumulateBodyEquation(in JointConstraint constraint, World world, Entity entity, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        ref readonly var endpoints = ref _fields.ReadEndpoints(entity);
        ref readonly var motor = ref _motors.ReadMotor(entity);
        ref readonly var target = ref _motors.ReadTarget(entity);
        var state = _fields.ReadState(entity);
        AccumulateBodyEquation(in constraint, in endpoints, in motor, in target, ref state, body, bodies, in context, initialErrorFraction, ref equation);
    }
    void IAvbdConstraint<JointConstraint>.UpdateDuals(in JointConstraint constraint, World world, Entity entity, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction)
    {
        ref readonly var endpoints = ref _fields.ReadEndpoints(entity);
        ref readonly var motor = ref _motors.ReadMotor(entity);
        ref readonly var target = ref _motors.ReadTarget(entity);
        ref var state = ref _fields.WriteState(entity);
        UpdateDuals(in constraint, in endpoints, in motor, in target, ref state, bodies, in context, initialErrorFraction);
    }
}

public readonly partial struct XpbdJointProjection : IXpbdConstraint<JointConstraint>, IPhysicsConstraintForces<JointConstraint>
{
    private readonly ConstraintColumns<JointConstraint, XpbdJointState> _fields;
    public XpbdJointProjection() { _fields = new(); }
    ref readonly JointConstraint IXpbdConstraint<JointConstraint>.ReadConstraint(Entity entity) => ref _fields.ReadConstraint(entity);
    public static void AddAccesses(World world, Query members, List<JobResourceAccess> accesses)
    {
        accesses.AddRange(ConstraintColumns<JointConstraint, XpbdJointState>.Accesses(world, members));
    }
    void IXpbdConstraint<JointConstraint>.Bind(World world, Query members)
    {
        _fields.Bind(world, members);
    }
    public static int GetBodyCount(in JointConstraint constraint, World world, Entity entity, in PhysicsStepContext context) =>
        2;
    public static Entity GetBody(in JointConstraint constraint, World world, Entity entity, int index, in PhysicsStepContext context)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<JointConstraint>>(entity);
        return index == 0 ? endpoints.Source : endpoints.Target;
    }
    public static PhysicsConstraintError GetActivityError(in JointConstraint constraint, World world, Entity entity,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<JointConstraint>>(entity);
        return GetActivityError(in constraint, in endpoints, bodies, in context);
    }
    public static void GetBodyForce(in JointConstraint constraint, World world, Entity entity, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        var state = world.ReadComponentData<XpbdJointState>(entity);
        var endpoints = world.ReadComponentData<RelationEndpoints<JointConstraint>>(entity);
        GetBodyForce(in constraint, in endpoints, ref state, body, bodies, in context, out force, out torque);
    }
    void IXpbdConstraint<JointConstraint>.Initialize(in JointConstraint constraint, World world, Entity entity, in PhysicsStepContext context)
    {
        ref readonly var endpoints = ref _fields.ReadEndpoints(entity);
        ref var state = ref _fields.WriteState(entity);
        Initialize(in constraint, in endpoints, ref state, in context);
    }
    void IXpbdConstraint<JointConstraint>.Project(in JointConstraint constraint, World world, Entity entity, scoped PhysicsBodies bodies,
        in PhysicsStepContext context)
    {
        ref readonly var endpoints = ref _fields.ReadEndpoints(entity);
        ref var state = ref _fields.WriteState(entity);
        Project(in constraint, in endpoints, ref state, bodies, in context);
    }
}

public readonly partial struct XpbdJointMotorProjection : IXpbdConstraint<JointConstraint>, IPhysicsConstraintForces<JointConstraint>
{
    private readonly ConstraintColumns<JointConstraint, XpbdMotorState> _fields;
    private readonly JointMotorColumns _motors;
    public XpbdJointMotorProjection() { _fields = new(); _motors = new(); }
    ref readonly JointConstraint IXpbdConstraint<JointConstraint>.ReadConstraint(Entity entity) => ref _fields.ReadConstraint(entity);
    public static void AddAccesses(World world, Query members, List<JobResourceAccess> accesses)
    {
        accesses.AddRange(ConstraintColumns<JointConstraint, XpbdMotorState>.Accesses(world, members));
        accesses.AddRange(JointMotorColumns.Accesses(world, members));
    }
    void IXpbdConstraint<JointConstraint>.Bind(World world, Query members)
    {
        _fields.Bind(world, members); _motors.Bind(world, members);
    }
    public static int GetBodyCount(in JointConstraint constraint, World world, Entity entity, in PhysicsStepContext context) =>
        world.ReadComponentData<JointMotor>(entity).Response == JointMotorResponse.Both ? 2 : 1;
    public static Entity GetBody(in JointConstraint constraint, World world, Entity entity, int index, in PhysicsStepContext context)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<JointConstraint>>(entity);
        return world.ReadComponentData<JointMotor>(entity).Response == JointMotorResponse.Both && index == 0 ? endpoints.Source : endpoints.Target;
    }
    public static int GetReadBodyCount(in JointConstraint constraint, World world, Entity entity, in PhysicsStepContext context) =>
        world.ReadComponentData<JointMotor>(entity).Response == JointMotorResponse.Both ? 0 : 1;
    public static Entity GetReadBody(in JointConstraint constraint, World world, Entity entity, int index, in PhysicsStepContext context) =>
        world.ReadComponentData<RelationEndpoints<JointConstraint>>(entity).Source;
    public static ulong GetInputVersion(in JointConstraint constraint, World world, Entity entity) =>
        Math.Max(world.ComponentDataWriteVersion<JointConstraint>(entity), Math.Max(world.ComponentDataWriteVersion<JointMotor>(entity),
            world.ComponentDataWriteVersion<JointMotorTarget>(entity)));
    public static PhysicsConstraintError GetActivityError(in JointConstraint constraint, World world, Entity entity,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<JointConstraint>>(entity);
        var motor = world.ReadComponentData<JointMotor>(entity);
        var target = world.ReadComponentData<JointMotorTarget>(entity);
        return JointMotorMath.GetActivityError(in constraint, in endpoints, in motor, in target, bodies, in context);
    }
    public static void GetBodyForce(in JointConstraint constraint, World world, Entity entity, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        var state = world.ReadComponentData<XpbdMotorState>(entity);
        var endpoints = world.ReadComponentData<RelationEndpoints<JointConstraint>>(entity);
        var motor = world.ReadComponentData<JointMotor>(entity);
        var target = world.ReadComponentData<JointMotorTarget>(entity);
        GetBodyForce(in constraint, in endpoints, in motor, in target, ref state, body, bodies, in context, out force, out torque);
    }
    void IXpbdConstraint<JointConstraint>.Initialize(in JointConstraint constraint, World world, Entity entity, in PhysicsStepContext context)
    {
        ref readonly var endpoints = ref _fields.ReadEndpoints(entity);
        ref readonly var motor = ref _motors.ReadMotor(entity);
        ref readonly var target = ref _motors.ReadTarget(entity);
        ref var state = ref _fields.WriteState(entity);
        Initialize(in constraint, in endpoints, in motor, in target, ref state, in context);
    }
    void IXpbdConstraint<JointConstraint>.Project(in JointConstraint constraint, World world, Entity entity, scoped PhysicsBodies bodies,
        in PhysicsStepContext context)
    {
        ref readonly var endpoints = ref _fields.ReadEndpoints(entity);
        ref readonly var motor = ref _motors.ReadMotor(entity);
        ref readonly var target = ref _motors.ReadTarget(entity);
        ref var state = ref _fields.WriteState(entity);
        Project(in constraint, in endpoints, in motor, in target, ref state, bodies, in context);
    }
}
public readonly partial struct XpbdLengthProjection : IXpbdConstraint<XpbdLength>, IXpbdConstraintForces<XpbdLength>
{
    private readonly ConstraintColumns<XpbdLength, XpbdLength> _fields;
    public XpbdLengthProjection() => _fields = new();
    void IXpbdConstraint<XpbdLength>.Bind(World world, Query members) => _fields.Bind(world, members);
    ref readonly XpbdLength IXpbdConstraint<XpbdLength>.ReadConstraint(Entity entity) => ref _fields.ReadConstraint(entity);
    public static void AddAccesses(World world, Query members, List<JobResourceAccess> accesses)
    {
        accesses.AddRange(ConstraintColumns<XpbdLength, XpbdLength>.Accesses(world, members));
    }
    public static int GetBodyCount(in XpbdLength constraint, World world, Entity entity, in PhysicsStepContext context) => 2;
    public static Entity GetBody(in XpbdLength constraint, World world, Entity entity, int index, in PhysicsStepContext context)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<XpbdLength>>(entity);
        return index == 0 ? endpoints.Source : endpoints.Target;
    }
    public static PhysicsConstraintError GetActivityError(in XpbdLength constraint, World world, Entity entity,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<XpbdLength>>(entity);
        return GetActivityError(in constraint, in endpoints, bodies, in context);
    }
    public static void GetBodyForce(in XpbdLength constraint, World world, Entity entity, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<XpbdLength>>(entity);
        GetBodyForce(in constraint, in endpoints, body, bodies, in context, out force, out torque);
    }
    void IXpbdConstraint<XpbdLength>.Initialize(in XpbdLength constraint, World world, Entity entity, in PhysicsStepContext context)
    {
        _fields.WriteState(entity).Multiplier = 0;
    }
    void IXpbdConstraint<XpbdLength>.Project(in XpbdLength constraint, World world, Entity entity, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<XpbdLength>>(entity);
        Project(ref _fields.WriteState(entity), in endpoints, bodies, in context);
    }
}

public readonly partial struct XpbdBendProjection : IXpbdConstraint<XpbdBend>, IXpbdConstraintForces<XpbdBend>
{
    private readonly ConstraintColumns<XpbdBend, XpbdBend> _fields;
    public XpbdBendProjection() => _fields = new();
    void IXpbdConstraint<XpbdBend>.Bind(World world, Query members) => _fields.Bind(world, members);
    ref readonly XpbdBend IXpbdConstraint<XpbdBend>.ReadConstraint(Entity entity) => ref _fields.ReadConstraint(entity);
    public static void AddAccesses(World world, Query members, List<JobResourceAccess> accesses)
    {
        accesses.AddRange(ConstraintColumns<XpbdBend, XpbdBend>.Accesses(world, members));
    }
    public static int GetBodyCount(in XpbdBend constraint, World world, Entity entity, in PhysicsStepContext context) => 3;
    public static Entity GetBody(in XpbdBend constraint, World world, Entity entity, int index, in PhysicsStepContext context)
    {
        return ConstraintBodies.GetBody(world, entity, index);
    }
    public static PhysicsConstraintError GetActivityError(in XpbdBend constraint, World world, Entity entity,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        using var links = ConstraintBodies.GetBodies(world, entity);
        Span<Entity> bodiesOfConstraint = stackalloc Entity[3];
        for (int index = 0; index < 3; index++) bodiesOfConstraint[index] = links.Entries[index].Other;
        return GetActivityError(in constraint, bodiesOfConstraint, bodies, in context);
    }
    public static void GetBodyForce(in XpbdBend constraint, World world, Entity entity, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        using var links = ConstraintBodies.GetBodies(world, entity);
        Span<Entity> bodiesOfConstraint = stackalloc Entity[3];
        for (int index = 0; index < 3; index++) bodiesOfConstraint[index] = links.Entries[index].Other;
        GetBodyForce(in constraint, bodiesOfConstraint, body, bodies, in context, out force, out torque);
    }
    void IXpbdConstraint<XpbdBend>.Initialize(in XpbdBend constraint, World world, Entity entity, in PhysicsStepContext context)
    {
        _fields.WriteState(entity).Multiplier = 0;
    }
    void IXpbdConstraint<XpbdBend>.Project(in XpbdBend constraint, World world, Entity entity, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        using var links = ConstraintBodies.GetBodies(world, entity);
        Span<Entity> bodiesOfConstraint = stackalloc Entity[3];
        for (int index = 0; index < 3; index++) bodiesOfConstraint[index] = links.Entries[index].Other;
        Project(ref _fields.WriteState(entity), bodiesOfConstraint, bodies, in context);
    }
}
