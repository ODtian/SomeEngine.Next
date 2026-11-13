using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly record struct ContactRefreshSettings(float MatchDistance, float MinimumNormalDot, bool WarmStart);

public sealed partial class ContactManifolds<TState> where TState : unmanaged
{
    private readonly Dictionary<ShapePair, int> _previousPairs = [];
    private readonly Dictionary<ShapePair, int> _newPairs = [];
    private ContactPair[] _previousPairValues = [];
    private ContactPoint[] _previousPoints = [];
    private TState[] _previousStates = [];
    private ContactResponse[] _previousResponses = [];
    private ContactBodyLink[] _previousLinks = [];
    private int[] _previousStarts = [], _previousCounts = [], _pairCounts = [], _pairStarts = [], _inputs = [];
    private int[] _claims = [], _matches = [];
    private float[] _matchDistances = [];
    private int _previousPointCount;

    /// <summary>Rebuilds shape pairs and points, retaining matched numerical state in body-local coordinates.</summary>
    public void Refresh<TCombine>(ContactPointBuffer detected, scoped ReadLookup<ContactMaterial> materials,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, TCombine combine, ContactRefreshSettings settings)
        where TCombine : struct, IContactMaterialCombine
    {
        KeepPrevious();
        _newPairs.Clear();
        int pairCount = 0, bodyCount = 0;
        ContactColumns.Grow(ref _pairCounts, detected.Count);
        ContactColumns.Grow(ref _pairStarts, detected.Count + 1);
        ContactColumns.Grow(ref _inputs, detected.Count);
        for (int input = 0; input < detected.Count; input++)
        {
            ref readonly CollisionContact contact = ref detected.Read(input).Contact;
            ShapePair key = new(contact.First, contact.Second, contact.FirstShape, contact.SecondShape);
            if (!_newPairs.TryGetValue(key, out int pair))
            {
                pair = pairCount++; _newPairs.Add(key, pair); _pairCounts[pair] = 0;
            }
            _pairCounts[pair]++; bodyCount += detected.Bodies(input).Length;
        }
        _pairStarts[0] = 0;
        for (int pair = 0; pair < pairCount; pair++)
        {
            _pairStarts[pair + 1] = _pairStarts[pair] + _pairCounts[pair];
            _pairCounts[pair] = 0;
        }
        for (int input = 0; input < detected.Count; input++)
        {
            ref readonly CollisionContact contact = ref detected.Read(input).Contact;
            int pair = _newPairs[new(contact.First, contact.Second, contact.FirstShape, contact.SecondShape)];
            _inputs[_pairStarts[pair] + _pairCounts[pair]++] = input;
        }
        ContactColumns.Grow(ref _matches, detected.Count); ContactColumns.Grow(ref _matchDistances, detected.Count);
        for (int point = 0; point < detected.Count; point++)
        {
            int input = _inputs[point];
            ref readonly CollisionContact contact = ref detected.Read(input).Contact;
            int previous = FindPrevious(new(contact.First, contact.Second, contact.FirstShape, contact.SecondShape),
                in contact, bodies, settings, out float distance);
            _matches[point] = previous; _matchDistances[point] = distance;
            if (previous < 0) continue;
            int competing = _claims[previous];
            if (competing < 0) _claims[previous] = point;
            else if (distance < _matchDistances[competing]) { _matches[competing] = -1; _claims[previous] = point; }
            else _matches[point] = -1;
        }
        Resize(pairCount, detected.Count, bodyCount);
        foreach ((ShapePair key, int pair) in _newPairs)
            Columns.Pairs[pair] = new(key.First, key.Second, key.FirstShape, key.SecondShape, _pairStarts[pair], _pairCounts[pair]);
        int bodyStart = 0;
        for (int point = 0; point < detected.Count; point++)
        {
            int input = _inputs[point];
            ref readonly CollisionContact contact = ref detected.Read(input).Contact;
            ReadOnlySpan<ContactBody> links = detected.Bodies(input);
            ShapePair key = new(contact.First, contact.Second, contact.FirstShape, contact.SecondShape);
            int pair = _newPairs[key];
            int previous = _matches[point];
            _states[point] = previous >= 0 ? _previousStates[previous] : _newPointState;
            Columns.Responses[point] = previous >= 0 ? _previousResponses[previous] : default;
            ContactMaterial first = materials.Has(contact.First) ? materials.Read(contact.First) : default;
            ContactMaterial second = materials.Has(contact.Second) ? materials.Read(contact.Second) : default;
            Columns.Materials[point] = combine.Combine(in first, in second);
            Columns.BodyStarts[point] = bodyStart; Columns.BodyCounts[point] = links.Length;
            for (int index = 0; index < links.Length; index++)
            {
                ref readonly ContactBody link = ref links[index];
                Columns.BodyLinks[bodyStart + index] = new(link.Body, link.LocalPoint, link.Weight, link.NormalReference);
            }
            ContactPoint value = new()
            {
                PairIndex = pair,
                FirstFeature = contact.FirstFeature,
                SecondFeature = contact.SecondFeature,
                PointIndex = contact.PointIndex,
                FirstLocalPoint = contact.FirstLocalPoint,
                SecondLocalPoint = contact.SecondLocalPoint,
                WorldPoint = contact.WorldPoint,
                Normal = contact.Normal,
                Separation = contact.Separation,
                FirstBodyCount = contact.FirstBodyCount,
                Prediction = contact.Prediction,
                WarmStart = settings.WarmStart && previous >= 0,
            };
            value.BodyAnchorDelta = ContactBodyMath.GetDelta(in value,
                Columns.BodyLinks.Slice(bodyStart, links.Length), bodies, in context, contact.Prediction.Enabled, normal: true);
            Columns.Flags[point] = ContactPointFlags.None;
            Columns.SetPoint(point, in value);
            bodyStart += links.Length;
        }
        Columns.RebuildBodyPairs();
    }

    private int FindPrevious(ShapePair key, in CollisionContact input, scoped PhysicsBodies bodies, ContactRefreshSettings settings, out float distanceSquared)
    {
        distanceSquared = settings.MatchDistance * settings.MatchDistance * 2;
        if (!_previousPairs.TryGetValue(key, out int pair)) return -1;
        ContactPair range = _previousPairValues[pair];
        int match = -1;
        float nearest = distanceSquared;
        for (int point = range.FirstPoint; point < range.FirstPoint + range.PointCount; point++)
        {
            ref readonly ContactPoint previous = ref _previousPoints[point];
            if (previous.FirstFeature != input.FirstFeature || previous.SecondFeature != input.SecondFeature
                || Vector3.Dot(previous.Normal, input.Normal) < settings.MinimumNormalDot) continue;
            bool liveBodies = true;
            foreach (ContactBodyLink link in _previousLinks.AsSpan(_previousStarts[point], _previousCounts[point]))
                liveBodies &= bodies.Contains(link.Body);
            if (!liveBodies) continue;
            float distance = Vector3.DistanceSquared(previous.FirstLocalPoint, input.FirstLocalPoint)
                + Vector3.DistanceSquared(previous.SecondLocalPoint, input.SecondLocalPoint);
            if (distance <= nearest) { nearest = distance; match = point; }
        }
        distanceSquared = nearest; return match;
    }

    private void KeepPrevious()
    {
        _previousPointCount = Columns.PointCount;
        ContactColumns.Grow(ref _previousPairValues, Columns.PairCount);
        ContactColumns.Grow(ref _previousPoints, _previousPointCount);
        ContactColumns.Grow(ref _previousStates, _previousPointCount);
        ContactColumns.Grow(ref _previousResponses, _previousPointCount);
        ContactColumns.Grow(ref _previousStarts, _previousPointCount);
        ContactColumns.Grow(ref _previousCounts, _previousPointCount);
        ContactColumns.Grow(ref _previousLinks, Columns.BodyLinkCount);
        ContactColumns.Grow(ref _claims, _previousPointCount);
        _previousPairs.Clear();
        for (int pair = 0; pair < Columns.PairCount; pair++)
        {
            ContactPair value = Columns.Pairs[pair];
            _previousPairValues[pair] = value;
            _previousPairs.Add(new(value.FirstGeometry, value.SecondGeometry, value.FirstShape, value.SecondShape), pair);
        }
        for (int point = 0; point < _previousPointCount; point++) _previousPoints[point] = Columns.GetPoint(point);
        States.CopyTo(_previousStates); Columns.Responses.CopyTo(_previousResponses);
        Columns.BodyStarts.CopyTo(_previousStarts); Columns.BodyCounts.CopyTo(_previousCounts);
        Columns.BodyLinks.CopyTo(_previousLinks); Array.Fill(_claims, -1, 0, _previousPointCount);
    }

    private void ClearHistory()
    {
        _previousPairs.Clear(); _newPairs.Clear(); _previousPairValues = []; _previousPoints = [];
        _previousStates = []; _previousResponses = []; _previousLinks = [];
        _previousStarts = []; _previousCounts = []; _pairCounts = []; _pairStarts = []; _inputs = []; _claims = []; _matches = []; _matchDistances = [];
    }

    private readonly record struct ShapePair(Entity First, Entity Second, uint FirstShape, uint SecondShape);
}

/// <summary>Reads detection and original body fields after its dependency, then fills the CPU contact columns.</summary>
public readonly struct RefreshContactManifolds<TState, TCombine> : IPhysicsStage
    where TState : unmanaged where TCombine : struct, IContactMaterialCombine
{
    private readonly ContactManifolds<TState> _contacts;
    private readonly ContactPointBuffer _detected;
    private readonly Query _bodies;
    private readonly BodyColumns _columns = new();
    private readonly TCombine _combine;
    private readonly ContactRefreshSettings _settings;
    public RefreshContactManifolds(World world, Query bodies, ContactManifolds<TState> contacts,
        ContactPointBuffer detected, TCombine combine, ContactRefreshSettings settings)
    {
        _contacts = contacts; _detected = detected; _bodies = bodies; _combine = combine; _settings = settings;

    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        JobHandle completed = PhysicsBodyJobs.Schedule<ReadBodyFields, Refresh>(_bodies, _columns, in context, new Refresh(this), dependency);
        var step = new PhysicsStepResult(context.Number, context.StartTime - context.Substep * context.Duration,
            context.EndTime, completed);
        _contacts.Columns.SetStep(in step);
        return completed;
    }
    private readonly struct Refresh(RefreshContactManifolds<TState, TCombine> stage) : IPhysicsBodyWork
    {
        public void AddAccesses(World world, List<JobResourceAccess> accesses)
        {
            accesses.Add(stage._contacts.WriteAccess); accesses.Add(stage._detected.ReadAccess);
            accesses.AddRange(((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().ReadRandom<ContactMaterial>()));
        }
        public void Execute(in PhysicsStepContext context, scoped PhysicsBodies bodies)
        {
            using var materials = context.World.OpenRead<ContactMaterial>();
            stage._contacts.Refresh(stage._detected, materials, bodies, in context, stage._combine, stage._settings);
        }
    }
}
