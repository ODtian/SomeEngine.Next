using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

// ContactSupports.cpp: stationary friction-cone supports, convex footprints and load paths.
internal sealed partial class ContactSupportData
{
    private readonly ContactSupportSettings _settings;
    private readonly Vector3 _horizontalX, _horizontalY;
    private readonly Dictionary<Entity, ContactSupportPolygon> _polygons = [];
    private readonly Stack<ContactSupportPolygon> _unusedPolygons = [];
    private readonly Dictionary<Entity, List<Entity>> _byBody = [];
    private readonly Stack<List<Entity>> _unusedLists = [];
    private readonly List<SupportCandidate> _candidates = [];
    private readonly List<Entity> _order = [];

    internal ContactSupportData(ContactSupportSettings settings)
    {
        _settings = settings;
        _horizontalX = Vector3.Normalize(Vector3.Cross(settings.UpDirection,
            MathF.Abs(settings.UpDirection.X) < .707f ? Vector3.UnitX : Vector3.UnitY));
        _horizontalY = Vector3.Cross(settings.UpDirection, _horizontalX);
    }

    internal void Initialize(scoped QueryCursor workBodies, scoped QueryCursor contacts, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        foreach (QueryRow row in workBodies.Rows) row.ReadWrite<LongRangeBodySupport>().Moving = false;
        foreach (QueryRow row in contacts.Rows)
        {
            ref LongRangeContactSupport state = ref row.ReadWrite<LongRangeContactSupport>();
            state.ExtraForceOnSecond *= row.ReadRef<ContactPoint>().WarmStart ? _settings.MultiplierRetention : 0;
        }
        UpdateMotion(workBodies, bodies);
        Build(workBodies, contacts, bodies, in context);
    }

    private bool UpdateMotion(scoped QueryCursor workBodies, scoped PhysicsBodies bodies)
    {
        bool changed = false;
        foreach (QueryRow row in workBodies.Rows)
        {
            ref LongRangeBodySupport state = ref row.ReadWrite<LongRangeBodySupport>();
            if (state.Moving || !HasMotion(row.Entity, bodies)) continue;
            state.Moving = true;
            changed = true;
        }
        return changed;
    }

    private bool HasMotion(Entity body, scoped PhysicsBodies bodies)
    {
        ref readonly BodyMass mass = ref bodies.ReadMass(body);
        if (mass.Motion == BodyMotion.Static) return false;
        ref readonly BodyPose pose = ref bodies.ReadPose(body);
        ref readonly BodyStepState start = ref bodies.ReadStepState(body);
        Vector3 travel = pose.GetCenterOfMass(in mass) - start.StartCenterOfMass;
        travel -= _settings.UpDirection * Vector3.Dot(travel, _settings.UpDirection);
        float rotation = BodyRotation.GetRotationVector(Quaternion.Conjugate(start.StartRotation) * pose.Rotation).Length();
        return travel.LengthSquared() > _settings.MotionTolerance * _settings.MotionTolerance || rotation > _settings.AngularMotionTolerance;
    }

    private void Build(scoped QueryCursor workBodies, scoped QueryCursor contacts, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        foreach (ContactSupportPolygon polygon in _polygons.Values) { polygon.Clear(); _unusedPolygons.Push(polygon); }
        _polygons.Clear();
        foreach (List<Entity> points in _byBody.Values) { points.Clear(); _unusedLists.Push(points); }
        _byBody.Clear();
        _candidates.Clear(); _order.Clear();
        foreach (QueryRow row in workBodies.Rows)
        {
            ref LongRangeBodySupport state = ref row.ReadWrite<LongRangeBodySupport>();
            state.Enabled = false; state.Reaction = 0;
            row.Buffer<LongRangeSupportLoad>().Clear();
        }
        foreach (QueryRow row in contacts.Rows)
        {
            ref LongRangeContactSupport state = ref row.ReadWrite<LongRangeContactSupport>();
            state.Enabled = false; state.Reaction = state.Load = 0;
            row.Buffer<LongRangeSupportLoad>().Clear();
            ref readonly ContactPoint point = ref row.ReadRef<ContactPoint>();
            ref readonly ContactMaterial material = ref row.ReadRef<ContactMaterial>();
            ReadOnlySpan<ContactBodyLink> links = row.ReadBuffer<ContactBodyLink>().AsSpan();
            if (!float.IsPositiveInfinity(material.NormalStiffness) || !TryRigidBodies(in point, links, in context, out Entity first, out Entity second)) continue;
            float vertical = Vector3.Dot(point.Normal, _settings.UpDirection);
            float squared = vertical * vertical;
            if (vertical == 0 || 1 - squared > material.StaticFriction * material.StaticFriction * squared) continue;
            bool upperIsSecond = vertical > 0;
            Entity lower = upperIsSecond ? first : second, upper = upperIsSecond ? second : first;
            if (bodies.ReadMass(upper).Motion != BodyMotion.Dynamic || !workBodies.TryGetRow(upper, out QueryRow upperWork)
                || upperWork.ReadRef<LongRangeBodySupport>().Moving) continue;
            if (bodies.ReadMass(lower).Motion == BodyMotion.Dynamic)
            {
                if (!workBodies.TryGetRow(lower, out QueryRow lowerWork) || lowerWork.ReadRef<LongRangeBodySupport>().Moving) continue;
            }
            else if (HasMotion(lower, bodies)) continue;
            Vector3 delta = ContactBodyMath.GetDelta(in point, links, bodies, in context, false);
            Vector3 gapVector = point.Normal * point.Separation + delta - point.BodyAnchorDelta;
            float pressure = Vector3.Dot(row.ReadRef<ContactResponse>().ForceOnSecond, point.Normal);
            if (Vector3.Dot(point.Normal, gapVector) > _settings.DistanceTolerance && pressure <= 0) continue;
            Vector3 slip = delta - ContactBodyMath.GetDelta(in point, links, bodies, in context, true);
            slip -= point.Normal * Vector3.Dot(point.Normal, slip);
            float travel = slip.Length(), mobility = 0;
            if (travel > 0)
                for (int bodyIndex = 0; bodyIndex < 2; bodyIndex++)
                {
                    Entity body = bodyIndex == 0 ? first : second;
                    ContactBodyMath.GetJacobian(in point, links, body, bodies, in context, slip / travel, out Vector3 linear, out Vector3 angular);
                    mobility += BodyConstraintMath.GetWeight(in bodies.ReadMass(body), in bodies.ReadPose(body), linear, angular);
                }
            if (travel > _settings.StickDistance + material.StaticFriction * MathF.Abs(pressure) * mobility * (float)(context.Duration * context.Duration)) continue;
            Vector3 firstWitness = point.WorldPoint - point.Normal * (point.Separation * .5f);
            Vector3 secondWitness = point.WorldPoint + point.Normal * (point.Separation * .5f);
            _candidates.Add(new(row.Entity, lower, upper, upperIsSecond,
                Project(upperIsSecond ? firstWitness : secondWitness), Project(upperIsSecond ? secondWitness : firstWitness),
                Vector3.Dot(point.WorldPoint, _settings.UpDirection), Vector3.Dot(gapVector, _settings.UpDirection) * (upperIsSecond ? 1 : -1)));
        }
        _candidates.Sort(static (first, second) =>
        {
            int height = first.Height.CompareTo(second.Height);
            return height != 0 ? height : EntityOrder.Compare(first.Contact, second.Contact);
        });
        Span<LongRangeSupportLoad> weights = stackalloc LongRangeSupportLoad[3];
        foreach (SupportCandidate candidate in _candidates)
        {
            int count = 0;
            if (bodies.ReadMass(candidate.Lower).Motion == BodyMotion.Dynamic
                && (!_polygons.TryGetValue(candidate.Lower, out ContactSupportPolygon? lowerPolygon)
                    || !lowerPolygon.TryWeights(candidate.LowerPoint, _settings.DistanceTolerance, weights, out count))) continue;
            contacts.TryGetRow(candidate.Contact, out QueryRow row);
            float shift = -candidate.GapHeight;
            for (int index = 0; index < count; index++)
            {
                contacts.TryGetRow(weights[index].Contact, out QueryRow below);
                shift += weights[index].Weight * below.ReadRef<LongRangeContactSupport>().HeightShift;
                row.Buffer<LongRangeSupportLoad>().Add(weights[index]);
            }
            ref readonly ContactPoint point = ref row.ReadRef<ContactPoint>();
            ReadOnlySpan<ContactBodyLink> links = row.ReadBuffer<ContactBodyLink>().AsSpan();
            ref LongRangeContactSupport state = ref row.ReadWrite<LongRangeContactSupport>();
            state.Enabled = true; state.UpperIsSecond = candidate.UpperIsSecond; state.HeightShift = shift;
            state.MinimumHeight = Vector3.Dot(SidePoint(in point, links, candidate.UpperIsSecond, bodies, in context, false), _settings.UpDirection) + shift;
            state.InitialError = MathF.Max(0, state.MinimumHeight
                - Vector3.Dot(SidePoint(in point, links, candidate.UpperIsSecond, bodies, in context, true), _settings.UpDirection));
            if (!_polygons.TryGetValue(candidate.Upper, out ContactSupportPolygon? polygon))
            {
                polygon = _unusedPolygons.TryPop(out ContactSupportPolygon? reused) ? reused : new();
                _polygons.Add(candidate.Upper, polygon);
            }
            polygon.Points.Add(new(candidate.UpperPoint, candidate.Contact));
            if (!_byBody.TryGetValue(candidate.Upper, out List<Entity>? points))
            {
                points = _unusedLists.TryPop(out List<Entity>? reused) ? reused : [];
                _byBody.Add(candidate.Upper, points);
            }
            points.Add(candidate.Contact);
            _order.Add(candidate.Contact);
        }
        foreach (QueryRow row in workBodies.Rows)
        {
            ref LongRangeBodySupport state = ref row.ReadWrite<LongRangeBodySupport>();
            if (state.Moving || bodies.ReadMass(row.Entity).Motion != BodyMotion.Dynamic
                || !_polygons.TryGetValue(row.Entity, out ContactSupportPolygon? polygon)) continue;
            Vector3 center = bodies.ReadPose(row.Entity).GetCenterOfMass(in bodies.ReadMass(row.Entity));
            if (!polygon.TryWeights(Project(center), _settings.DistanceTolerance, weights, out int count)) continue;
            float height = Vector3.Dot(center, _settings.UpDirection);
            for (int index = 0; index < count; index++)
            {
                contacts.TryGetRow(weights[index].Contact, out QueryRow contact);
                height += weights[index].Weight * contact.ReadRef<LongRangeContactSupport>().HeightShift;
                row.Buffer<LongRangeSupportLoad>().Add(weights[index]);
            }
            state.MinimumHeight = height;
            state.InitialError = MathF.Max(0, height - Vector3.Dot(bodies.ReadStepState(row.Entity).StartCenterOfMass, _settings.UpDirection));
            state.Enabled = true;
        }
        foreach (QueryRow row in contacts.Rows)
            if (!row.ReadRef<LongRangeContactSupport>().Enabled) Release(row);
    }

    private Vector2 Project(Vector3 point) => new(Vector3.Dot(point, _horizontalX), Vector3.Dot(point, _horizontalY));

    private static bool TryRigidBodies(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        in PhysicsStepContext context, out Entity first, out Entity second)
    {
        first = ContactBodyMath.GetBody(in links[0], in context);
        second = ContactBodyMath.GetBody(in links[point.FirstBodyCount], in context);
        for (int index = 1; index < point.FirstBodyCount; index++)
            if (ContactBodyMath.GetBody(in links[index], in context) != first) return false;
        for (int index = point.FirstBodyCount + 1; index < links.Length; index++)
            if (ContactBodyMath.GetBody(in links[index], in context) != second) return false;
        return first != second;
    }

    private static Vector3 SidePoint(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links, bool second,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, bool start)
    {
        Vector3 result = default;
        int first = second ? point.FirstBodyCount : 0, last = second ? links.Length : point.FirstBodyCount;
        for (int index = first; index < last; index++)
        {
            ref readonly ContactBodyLink link = ref links[index];
            Entity body = ContactBodyMath.GetBody(in link, in context);
            ref readonly BodyMass mass = ref bodies.ReadMass(body);
            Vector3 position;
            if (start)
            {
                ref readonly BodyStepState step = ref bodies.ReadStepState(body);
                position = step.StartCenterOfMass + Vector3.Transform(link.LocalPoint - mass.CenterOfMass, step.StartRotation);
            }
            else
            {
                ref readonly BodyPose pose = ref bodies.ReadPose(body);
                position = pose.Position + Vector3.Transform(link.LocalPoint, pose.Rotation);
            }
            result += position * link.Weight;
        }
        return result;
    }

    private static void Release(scoped QueryRow row)
    {
        ref LongRangeContactSupport support = ref row.ReadWrite<LongRangeContactSupport>();
        if (support.ExtraForceOnSecond == Vector3.Zero) return;
        ref readonly ContactPoint point = ref row.ReadRef<ContactPoint>();
        ref readonly ContactMaterial material = ref row.ReadRef<ContactMaterial>();
        ref AvbdContactState state = ref row.ReadWrite<AvbdContactState>();
        float normal = Vector3.Dot(support.ExtraForceOnSecond, point.Normal);
        state.NormalMultiplier = MathF.Min(0, state.NormalMultiplier - normal);
        state.TangentMultiplier -= support.ExtraForceOnSecond - point.Normal * normal;
        float magnitude = state.TangentMultiplier.Length(), bound = -state.NormalMultiplier * material.StaticFriction;
        if (magnitude > bound) state.TangentMultiplier *= bound / magnitude;
        support.ExtraForceOnSecond = Vector3.Zero;
    }

    private readonly record struct SupportCandidate(Entity Contact, Entity Lower, Entity Upper, bool UpperIsSecond,
        Vector2 LowerPoint, Vector2 UpperPoint, float Height, float GapHeight);
}
