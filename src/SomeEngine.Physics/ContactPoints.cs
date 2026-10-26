using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly record struct ContactRefreshSettings(float MatchDistance, float MinimumNormalDot, bool WarmStart);

/// <summary>A concrete matching result for the current detection output; false selects the normal point search.</summary>
public interface IContactPointMatches
{
    void AddAccesses(List<JobResourceAccess> accesses);
    bool TryGetMatch(int input, out Entity point, out float distanceSquared);
    void RecordPoint(int input, Entity point) { }
}

public readonly struct SearchContactPoints : IContactPointMatches
{
    public void AddAccesses(List<JobResourceAccess> accesses) { }
    public bool TryGetMatch(int input, out Entity point, out float distanceSquared)
    { point = default; distanceSquared = 0; return false; }
}

public static class ContactPoints
{
    public static ContactPoints<TCombine, TComponents> Create<TCombine, TComponents>(World world, QueryHandle poses, QueryHandle points,
        ContactPointBuffer detected, TCombine combine, TComponents components, ContactRefreshSettings settings)
        where TCombine : struct, IContactMaterialCombine where TComponents : struct, IContactPointComponents =>
        new(world, poses, points, detected, combine, components, settings);
}

/// <summary>Matches derived geometry output to ECS points and maintains their protected participant relationships.</summary>
public sealed class ContactPoints<TCombine, TComponents> : IDisposable
    where TCombine : struct, IContactMaterialCombine where TComponents : struct, IContactPointComponents
{
    private readonly World _world;
    private readonly QueryHandle _poses, _points;
    private readonly ContactPointBuffer _detected;
    private readonly TCombine _combine;
    private readonly TComponents _components;
    private readonly ContactRefreshSettings _settings;
    private readonly Scheduler _scheduler;
    private readonly JobResourceToken _resource;
    private readonly JobResourceAccess[] _matchAccesses, _componentAccesses, _valueAccesses;
    private readonly List<Entity> _oldPoints = [];
    private readonly Dictionary<(Entity, Entity), List<Entity>> _pairs = [];
    private readonly Stack<List<Entity>> _unusedPairs = [];
    private readonly List<PointMatch> _matches = [];
    private readonly Dictionary<Entity, int> _claims = [];
    private readonly List<ParticipantReference> _references = [];
    private readonly List<RelationEdge<ContactParticipant>> _oldParticipants = [];

    public ContactPoints(World world, QueryHandle poses, QueryHandle points, ContactPointBuffer detected, TCombine combine,
        TComponents components, ContactRefreshSettings settings)
    {
        _world = world;
        _poses = poses;
        _detected = detected;
        _combine = combine;
        _components = components;
        _settings = settings;
        _scheduler = WorldJobs.GetScheduler(world);
        _resource = _scheduler.CreateResourceToken("Contact point matching");
        QueryDefinition reads = new QueryDefinitionBuilder().Read<ContactPoint>().Read<UndirectedRelationEndpoints<ContactPoint>>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read).Build();
        _points = world.Query(world.GetQueryDefinition(points).WithAccess(reads));
        var accesses = new List<JobResourceAccess> { detected.ReadAccess, JobResourceAccess.Exclusive(_resource) };
        PhysicsQueryAccess.AddAccesses(world, _points, accesses);
        _matchAccesses = accesses.ToArray();
        PhysicsQueryAccess.AddAccesses(world, poses, accesses);
        accesses.Add(ComponentJobAccess<ContactMaterial>.Read(world));
        components.AddReads(world, accesses);
        _componentAccesses = accesses.ToArray();
        _valueAccesses = [RelationshipJobAccess.TopologyRead(world), ComponentJobAccess<ContactPoint>.Write(world),
            ComponentJobAccess<ContactMaterial>.Write(world), BufferJobAccess<ContactBodyLink>.Write(world),
            detected.ReadAccess, JobResourceAccess.Exclusive(_resource)];
    }

    public RefreshContactPoints<TCombine, TComponents> Refresh() => new(this);
    public RefreshMatchedContactPoints<TCombine, TComponents, TMatches> Refresh<TMatches>(TMatches matches)
        where TMatches : struct, IContactPointMatches => new(this, matches);

    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => Schedule(in context, default(SearchContactPoints), dependency);

    internal JobHandle Schedule<TMatches>(in PhysicsStepContext context, TMatches matches, JobHandle dependency)
        where TMatches : struct, IContactPointMatches
    {
        var points = new JobCommandBuffer(_world, 1);
        var components = new JobCommandBuffer(_world, 1);
        var accesses = new List<JobResourceAccess>(_matchAccesses);
        matches.AddAccesses(accesses);
        points.ScheduleParallel(new MatchPoints<TMatches>(this, matches), 1, CollectionsMarshal.AsSpan(accesses), dependency);
        JobHandle pointChanges = points.SchedulePlayback();
        var componentAccesses = new List<JobResourceAccess>(_componentAccesses);
        matches.AddAccesses(componentAccesses);
        components.ScheduleParallel(new AddComponents<TMatches>(this, matches, context), 1, CollectionsMarshal.AsSpan(componentAccesses), pointChanges);
        JobHandle componentChanges = components.SchedulePlayback();
        JobHandle values = _scheduler.Schedule(new WriteValues(this), _valueAccesses, componentChanges);
        return _scheduler.ScheduleFinally(new ReleaseCommands(points, components), JobScheduleOptions.Default, values);
    }

    private void Match<TMatches>(TMatches matches, ref JobCommandWriter commands) where TMatches : struct, IContactPointMatches
    {
        foreach (List<Entity> pair in _pairs.Values) { pair.Clear(); _unusedPairs.Push(pair); }
        _pairs.Clear();
        _oldPoints.Clear();
        _matches.Clear();
        _claims.Clear();
        var collect = new ReadPoints(this);
        _world.ExecuteQuery(_points, ref collect);
        float limit = _settings.MatchDistance * _settings.MatchDistance;
        for (int index = 0; index < _detected.Count; index++)
        {
            ref readonly CollisionContact input = ref _detected.Read(index).Contact;
            Entity match = Entity.Null;
            float distance = limit;
            bool supplied = matches.TryGetMatch(index, out Entity suppliedPoint, out float suppliedDistance);
            if (supplied)
            {
                if (suppliedPoint != Entity.Null && _pairs.TryGetValue((input.First, input.Second), out List<Entity>? current)
                    && current.Contains(suppliedPoint))
                { match = suppliedPoint; distance = suppliedDistance; }
            }
            else if (_pairs.TryGetValue((input.First, input.Second), out List<Entity>? candidates))
                foreach (Entity candidate in candidates)
                {
                    ContactPoint old = _world.Read<ContactPoint>(candidate);
                    float current = Vector3.DistanceSquared(input.WorldPoint, old.WorldPoint);
                    if (current <= distance && Vector3.Dot(input.Normal, old.Normal) >= _settings.MinimumNormalDot)
                    {
                        match = candidate;
                        distance = current;
                    }
                }
            _matches.Add(new PointMatch { Point = match, Input = index, DistanceSquared = distance });
            if (match == Entity.Null) continue;
            if (_claims.TryGetValue(match, out int previous))
            {
                if (distance < _matches[previous].DistanceSquared)
                {
                    CollectionsMarshal.AsSpan(_matches)[previous].Point = Entity.Null;
                    _claims[match] = index;
                }
                else CollectionsMarshal.AsSpan(_matches)[index].Point = Entity.Null;
            }
            else _claims.Add(match, index);
        }
        foreach (ref PointMatch match in CollectionsMarshal.AsSpan(_matches))
            if (match.Point == Entity.Null)
            {
                ref readonly CollisionContact input = ref _detected.Read(match.Input).Contact;
                match.Created = commands.Relations<ContactPoint>().Create(input.First, input.Second, ToPoint(in input));
            }
        foreach (Entity old in _oldPoints)
            if (!_claims.ContainsKey(old)) commands.Relations<ContactPoint>().Destroy(new RelationEdge<ContactPoint>(old));
    }

    private readonly struct ReadPoints(ContactPoints<TCombine, TComponents> points) : IQueryExecution
    {
        public void Execute(scoped QueryCursor query)
        {
            foreach (QueryRow row in query.Rows)
            {
                points._oldPoints.Add(row.Entity);
                bool live = true;
                foreach (ref readonly ContactBodyLink link in row.ReadBuffer<ContactBodyLink>().AsSpan())
                    if (!points._world.IsAlive(link.Participant.Entity)) { live = false; break; }
                if (!live) continue;
                ref readonly var endpoints = ref row.ReadRef<UndirectedRelationEndpoints<ContactPoint>>();
                var pair = (endpoints.EndpointA, endpoints.EndpointB);
                if (!points._pairs.TryGetValue(pair, out List<Entity>? bucket))
                {
                    bucket = points._unusedPairs.TryPop(out List<Entity>? reused) ? reused : [];
                    points._pairs.Add(pair, bucket);
                }
                bucket.Add(row.Entity);
            }
        }
    }

    private void Add<TMatches>(TMatches matches, ref JobCommandWriter commands, in PhysicsStepContext context)
        where TMatches : struct, IContactPointMatches
    {
        _references.Clear();
        var anchors = new ReadAnchors(this);
        _world.ExecuteQuery(_poses, ref anchors);
        foreach (ref PointMatch match in CollectionsMarshal.AsSpan(_matches))
        {
            bool created = match.Point == Entity.Null;
            if (created) match.Point = match.Created.Resolve().Entity;
            matches.RecordPoint(match.Input, match.Point);
            ref readonly CollisionContact input = ref _detected.Read(match.Input).Contact;
            ReadOnlySpan<ContactBody> bodies = _detected.Bodies(match.Input);
            ContactMaterial first = _world.Read<ContactMaterial>(input.First);
            ContactMaterial second = _world.Read<ContactMaterial>(input.Second);
            match.Material = _combine.Combine(in first, in second);
            if (created)
            {
                commands.Add(match.Point, match.Material);
                commands.Add(match.Point, new ContactResponse());
                commands.AddBuffer<ContactBodyLink>(match.Point);
                ContactPoint geometry = ToPoint(in input);
                geometry.BodyAnchorDelta = match.BodyAnchorDelta;
                _components.Add(ref commands, match.Point, in geometry, bodies[..input.FirstBodyCount], bodies[input.FirstBodyCount..], in context);
            }
            _oldParticipants.Clear();
            if (!created)
            {
                var old = _oldParticipants;
                _world.ExecuteBufferRead<ContactBodyLink, List<RelationEdge<ContactParticipant>>>(match.Point, ref old,
                    static (links, ref old) =>
                    {
                        foreach (ref readonly ContactBodyLink link in links.AsSpan()) old.Add(link.Participant);
                    });
            }
            match.LinkStart = _references.Count;
            match.LinkCount = bodies.Length;
            for (int index = 0; index < bodies.Length; index++)
            {
                if (index < _oldParticipants.Count)
                {
                    RelationEdge<ContactParticipant> existing = _oldParticipants[index];
                    if (_world.GetDirectedRelationEndpoints(existing).Target != bodies[index].Body)
                        commands.Relations<ContactParticipant>().Retarget(existing, match.Point, bodies[index].Body);
                    _references.Add(new(existing, default));
                }
                else
                {
                    DeferredRelationEdge<ContactParticipant> participant = commands.Relations<ContactParticipant>()
                        .Create(match.Point, bodies[index].Body, new ContactParticipant());
                    _references.Add(new(default, participant));
                }
            }
            for (int index = bodies.Length; index < _oldParticipants.Count; index++)
                commands.Relations<ContactParticipant>().Destroy(_oldParticipants[index]);
        }
    }

    private readonly struct ReadAnchors(ContactPoints<TCombine, TComponents> points) : IQueryExecution
    {
        public void Execute(scoped QueryCursor query)
        {
            foreach (ref PointMatch match in CollectionsMarshal.AsSpan(points._matches))
            {
                ref readonly ContactDetectionRange range = ref points._detected.Read(match.Input);
                ReadOnlySpan<ContactBody> bodies = points._detected.Bodies(match.Input);
                Vector3 delta = default;
                for (int index = 0; index < bodies.Length; index++)
                {
                    ref readonly ContactBody body = ref bodies[index];
                    query.TryGetRow(body.Body, out QueryRow row);
                    Vector3 position;
                    if (range.Contact.Prediction.Enabled)
                    {
                        ref readonly BodyMass mass = ref row.ReadRef<BodyMass>();
                        ref readonly BodyStepState step = ref row.ReadRef<BodyStepState>();
                        position = step.StartCenterOfMass + Vector3.Transform(body.LocalPoint - mass.CenterOfMass, step.StartRotation);
                    }
                    else
                    {
                        ref readonly BodyPose pose = ref row.ReadRef<BodyPose>();
                        position = pose.Position + Vector3.Transform(body.LocalPoint, pose.Rotation);
                    }
                    delta += position * (body.Weight * (index < range.Contact.FirstBodyCount ? -1 : 1));
                }
                match.BodyAnchorDelta = delta;
            }
        }
    }

    private void Write()
    {
        foreach (ref readonly PointMatch match in CollectionsMarshal.AsSpan(_matches))
        {
            ref readonly CollisionContact input = ref _detected.Read(match.Input).Contact;
            ContactPoint point = ToPoint(in input);
            point.BodyAnchorDelta = match.BodyAnchorDelta;
            _world.Replace(match.Point, point);
            _world.Replace(match.Point, match.Material);
            var copy = new WriteLinks(this, match.Input, match.LinkStart, match.LinkCount);
            _world.ExecuteBufferWrite<ContactBodyLink, WriteLinks>(match.Point, ref copy,
                static (links, ref copy) => copy.Write(links));
        }
    }

    private readonly record struct WriteLinks(ContactPoints<TCombine, TComponents> Points, int Input, int Start, int Count)
    {
        internal void Write(DynamicBuffer<ContactBodyLink> links)
        {
            links.Clear();
            ReadOnlySpan<ContactBody> bodies = Points._detected.Bodies(Input);
            for (int index = 0; index < Count; index++)
            {
                ParticipantReference reference = Points._references[Start + index];
                RelationEdge<ContactParticipant> participant = reference.Existing.IsNull ? reference.Created.Resolve() : reference.Existing;
                links.Add(new(participant, bodies[index].LocalPoint, bodies[index].Weight));
            }
        }
    }

    private ContactPoint ToPoint(in CollisionContact contact) => new()
    {
        FirstFeature = contact.FirstFeature, SecondFeature = contact.SecondFeature, PointIndex = contact.PointIndex,
        FirstLocalPoint = contact.FirstLocalPoint, SecondLocalPoint = contact.SecondLocalPoint,
        WorldPoint = contact.WorldPoint, Normal = contact.Normal, Separation = contact.Separation,
        FirstBodyCount = contact.FirstBodyCount, WarmStart = _settings.WarmStart,
        Prediction = contact.Prediction,
    };

    private readonly struct MatchPoints<TMatches>(ContactPoints<TCombine, TComponents> points, TMatches matches) : IJobParallelCommandProducer
        where TMatches : struct, IContactPointMatches
    {
        public void Execute(int producerIndex, ref JobCommandWriter commands) => points.Match(matches, ref commands);
    }
    private readonly struct AddComponents<TMatches>(ContactPoints<TCombine, TComponents> points, TMatches matches, PhysicsStepContext context) : IJobParallelCommandProducer
        where TMatches : struct, IContactPointMatches
    {
        private readonly PhysicsStepContext _context = context;
        public void Execute(int producerIndex, ref JobCommandWriter commands) => points.Add(matches, ref commands, in _context);
    }
    private readonly struct WriteValues(ContactPoints<TCombine, TComponents> points) : IJob
    {
        public void Execute() => points.Write();
    }
    private readonly struct ReleaseCommands(JobCommandBuffer points, JobCommandBuffer components) : IJob
    {
        public void Execute() { points.Dispose(); components.Dispose(); }
    }
    private struct PointMatch
    {
        internal Entity Point;
        internal DeferredRelationEdge<ContactPoint> Created;
        internal int Input, LinkStart, LinkCount;
        internal float DistanceSquared;
        internal Vector3 BodyAnchorDelta;
        internal ContactMaterial Material;
    }
    private readonly record struct ParticipantReference(RelationEdge<ContactParticipant> Existing, DeferredRelationEdge<ContactParticipant> Created);

    public void Dispose()
    {
        _world.ReleaseQuery(_points);
        _scheduler.ReleaseResourceToken(_resource);
    }
}

public readonly struct RefreshMatchedContactPoints<TCombine, TComponents, TMatches>(ContactPoints<TCombine, TComponents> points, TMatches matches) : IPhysicsStage
    where TCombine : struct, IContactMaterialCombine where TComponents : struct, IContactPointComponents
    where TMatches : struct, IContactPointMatches
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => points.Schedule(in context, matches, dependency);
}

public readonly struct RefreshContactPoints<TCombine, TComponents>(ContactPoints<TCombine, TComponents> points) : IPhysicsStage
    where TCombine : struct, IContactMaterialCombine where TComponents : struct, IContactPointComponents
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => points.Schedule(in context, dependency);
}
