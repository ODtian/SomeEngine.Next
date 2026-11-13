using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Hard distance bounds inferred from the selected original joint topology.</summary>
public readonly partial struct JointLongRangeConstraints(Query joints, JointLongRangeSettings settings) : IAvbdConstraints
{
    private readonly JointLongRangeData _data = new(joints, settings);
    public void AddAccesses(World world, List<JobResourceAccess> accesses)
    {
        PhysicsQueryAccess.AddAccesses(world, joints, accesses);
        accesses.AddRange(((SomeEngine.ECS.Systems.WorldJobs)world.Execution!).CollectAccess(new SomeEngine.ECS.Systems.SystemAccesses()
            .ReadRandom<JointConstraint>().ReadRandom<RelationEndpoints<JointConstraint>>()
            .WriteRandom<SomeEngine.ECS.Buffer<LongRangeJointBound>>()));
        accesses.Add(JobResourceAccess.Write((world.Execution!.Scheduler!).GetResourceToken(_data)));
    }
    public void Initialize(in PhysicsStepContext context, scoped PhysicsBodies bodies, float retention) =>
        _data.Initialize(context.World, bodies, in context);
    public void AccumulateBodyEquation(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        float initialErrorFraction, ref AvbdBodyEquation equation) => _data.Accumulate(context.World, body, bodies, ref equation);
    public void AdjustBodyChange(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation)
    { }
    public void UpdateDuals(in PhysicsStepContext context, scoped PhysicsBodies bodies, float initialErrorFraction) =>
        _data.Update(context.World, bodies);
}

internal sealed class JointLongRangeData(Query joints, JointLongRangeSettings settings)
{
    private readonly JointLongRangeGraph _graph = new();
    private readonly Dictionary<Entity, List<JointBoundReference>> _byBody = [];
    private readonly Stack<List<JointBoundReference>> _unused = [];

    internal void RefreshGraph(World world, scoped PhysicsBodies bodies, in PhysicsStepContext context) =>
        _graph.Refresh(world, joints, bodies, in context, settings.Levels);

    internal void Initialize(World world, scoped PhysicsBodies bodies, in PhysicsStepContext context, bool refreshGraph = true)
    {
        if (refreshGraph) RefreshGraph(world, bodies, in context);
        foreach (List<JointBoundReference> references in _byBody.Values) { references.Clear(); _unused.Push(references); }
        _byBody.Clear();
        float squaredDuration = (float)(context.Duration * context.Duration);
        using var scope = joints.OpenRead();
        foreach (QueryChunk chunk in scope)
            foreach (Entity entity in chunk.Entities)
            {
                using DynamicBuffer<LongRangeJointBound> bounds = world.GetBuffer<LongRangeJointBound>(entity);
                for (int index = 0; index < bounds.Count; index++)
                {
                    ref LongRangeJointBound bound = ref bounds[index];
                    JointBoundGeometry geometry = GetGeometry(world, entity, in bound, bodies, false);
                    JointBoundGeometry start = GetGeometry(world, entity, in bound, bodies, true);
                    Vector3 direction = geometry.Distance > 0 ? geometry.Delta / geometry.Distance : Vector3.Zero;
                    float mobility = BodyConstraintMath.GetWeight(in bodies.ReadMass(geometry.FirstBody), in bodies.ReadPose(geometry.FirstBody),
                        direction, Vector3.Cross(geometry.FirstOffset, direction))
                        + BodyConstraintMath.GetWeight(in bodies.ReadMass(geometry.LastBody), in bodies.ReadPose(geometry.LastBody),
                            direction, Vector3.Cross(geometry.LastOffset, direction));
                    bound.Penalty = settings.PenaltyScale / (squaredDuration * mobility);
                    bound.InitialError = new(MathF.Max(0, start.Distance - bound.Maximum), MathF.Max(0, bound.Minimum - start.Distance));
                    bound.Multiplier *= context.FirstIteration == 0 ? settings.MultiplierRetention : 1;
                    var reference = new JointBoundReference(entity, index);
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

    internal void Accumulate(World world, Entity body, scoped PhysicsBodies bodies, ref AvbdBodyEquation equation)
    {
        if (!_byBody.TryGetValue(body, out List<JointBoundReference>? references)) return;
        foreach (JointBoundReference reference in CollectionsMarshal.AsSpan(references))
        {
            using var bounds = world.ReadBuffer<LongRangeJointBound>(reference.Joint);
            ref readonly LongRangeJointBound bound = ref bounds.AsSpan()[reference.Index];
            JointBoundGeometry geometry = GetGeometry(world, reference.Joint, in bound, bodies, false);
            Evaluate(in bound, in geometry, out float force, out float penalty, out _);
            float side = body == geometry.LastBody ? 1 : -1;
            Vector3 offset = body == geometry.LastBody ? geometry.LastOffset : geometry.FirstOffset;
            DistanceRangeMath.Accumulate(geometry.Delta, geometry.Distance, offset, side, force, penalty, ref equation);
        }
    }

    internal void Update(World world, scoped PhysicsBodies bodies)
    {
        using var scope = joints.OpenRead();
        foreach (QueryChunk chunk in scope)
            foreach (Entity entity in chunk.Entities)
            {
                using DynamicBuffer<LongRangeJointBound> bounds = world.GetBuffer<LongRangeJointBound>(entity);
                for (int index = 0; index < bounds.Count; index++)
                {
                    ref LongRangeJointBound bound = ref bounds[index];
                    JointBoundGeometry geometry = GetGeometry(world, entity, in bound, bodies, false);
                    Evaluate(in bound, in geometry, out _, out _, out Vector2 multiplier);
                    bound.Multiplier = multiplier;
                }
            }
    }

    internal void ReadActivityErrors(World world, scoped PhysicsBodies bodies, ref PhysicsActivityErrors errors)
    {
        using var scope = joints.OpenRead();
        foreach (QueryChunk chunk in scope)
            foreach (Entity entity in chunk.Entities)
            {
                using var bounds = world.ReadBuffer<LongRangeJointBound>(entity);
                foreach (ref readonly LongRangeJointBound bound in bounds.AsSpan())
                {
                    JointBoundGeometry geometry = GetGeometry(world, entity, in bound, bodies, false);
                    var error = new PhysicsConstraintError(MathF.Max(0, MathF.Max(bound.Minimum - geometry.Distance, geometry.Distance - bound.Maximum)), 0);
                    errors.Add(geometry.FirstBody, in error); errors.Add(geometry.LastBody, in error);
                }
            }
    }

    private void Evaluate(in LongRangeJointBound bound, in JointBoundGeometry geometry,
        out float force, out float penalty, out Vector2 multiplier) =>
        DistanceRangeMath.Evaluate(bound.Minimum, bound.Maximum, float.PositiveInfinity, bound.Penalty,
            bound.Multiplier, bound.InitialError, settings.InitialErrorFraction, geometry.Distance, out force, out penalty, out multiplier);

    private static JointBoundGeometry GetGeometry(World world, Entity first,
        in LongRangeJointBound bound, scoped PhysicsBodies bodies, bool start)
    {
        ReadAnchor(world, first, bound.FirstEnd, bodies, start, out Entity firstBody, out Vector3 firstPoint, out Vector3 firstOffset);
        ReadAnchor(world, bound.OtherJoint, bound.OtherEnd, bodies, start, out Entity lastBody, out Vector3 lastPoint, out Vector3 lastOffset);
        Vector3 delta = lastPoint - firstPoint;
        return new(firstBody, lastBody, firstOffset, lastOffset, delta, delta.Length());
    }

    private static void ReadAnchor(World world, Entity entity, JointEnd end, scoped PhysicsBodies bodies, bool start,
        out Entity body, out Vector3 point, out Vector3 offset)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<JointConstraint>>(entity);
        var joint = world.ReadComponentData<JointConstraint>(entity);
        body = end == JointEnd.Source ? endpoints.Source : endpoints.Target;
        Vector3 local = end == JointEnd.Source ? joint.SourceFrame.Position : joint.TargetFrame.Position;
        DistanceRangeMath.ReadAnchor(body, local, bodies, start, out point, out offset);
    }

    private readonly record struct JointBoundReference(Entity Joint, int Index);
    private readonly record struct JointBoundGeometry(Entity FirstBody, Entity LastBody,
        Vector3 FirstOffset, Vector3 LastOffset, Vector3 Delta, float Distance);
}
