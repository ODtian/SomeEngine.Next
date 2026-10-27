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

    internal void RefreshGraph(scoped PhysicsQueryRows joints, scoped PhysicsBodies bodies, in PhysicsStepContext context) =>
        _graph.Refresh(joints, bodies, in context, settings.Levels);

    internal void Initialize(scoped PhysicsQueryRows joints, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        RefreshGraph(joints, bodies, in context);
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
            float side = body == geometry.LastBody ? 1 : -1;
            Vector3 offset = body == geometry.LastBody ? geometry.LastOffset : geometry.FirstOffset;
            DistanceRangeMath.Accumulate(geometry.Delta, geometry.Distance, offset, side, force, penalty, ref equation);
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

    internal void ReadActivityErrors(scoped PhysicsQueryRows joints, scoped PhysicsBodies bodies, ref PhysicsActivityErrors errors)
    {
        foreach (QueryRow row in joints.Rows)
            foreach (ref readonly LongRangeJointBound bound in row.ReadBuffer<LongRangeJointBound>().AsSpan())
            {
                JointBoundGeometry geometry = GetGeometry(joints, row.Entity, in bound, bodies, false);
                var error = new PhysicsConstraintError(MathF.Max(0, MathF.Max(bound.Minimum - geometry.Distance, geometry.Distance - bound.Maximum)), 0);
                errors.Add(geometry.FirstBody, in error); errors.Add(geometry.LastBody, in error);
            }
    }

    private void Evaluate(in LongRangeJointBound bound, in JointBoundGeometry geometry,
        out float force, out float penalty, out Vector2 multiplier) =>
        DistanceRangeMath.Evaluate(bound.Minimum, bound.Maximum, float.PositiveInfinity, bound.Penalty,
            bound.Multiplier, bound.InitialError, settings.InitialErrorFraction, geometry.Distance, out force, out penalty, out multiplier);

    private static JointBoundGeometry GetGeometry(scoped PhysicsQueryRows joints, Entity firstJoint,
        in LongRangeJointBound bound, scoped PhysicsBodies bodies, bool start)
    {
        var first = new JointGraphAnchor(firstJoint, bound.FirstEnd);
        var last = new JointGraphAnchor(bound.OtherJoint, bound.OtherEnd);
        Entity firstBody = JointLongRangeGraph.ReadBody(joints, first), lastBody = JointLongRangeGraph.ReadBody(joints, last);
        DistanceRangeMath.ReadAnchor(firstBody, JointLongRangeGraph.ReadLocal(joints, first), bodies, start, out Vector3 firstPoint, out Vector3 firstOffset);
        DistanceRangeMath.ReadAnchor(lastBody, JointLongRangeGraph.ReadLocal(joints, last), bodies, start, out Vector3 lastPoint, out Vector3 lastOffset);
        Vector3 delta = lastPoint - firstPoint;
        return new(firstBody, lastBody, firstOffset, lastOffset, delta, delta.Length());
    }

    private readonly record struct JointBoundReference(Entity Joint, int Index);
    private readonly record struct JointBoundGeometry(Entity FirstBody, Entity LastBody,
        Vector3 FirstOffset, Vector3 LastOffset, Vector3 Delta, float Distance);
}
