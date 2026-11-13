using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public enum PhysicsGraphOrder : byte { Islands, Colors }
public enum PhysicsGraphPhase : byte { Metadata, Initialize, Bodies, Duals }
public readonly record struct PhysicsGraphSettings(PhysicsGraphOrder Order, int SmallIslandBodyLimit,
    int BodiesPerBatch, int LargeIslandBodyLimit, int RowsPerBlock, bool UseSimd);
public readonly record struct PhysicsConstraintBody(Entity Body, bool Writes);
public readonly record struct PhysicsConstraintIdentity(int Query, Entity Entity, int Element = 0);
internal readonly record struct PhysicsBodyRange(int Start, int Count);
internal readonly record struct PhysicsConstraintRange(int Start, int Count, bool Independent = false);
internal readonly record struct PhysicsBodyGroup(PhysicsBodyRange[] Ranges, int Count);
internal readonly record struct PhysicsConstraintGroup(PhysicsConstraintRange[] Ranges, int Count);

public readonly ref struct PhysicsConstraintGraphBuilder
{
    private readonly PhysicsGraphData _data;
    private readonly PhysicsBodies _bodies;
    internal PhysicsConstraintGraphBuilder(PhysicsGraphData data, PhysicsBodies bodies) { _data = data; _bodies = bodies; }
    public void Clear(int set) => _data.Clear(set);
    public void BeginSet(int set) => _data.BeginSet(set);
    public void EndSet(int set) => _data.EndSet(set);
    public void Remove(int set, Entity entity, int element = 0) => _data.Remove(set, entity, element);
    public void Add(int set, Entity constraint, ReadOnlySpan<PhysicsConstraintBody> bodies, ReadOnlySpan<Entity> references) =>
        _data.Add(set, constraint, 0, bodies, references);
    public void Add(int set, Entity constraint, int element, ReadOnlySpan<PhysicsConstraintBody> bodies, ReadOnlySpan<Entity> references) =>
        _data.Add(set, constraint, element, bodies, references);
    public void Add<TConstraint, TParticipants>(int set, World world, Entity entity, in TConstraint value,
        in PhysicsStepContext context)
        where TConstraint : struct, IComponent where TParticipants : struct, IPhysicsConstraintBodies<TConstraint>
    {
        int writes = TParticipants.GetBodyCount(in value, world, entity, in context);
        int reads = TParticipants.GetReadBodyCount(in value, world, entity, in context);
        Span<PhysicsConstraintBody> bodies = stackalloc PhysicsConstraintBody[writes + reads];
        for (int i = 0; i < writes; i++) bodies[i] = new(PhysicsConstraintBodies.GetBody<TConstraint, TParticipants>(
            in value, world, entity, i, _bodies, in context), true);
        for (int i = 0; i < reads; i++) bodies[writes + i] = new(TParticipants.GetReadBody(in value, world, entity, i, in context), false);
        Span<Entity> references = stackalloc Entity[TParticipants.GetReadEntityCount(in value, world, entity, in context)];
        for (int i = 0; i < references.Length; i++) references[i] = TParticipants.GetReadEntity(in value, world, entity, i, in context);
        _data.Add(set, entity, 0, bodies, references);
    }
}

internal sealed partial class PhysicsGraphData : IDisposable
{
    private struct ConstraintRow
    {
        internal PhysicsConstraintIdentity Identity;
        internal int BodyStart, BodyCount, ReferenceStart, ReferenceCount;
        internal bool Active;
        internal int ReadGeneration;
    }
    private sealed class ConstraintSet
    {
        internal readonly List<ConstraintRow> Rows = [];
        internal readonly List<PhysicsConstraintBody> Bodies = [];
        internal readonly List<Entity> References = [];
        internal int[] ByEntity = [], ByElement = [];
        internal int ReadGeneration;
        internal int Find(Entity entity, int element)
        {
            int[] indices = entity == Entity.Null ? ByElement : ByEntity;
            int key = entity == Entity.Null ? element : entity.Id;
            int row = key < indices.Length ? indices[key] - 1 : -1;
            return row >= 0 && Rows[row].Identity.Entity == entity ? row : -1;
        }
        internal void Index(Entity entity, int element, int row)
        {
            ref int[] indices = ref (entity == Entity.Null ? ref ByElement : ref ByEntity);
            int key = entity == Entity.Null ? element : entity.Id;
            if (key >= indices.Length) Array.Resize(ref indices, Math.Max(key + 1, Math.Max(16, indices.Length * 2)));
            indices[key] = row + 1;
        }
    }
    private readonly PhysicsGraphSettings _settings;
    private readonly JobResourceToken _resource;
    private readonly ConstraintSet[] _sets;
    private BodyColumns _columns = null!;
    private bool[] _dynamic = [], _activityChanges = [];
    private ulong _directory;
    private bool _persistentDirty = true, _dirty = true;
    private readonly List<(int Set, int Row)> _rows = [];
    private int[] _baseParents = [], _parents = [], _islandOfBody = [], _islandStarts = [], _islandBodies = [];
    private int[] _bodyOrder = [];
    private PhysicsConstraintIdentity[] _constraintOrder = [];
    internal PhysicsBodyGroup[] BodyGroups { get; private set; } = [];
    internal PhysicsConstraintGroup[] ProjectionGroups { get; private set; } = [];
    internal PhysicsActivityIsland[] ActivityIslands { get; private set; } = [];
    internal JobResourceAccess ReadAccess => JobResourceAccess.Read(_resource);
    internal JobResourceAccess WriteAccess => JobResourceAccess.Write(_resource);
    internal Entity BodyAt(int orderIndex) => _columns.EntityAt(_bodyOrder[orderIndex]);
    internal PhysicsConstraintIdentity ConstraintAt(int orderIndex) => _constraintOrder[orderIndex];
    internal ReadOnlySpan<PhysicsConstraintIdentity> ConstraintSpan(int start, int count) => _constraintOrder.AsSpan(start, count);
    internal PhysicsGraphData(Scheduler scheduler, int queryCount, PhysicsGraphSettings settings, bool bodyUpdates = false)
    {
        _settings = settings; _resource = scheduler.GetResourceToken(this);
        _sets = new ConstraintSet[queryCount];
        for (int i = 0; i < queryCount; i++) _sets[i] = new ConstraintSet();
    }
    internal void Begin() { }
    internal void ReadBodies(PhysicsBodies bodies)
    {
        _columns = bodies.Columns;
        int count = _columns.Count;
        if (_dynamic.Length != count) { Array.Resize(ref _dynamic, count); Array.Resize(ref _activityChanges, count); _persistentDirty = _dirty = true; }
        if (_directory != _columns.DirectoryVersion) { _directory = _columns.DirectoryVersion; _persistentDirty = _dirty = true; Array.Fill(_activityChanges, true); }
        for (int i = 0; i < count; i++)
        {
            bool dynamic = bodies.ReadMass(_columns.EntityAt(i)).Motion == BodyMotion.Dynamic;
            if (_dynamic[i] != dynamic) { _dynamic[i] = dynamic; _activityChanges[i] = true; _persistentDirty = _dirty = true; }
        }
    }
    internal void Clear(int setIndex)
    {
        ConstraintSet set = _sets[setIndex];
        foreach (ConstraintRow row in set.Rows) if (row.Active) MarkActivity(CollectionsMarshal.AsSpan(set.Bodies).Slice(row.BodyStart, row.BodyCount));
        if (set.Rows.Any(row => row.Active && row.Identity.Entity != Entity.Null)) _persistentDirty = true;
        set.Rows.Clear(); set.Bodies.Clear(); set.References.Clear(); Array.Clear(set.ByEntity); Array.Clear(set.ByElement); _dirty = true;
    }
    internal void BeginSet(int setIndex) => _sets[setIndex].ReadGeneration++;
    internal void EndSet(int setIndex)
    {
        ConstraintSet set = _sets[setIndex];
        for (int i = 0; i < set.Rows.Count; i++)
        {
            ConstraintRow row = set.Rows[i];
            if (row.Active && row.ReadGeneration != set.ReadGeneration) Remove(setIndex, row.Identity.Entity, row.Identity.Element);
        }
    }
    internal void Remove(int setIndex, Entity entity, int element = 0)
    {
        ConstraintSet set = _sets[setIndex]; int index = set.Find(entity, element);
        if (index < 0 || !set.Rows[index].Active) return;
        ConstraintRow row = set.Rows[index];
        MarkActivity(CollectionsMarshal.AsSpan(set.Bodies).Slice(row.BodyStart, row.BodyCount));
        row.Active = false; set.Rows[index] = row; _dirty = true; _persistentDirty |= entity != Entity.Null;
    }
    internal void Add(int setIndex, Entity entity, int element, ReadOnlySpan<PhysicsConstraintBody> bodies, ReadOnlySpan<Entity> references)
    {
        ConstraintSet set = _sets[setIndex]; int index = set.Find(entity, element);
        ConstraintRow row = index < 0 ? default : set.Rows[index];
        row.ReadGeneration = set.ReadGeneration;
        if (index >= 0 && row.Active && row.BodyCount == bodies.Length && row.ReferenceCount == references.Length &&
            CollectionsMarshal.AsSpan(set.Bodies).Slice(row.BodyStart, row.BodyCount).SequenceEqual(bodies) &&
            CollectionsMarshal.AsSpan(set.References).Slice(row.ReferenceStart, row.ReferenceCount).SequenceEqual(references))
        { set.Rows[index] = row; return; }
        if (row.Active) MarkActivity(CollectionsMarshal.AsSpan(set.Bodies).Slice(row.BodyStart, row.BodyCount));
        bool added = index < 0;
        if (added) { index = set.Rows.Count; set.Rows.Add(default); set.Index(entity, element, index); }
        if (row.BodyCount != bodies.Length || added) { row.BodyStart = set.Bodies.Count; foreach (var body in bodies) set.Bodies.Add(body); }
        else bodies.CopyTo(CollectionsMarshal.AsSpan(set.Bodies).Slice(row.BodyStart, row.BodyCount));
        if (row.ReferenceCount != references.Length || added) { row.ReferenceStart = set.References.Count; foreach (Entity reference in references) set.References.Add(reference); }
        else references.CopyTo(CollectionsMarshal.AsSpan(set.References).Slice(row.ReferenceStart, row.ReferenceCount));
        row.BodyCount = bodies.Length; row.ReferenceCount = references.Length; row.Identity = new(setIndex, entity, element); row.Active = true;
        set.Rows[index] = row; MarkActivity(bodies); _dirty = true; _persistentDirty |= entity != Entity.Null;
    }
    private void MarkActivity(ReadOnlySpan<PhysicsConstraintBody> bodies)
    {
        foreach (var body in bodies)
            if (_columns is not null && _columns.Contains(body.Body))
            { int index = _columns.IndexOf(body.Body); if (index < _activityChanges.Length) _activityChanges[index] = true; }
    }
    internal bool IsActivityChanged(Entity body) => _columns.Contains(body) && _columns.IndexOf(body) < _activityChanges.Length && _activityChanges[_columns.IndexOf(body)];
    internal bool HasActivityChange(Entity body) => IsActivityChanged(body);
    internal void ClearActivityChanges() => Array.Clear(_activityChanges);
    internal bool Finish()
    {
        if (!_dirty) return false;
        BuildGroups(); _dirty = false; _persistentDirty = false; return true;
    }
    public void Dispose() { foreach (ConstraintSet set in _sets) { set.Rows.Clear(); set.Bodies.Clear(); set.References.Clear(); } }
}
