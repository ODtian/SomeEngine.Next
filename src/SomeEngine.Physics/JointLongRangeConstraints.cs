using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Explicit hard distance bounds inferred from the selected original joint topology.</summary>
public readonly struct JointLongRangeConstraints(QueryHandle joints, JointLongRangeSettings settings) : IAvbdConstraints
{
    private readonly JointLongRangeData _data = new(settings);

    public void AddAccesses(World world, List<JobResourceAccess> accesses)
    {
        PhysicsQueryAccess.AddAccesses(world, joints, accesses);
        accesses.Add(JobResourceAccess.Exclusive(SomeEngine.ECS.Systems.WorldJobs.GetScheduler(world).GetResourceToken(_data)));
    }

    public void Initialize(in PhysicsStepContext context, scoped PhysicsBodies bodies, float retention)
    {
        var work = new InitializeBounds(_data, bodies, context);
        context.World.ExecuteQuery(joints, ref work);
    }

    public void AccumulateBodyEquation(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        var work = new AccumulateBounds(_data, body, bodies, ref equation);
        context.World.ExecuteQuery(joints, ref work);
    }

    public void AdjustBodyChange(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation) { }

    public void UpdateDuals(in PhysicsStepContext context, scoped PhysicsBodies bodies, float initialErrorFraction)
    {
        var work = new UpdateBounds(_data, bodies);
        context.World.ExecuteQuery(joints, ref work);
    }

    private readonly ref struct InitializeBounds(JointLongRangeData data, PhysicsBodies bodies, PhysicsStepContext context) : IQueryExecution
    {
        private readonly PhysicsBodies _bodies = bodies;
        private readonly PhysicsStepContext _context = context;
        public void Execute(scoped QueryCursor query) => data.Initialize(new PhysicsQueryRows(query), _bodies, in _context);
    }

    private ref struct AccumulateBounds(JointLongRangeData data, Entity body, PhysicsBodies bodies, ref AvbdBodyEquation equation) : IQueryExecution
    {
        private readonly PhysicsBodies _bodies = bodies;
        private ref AvbdBodyEquation _equation = ref equation;
        public void Execute(scoped QueryCursor query) => data.Accumulate(new PhysicsQueryRows(query), body, _bodies, ref _equation);
    }

    private readonly ref struct UpdateBounds(JointLongRangeData data, PhysicsBodies bodies) : IQueryExecution
    {
        private readonly PhysicsBodies _bodies = bodies;
        public void Execute(scoped QueryCursor query) => data.Update(new PhysicsQueryRows(query), _bodies);
    }
}

internal sealed class JointLongRangeData(JointLongRangeSettings settings)
{
    private readonly JointLongRangeGraph _graph = new();
    private readonly Dictionary<Entity, List<JointBoundReference>> _byBody = [];
    private readonly Stack<List<JointBoundReference>> _unused = [];

    internal void Initialize(scoped PhysicsQueryRows joints, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        _graph.Refresh(joints, bodies, in context, settings.Levels);
        foreach (List<JointBoundReference> references in _byBody.Values) { references.Clear(); _unused.Push(references); }
        _byBody.Clear();
        float squaredDuration = (float)(context.Duration * context.Duration);
        foreach (QueryRow row in joints.Rows)
        {
            DynamicBuffer<LongRangeJointBound> bounds = row.Buffer<LongRangeJointBound>();
            for (int index = 0; index < bounds.Count; index++)
            {
                ref LongRangeJointBound bound = ref bounds[index];
                JointBoundGeometry geometry = GetGeometry(joints, row.Entity, in bound, bodies, false);
                JointBoundGeometry start = GetGeometry(joints, row.Entity, in bound, bodies, true);
                Vector3 direction = geometry.Distance > 0 ? geometry.Delta / geometry.Distance : Vector3.Zero;
                float mobility = BodyConstraintMath.GetWeight(in bodies.ReadMass(geometry.FirstBody), in bodies.ReadPose(geometry.FirstBody),
                    direction, Vector3.Cross(geometry.FirstOffset, direction))
                    + BodyConstraintMath.GetWeight(in bodies.ReadMass(geometry.LastBody), in bodies.ReadPose(geometry.LastBody),
                        direction, Vector3.Cross(geometry.LastOffset, direction));
                bound.Penalty = settings.PenaltyScale / (squaredDuration * mobility);
                bound.InitialError = new(MathF.Max(0, start.Distance - bound.Maximum), MathF.Max(0, bound.Minimum - start.Distance));
                bound.Multiplier *= settings.MultiplierRetention;
                var reference = new JointBoundReference(row.Entity, index);
                Add(geometry.FirstBody, reference);
                Add(geometry.LastBody, reference);
            }
        }
    }

    private void Add(Entity body, JointBoundReference reference)
    {
        if (!_byBody.TryGetValue(body, out List<JointBoundReference>? references))
        {
            references = _unused.TryPop(out List<JointBoundReference>? reused) ? reused : [];
            _byBody.Add(body, references);
        }
        references.Add(reference);
    }

    internal void Accumulate(scoped PhysicsQueryRows joints, Entity body, scoped PhysicsBodies bodies, ref AvbdBodyEquation equation)
    {
        if (!_byBody.TryGetValue(body, out List<JointBoundReference>? references)) return;
        foreach (JointBoundReference reference in CollectionsMarshal.AsSpan(references))
        {
            joints.TryGetRow(reference.Joint, out QueryRow row);
            ref readonly LongRangeJointBound bound = ref row.ReadBuffer<LongRangeJointBound>().AsSpan()[reference.Index];
            JointBoundGeometry geometry = GetGeometry(joints, row.Entity, in bound, bodies, false);
            Evaluate(in bound, in geometry, out float force, out float penalty, out _);
            if (penalty == 0) continue;
            Vector3 direction = geometry.Distance > 0 ? geometry.Delta / geometry.Distance : Vector3.Zero;
            float side = body == geometry.LastBody ? 1 : -1;
            Vector3 offset = body == geometry.LastBody ? geometry.LastOffset : geometry.FirstOffset;
            Vector3 linear = direction * side, angular = Vector3.Cross(offset, linear);
            equation.AddRow(linear, angular, force, penalty);
            if (force == 0 || geometry.Distance == 0) continue;
            float magnitude = MathF.Abs(force), radiusSquared = offset.LengthSquared(), radial = Vector3.Dot(offset, direction);
            for (int axis = 0; axis < 3; axis++)
            {
                Vector3 unit = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
                float linearCurvature = magnitude / geometry.Distance * MathF.Max(0, 1 - direction[axis] * direction[axis]);
                float angularCurvature = MathF.Max(0,
                    (radiusSquared - offset[axis] * offset[axis] - angular[axis] * angular[axis]) / geometry.Distance);
                Vector3 column = offset * direction[axis] - unit * radial;
                equation.AddRow(unit, Vector3.Zero, 0, linearCurvature);
                equation.AddRow(Vector3.Zero, unit, 0, magnitude * (angularCurvature + column.Length()));
            }
        }
    }

    internal void Update(scoped PhysicsQueryRows joints, scoped PhysicsBodies bodies)
    {
        foreach (QueryRow row in joints.Rows)
        {
            DynamicBuffer<LongRangeJointBound> bounds = row.Buffer<LongRangeJointBound>();
            for (int index = 0; index < bounds.Count; index++)
            {
                ref LongRangeJointBound bound = ref bounds[index];
                JointBoundGeometry geometry = GetGeometry(joints, row.Entity, in bound, bodies, false);
                Evaluate(in bound, in geometry, out _, out _, out Vector2 multiplier);
                bound.Multiplier = multiplier;
            }
        }
    }

    private void Evaluate(in LongRangeJointBound bound, in JointBoundGeometry geometry,
        out float force, out float penalty, out Vector2 multiplier)
    {
        if (bound.Minimum == bound.Maximum)
        {
            force = bound.Multiplier.X + bound.Penalty * (geometry.Distance - bound.Maximum
                - settings.InitialErrorFraction * (bound.InitialError.X - bound.InitialError.Y));
            penalty = bound.Penalty;
            multiplier = new(force, 0);
            return;
        }
        float maximum = bound.Maximum + settings.InitialErrorFraction * bound.InitialError.X;
        float minimum = bound.Minimum - settings.InitialErrorFraction * bound.InitialError.Y;
        float upper = MathF.Max(0, bound.Multiplier.X + bound.Penalty * (geometry.Distance - maximum));
        float lower = MathF.Max(0, bound.Multiplier.Y + bound.Penalty * (minimum - geometry.Distance));
        force = upper - lower;
        penalty = bound.Penalty * ((upper > 0 ? 1 : 0) + (lower > 0 ? 1 : 0));
        multiplier = new(upper, lower);
    }

    private static JointBoundGeometry GetGeometry(scoped PhysicsQueryRows joints, Entity firstJoint,
        in LongRangeJointBound bound, scoped PhysicsBodies bodies, bool start)
    {
        var first = new JointGraphAnchor(firstJoint, bound.FirstEnd);
        var last = new JointGraphAnchor(bound.OtherJoint, bound.OtherEnd);
        Entity firstBody = JointLongRangeGraph.ReadBody(joints, first), lastBody = JointLongRangeGraph.ReadBody(joints, last);
        ReadAnchor(firstBody, JointLongRangeGraph.ReadLocal(joints, first), bodies, start, out Vector3 firstPoint, out Vector3 firstOffset);
        ReadAnchor(lastBody, JointLongRangeGraph.ReadLocal(joints, last), bodies, start, out Vector3 lastPoint, out Vector3 lastOffset);
        Vector3 delta = lastPoint - firstPoint;
        return new(firstBody, lastBody, firstOffset, lastOffset, delta, delta.Length());
    }

    private static void ReadAnchor(Entity body, Vector3 local, scoped PhysicsBodies bodies, bool start, out Vector3 point, out Vector3 offset)
    {
        ref readonly BodyMass mass = ref bodies.ReadMass(body);
        if (start)
        {
            ref readonly BodyStepState step = ref bodies.ReadStepState(body);
            offset = Vector3.Transform(local - mass.CenterOfMass, step.StartRotation);
            point = step.StartCenterOfMass + offset;
        }
        else
        {
            ref readonly BodyPose pose = ref bodies.ReadPose(body);
            offset = Vector3.Transform(local - mass.CenterOfMass, pose.Rotation);
            point = pose.GetCenterOfMass(in mass) + offset;
        }
    }

    private readonly record struct JointBoundReference(Entity Joint, int Index);
    private readonly record struct JointBoundGeometry(Entity FirstBody, Entity LastBody,
        Vector3 FirstOffset, Vector3 LastOffset, Vector3 Delta, float Distance);
}
