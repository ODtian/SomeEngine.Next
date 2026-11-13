using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

// ContactSupports.cpp: stationary friction-cone supports, convex footprints and load paths.
internal sealed partial class ContactSupportData
{
    private readonly ContactSupportSettings _settings;
    private readonly Vector3 _horizontalX, _horizontalY;
    private readonly Dictionary<Entity, ContactSupportPolygon> _polygons = [];
    private readonly Stack<ContactSupportPolygon> _unusedPolygons = [];
    private readonly Dictionary<Entity, List<int>> _byBody = [];
    private readonly Stack<List<int>> _unusedLists = [];
    private readonly List<SupportCandidate> _candidates = [];
    private readonly List<int> _order = [];
    internal bool HasGraphChanges;
    private readonly ContactManifolds<AvbdSupportedContactState> _contacts;
    private readonly Dictionary<Entity, SupportBody> _bodyStates = [];
    private List<LongRangeSupportLoad>[] _contactLoads = [];
    private Entity[] _workBodies = [];
    private int _bodyCount;
    private long _bodyRead;
    private readonly List<Entity> _retiredBodies = [];
    private sealed class SupportBody
    {
        internal LongRangeBodySupport Value;
        internal long Read;
        internal readonly List<LongRangeSupportLoad> Loads = [];
        internal readonly List<LongRangeSupportEquation> Equations = [];
    }
    internal LongRangeBodySupport ReadBodySupport(Entity body) => _bodyStates[body].Value;
    internal ReadOnlySpan<LongRangeSupportLoad> ReadBodyLoads(Entity body) => CollectionsMarshal.AsSpan(_bodyStates[body].Loads);

    internal ContactSupportData(ContactSupportSettings settings, ContactManifolds<AvbdSupportedContactState> contacts)
    {
        _settings = settings; _contacts = contacts;
        _horizontalX = Vector3.Normalize(Vector3.Cross(settings.UpDirection,
            MathF.Abs(settings.UpDirection.X) < .707f ? Vector3.UnitX : Vector3.UnitY));
        _horizontalY = Vector3.Cross(settings.UpDirection, _horizontalX);
    }

    internal void Initialize(ReadOnlySpan<Entity> workBodies, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        ContactColumns.Grow(ref _workBodies, workBodies.Length); workBodies.CopyTo(_workBodies); _bodyCount = workBodies.Length;
        _bodyRead++;
        foreach (Entity entity in workBodies)
        {
            if (!_bodyStates.TryGetValue(entity, out SupportBody? value)) _bodyStates.Add(entity, value = new());
            value.Read = _bodyRead; value.Value.Moving = false;
        }
        _retiredBodies.Clear();
        foreach (var body in _bodyStates) if (body.Value.Read != _bodyRead) _retiredBodies.Add(body.Key);
        foreach (Entity entity in _retiredBodies) _bodyStates.Remove(entity);
        int previous = _contactLoads.Length;
        if (previous < _contacts.Columns.PointCount)
        {
            Array.Resize(ref _contactLoads, _contacts.Columns.PointCount);
            for (int index = previous; index < _contactLoads.Length; index++) _contactLoads[index] = [];
        }
        for (int point = 0; point < _contacts.Columns.PointCount; point++)
            _contacts.States[point].Support.ExtraForceOnSecond *= _contacts.Columns.GetPoint(point).WarmStart
                ? context.FirstIteration == 0 ? _settings.MultiplierRetention : 1 : 0;
        UpdateMotion(bodies); Build(bodies, in context);
    }

    private bool UpdateMotion(scoped PhysicsBodies bodies)
    {
        bool changed = false;
        foreach (Entity entity in _workBodies.AsSpan(0, _bodyCount))
        {
            ref LongRangeBodySupport state = ref _bodyStates[entity].Value;
            if (state.Moving || !HasMotion(entity, bodies)) continue;
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

    private void Build(scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        HasGraphChanges = true;
        foreach (ContactSupportPolygon polygon in _polygons.Values) { polygon.Clear(); _unusedPolygons.Push(polygon); }
        _polygons.Clear();
        foreach (List<int> points in _byBody.Values) { points.Clear(); _unusedLists.Push(points); }
        _byBody.Clear();
        _candidates.Clear(); _order.Clear();
        foreach (Entity entity in _workBodies.AsSpan(0, _bodyCount))
        {
            ref LongRangeBodySupport state = ref _bodyStates[entity].Value;
            state.Enabled = false; state.Reaction = 0;
            _bodyStates[entity].Loads.Clear();
        }
        for (int pointIndex = 0; pointIndex < _contacts.Columns.PointCount; pointIndex++)
        {
            ref LongRangeContactSupport state = ref _contacts.States[pointIndex].Support;
            state.Enabled = false; state.Reaction = state.Load = 0;
            _contactLoads[pointIndex].Clear();
            if ((_contacts.Columns.Flags[pointIndex] & ContactPointFlags.Trigger) != 0) continue;
            ContactPoint point = _contacts.Columns.GetPoint(pointIndex);
            ref readonly ContactMaterial material = ref _contacts.Materials[pointIndex];
            ReadOnlySpan<ContactBodyLink> links = _contacts.Columns.BodyLinksOf(pointIndex);
            if (!float.IsPositiveInfinity(material.NormalStiffness) || !TryRigidBodies(in point, links, bodies, in context, out Entity first, out Entity second)) continue;
            float vertical = Vector3.Dot(point.Normal, _settings.UpDirection);
            float squared = vertical * vertical;
            if (vertical == 0 || 1 - squared > material.StaticFriction * material.StaticFriction * squared) continue;
            bool upperIsSecond = vertical > 0;
            Entity lower = upperIsSecond ? first : second, upper = upperIsSecond ? second : first;
            if (bodies.ReadMass(upper).Motion != BodyMotion.Dynamic || !_bodyStates.TryGetValue(upper, out SupportBody? upperWork)
                || upperWork.Value.Moving) continue;
            if (bodies.ReadMass(lower).Motion == BodyMotion.Dynamic)
            {
                if (!_bodyStates.TryGetValue(lower, out SupportBody? lowerWork) || lowerWork.Value.Moving) continue;
            }
            else if (HasMotion(lower, bodies)) continue;
            Vector3 delta = ContactBodyMath.GetDelta(in point, links, bodies, in context, false, normal: true);
            Vector3 gapVector = point.Normal * point.Separation + delta - point.BodyAnchorDelta;
            float pressure = Vector3.Dot(_contacts.Responses[pointIndex].ForceOnSecond, point.Normal);
            if (Vector3.Dot(point.Normal, gapVector) > _settings.DistanceTolerance && pressure <= 0) continue;
            Vector3 slip = ContactBodyMath.GetDelta(in point, links, bodies, in context, false)
                - ContactBodyMath.GetDelta(in point, links, bodies, in context, true);
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
            _candidates.Add(new(pointIndex, lower, upper, upperIsSecond,
                Project(upperIsSecond ? firstWitness : secondWitness), Project(upperIsSecond ? secondWitness : firstWitness),
                Vector3.Dot(point.WorldPoint, _settings.UpDirection), Vector3.Dot(gapVector, _settings.UpDirection) * (upperIsSecond ? 1 : -1)));
        }
        _candidates.Sort(static (first, second) =>
        {
            int height = first.Height.CompareTo(second.Height);
            return height != 0 ? height : first.Contact.CompareTo(second.Contact);
        });
        Span<LongRangeSupportLoad> weights = stackalloc LongRangeSupportLoad[3];
        foreach (SupportCandidate candidate in _candidates)
        {
            int count = 0;
            if (bodies.ReadMass(candidate.Lower).Motion == BodyMotion.Dynamic
                && (!_polygons.TryGetValue(candidate.Lower, out ContactSupportPolygon? lowerPolygon)
                    || !lowerPolygon.TryWeights(candidate.LowerPoint, _settings.DistanceTolerance, weights, out count))) continue;
            int pointIndex = candidate.Contact;
            float shift = -candidate.GapHeight;
            for (int index = 0; index < count; index++)
            {
                shift += weights[index].Weight * _contacts.States[weights[index].Contact].Support.HeightShift;
                _contactLoads[pointIndex].Add(weights[index]);
            }
            ContactPoint point = _contacts.Columns.GetPoint(pointIndex);
            ReadOnlySpan<ContactBodyLink> links = _contacts.Columns.BodyLinksOf(pointIndex);
            ref LongRangeContactSupport state = ref _contacts.States[pointIndex].Support;
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
            if (!_byBody.TryGetValue(candidate.Upper, out List<int>? points))
            {
                points = _unusedLists.TryPop(out List<int>? reused) ? reused : [];
                _byBody.Add(candidate.Upper, points);
            }
            points.Add(candidate.Contact);
            _order.Add(candidate.Contact);
        }
        foreach (Entity entity in _workBodies.AsSpan(0, _bodyCount))
        {
            ref LongRangeBodySupport state = ref _bodyStates[entity].Value;
            if (state.Moving || bodies.ReadMass(entity).Motion != BodyMotion.Dynamic
                || !_polygons.TryGetValue(entity, out ContactSupportPolygon? polygon)) continue;
            Vector3 center = bodies.ReadPose(entity).GetCenterOfMass(in bodies.ReadMass(entity));
            if (!polygon.TryWeights(Project(center), _settings.DistanceTolerance, weights, out int count)) continue;
            float height = Vector3.Dot(center, _settings.UpDirection);
            for (int index = 0; index < count; index++)
            {
                height += weights[index].Weight * _contacts.States[weights[index].Contact].Support.HeightShift;
                _bodyStates[entity].Loads.Add(weights[index]);
            }
            state.MinimumHeight = height;
            state.InitialError = MathF.Max(0, height - Vector3.Dot(bodies.ReadStepState(entity).StartCenterOfMass, _settings.UpDirection));
            state.Enabled = true;
        }
        for (int pointIndex = 0; pointIndex < _contacts.Columns.PointCount; pointIndex++)
            if (!_contacts.States[pointIndex].Support.Enabled) Release(pointIndex);
    }

    private Vector2 Project(Vector3 point) => new(Vector3.Dot(point, _horizontalX), Vector3.Dot(point, _horizontalY));

    private static bool TryRigidBodies(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Entity first, out Entity second)
    {
        first = ContactBodyMath.GetBody(in links[0], bodies, in context);
        second = ContactBodyMath.GetBody(in links[point.FirstBodyCount], bodies, in context);
        for (int index = 1; index < point.FirstBodyCount; index++)
            if (ContactBodyMath.GetBody(in links[index], bodies, in context) != first) return false;
        for (int index = point.FirstBodyCount + 1; index < links.Length; index++)
            if (ContactBodyMath.GetBody(in links[index], bodies, in context) != second) return false;
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
            Entity body = ContactBodyMath.GetBody(in link, bodies, in context);
            ref readonly BodyMass mass = ref bodies.ReadMass(body);
            Vector3 position;
            if (start)
            {
                ref readonly BodyStepState step = ref bodies.ReadStepState(body);
                position = step.StartCenterOfMass + Vector3.Transform(link.NormalLocalPoint - mass.CenterOfMass, step.StartRotation);
            }
            else
            {
                ref readonly BodyPose pose = ref bodies.ReadPose(body);
                position = pose.Position + Vector3.Transform(link.NormalLocalPoint, pose.Rotation);
            }
            result += position * link.Weight;
        }
        return result;
    }

    private void Release(int pointIndex)
    {
        ref LongRangeContactSupport support = ref _contacts.States[pointIndex].Support;
        if (support.ExtraForceOnSecond == Vector3.Zero) return;
        ContactPoint point = _contacts.Columns.GetPoint(pointIndex);
        ref readonly ContactMaterial material = ref _contacts.Materials[pointIndex];
        ref AvbdContactState state = ref _contacts.States[pointIndex].Contact;
        float normal = Vector3.Dot(support.ExtraForceOnSecond, point.Normal);
        state.NormalMultiplier = MathF.Min(0, state.NormalMultiplier - normal);
        state.TangentMultiplier -= support.ExtraForceOnSecond - point.Normal * normal;
        float magnitude = state.TangentMultiplier.Length(), bound = -state.NormalMultiplier * material.StaticFriction;
        if (magnitude > bound) state.TangentMultiplier *= bound / magnitude;
        support.ExtraForceOnSecond = Vector3.Zero;
    }

    private readonly record struct SupportCandidate(int Contact, Entity Lower, Entity Upper, bool UpperIsSecond,
        Vector2 LowerPoint, Vector2 UpperPoint, float Height, float GapHeight);
}
