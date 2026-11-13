using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

[Flags]
internal enum BodyWrites : byte { None = 0, Pose = 1, Velocity = 2, Step = 4, Activity = 8, Equation = 16 }
internal interface IBodyFields { static abstract BodyWrites Writes { get; } }
internal readonly struct ReadBodyFields : IBodyFields { public static BodyWrites Writes => BodyWrites.None; }
internal readonly struct SolveBodyFields : IBodyFields { public static BodyWrites Writes => BodyWrites.Pose | BodyWrites.Equation; }
internal readonly struct ProjectBodyFields : IBodyFields { public static BodyWrites Writes => BodyWrites.Pose; }
internal readonly struct PredictBodyFields : IBodyFields { public static BodyWrites Writes => BodyWrites.Pose | BodyWrites.Velocity | BodyWrites.Step; }
internal readonly struct VelocityBodyFields : IBodyFields { public static BodyWrites Writes => BodyWrites.Velocity; }
internal readonly struct ActivityBodyFields : IBodyFields { public static BodyWrites Writes => BodyWrites.Activity | BodyWrites.Velocity; }

/// <summary>Dense identities and addresses into the original pinned ECS columns.</summary>
internal sealed unsafe class BodyColumns
{
    internal struct Fields
    {
        internal BodyWrites Writes;
        internal BodyPose* Pose;
        internal BodyMass* Mass;
        internal BodyVelocity* Velocity;
        internal BodyStepState* Step;
        internal BodyActivity* Activity;
        internal AvbdBodyEquation* Equation;
    }
    private Fields[] _fields = [];
    private Entity[] _entities = [];
    private int[] _indices = [];
    private ulong _membership;
    private ulong _referenceMembership;
    private Query _referenceQuery;
    private int _boundCount;
    private bool _bound;
    internal int Count { get; private set; }
    internal BodyWrites Writes { get; private set; }
    internal ulong DirectoryVersion { get; private set; }
    internal ReadOnlySpan<Entity> Entities => _entities.AsSpan(0, Count);
    internal Entity EntityAt(int index) => _entities[index];
    internal int IndexOf(Entity entity) => _indices[entity.Id] - 1;
    internal ref readonly Fields Get(Entity entity) => ref _fields[IndexOf(entity)];
    internal bool Contains(Entity entity) => entity.Id > 0 && entity.Id < _indices.Length &&
        _indices[entity.Id] != 0 && _entities[_indices[entity.Id] - 1] == entity;

    // The caller keeps the original QueryScope and its parent Job's permissions until all ranges join.
    internal PhysicsBodies Bind<TFields>(World world, Query members) where TFields : struct, IBodyFields
    {
        if (!_bound) _referenceQuery = world.Query<BodyPose, BodyMass>();
        ulong membership = members.MembershipVersion;
        ulong referenceMembership = _referenceQuery.MembershipVersion;
        int count = members.Count;
        if (!_bound || membership != _membership || referenceMembership != _referenceMembership || count != Count)
        {
            Array.Clear(_indices);
            int capacity = Math.Max(count, _referenceQuery.Count);
            if (_fields.Length < capacity) Array.Resize(ref _fields, capacity);
            if (_entities.Length < capacity) Array.Resize(ref _entities, capacity);
            int index = 0;
            using (QueryScope scope = members.OpenRead())
                foreach (QueryChunk chunk in scope)
                    foreach (Entity entity in chunk.Entities) AddEntity(entity, ref index);
            using (QueryScope scope = _referenceQuery.OpenRead())
                foreach (QueryChunk chunk in scope)
                    foreach (Entity entity in chunk.Entities)
                        if (entity.Id >= _indices.Length || _indices[entity.Id] == 0) AddEntity(entity, ref index);
            _boundCount = index;
            DirectoryVersion++;
        }
        Count = count;
        Writes = TFields.Writes;
        var runner = new SystemRunner(world);
        runner.ExecuteParallel(_referenceQuery, new BindFields<ReadBodyFields>(this));
        runner.ExecuteParallel(members, new BindFields<TFields>(this));
        _membership = membership; _referenceMembership = referenceMembership; _bound = true;
        return new PhysicsBodies(this);
    }

    private void AddEntity(Entity entity, ref int index)
    {
        if (entity.Id >= _indices.Length)
            Array.Resize(ref _indices, Math.Max(entity.Id + 1, Math.Max(16, _indices.Length * 2)));
        if (index == _entities.Length)
        {
            int capacity = Math.Max(16, _entities.Length * 2);
            Array.Resize(ref _entities, capacity); Array.Resize(ref _fields, capacity);
        }
        _entities[index] = entity; _indices[entity.Id] = ++index;
    }

    internal static JobResourceAccess[] Accesses<TFields>(World world, Query members) where TFields : struct, IBodyFields
    {
        var jobs = (WorldJobs)world.Execution!;
        return [.. jobs.CollectAccess<BindFields<TFields>>(members), .. jobs.CollectAccess(ReferenceReads)];
    }
    private static readonly SystemAccesses ReferenceReads = new SystemAccesses()
        .ReadRandom<BodyPose>().ReadRandom<BodyMass>().ReadRandom<BodyVelocity>()
        .ReadRandom<BodyStepState>().ReadRandom<BodyActivity>().ReadRandom<AvbdBodyEquation>();

    private static T* Read<T>(ReadOnlySpan<T> values) where T : unmanaged =>
        (T*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(values));
    private static T* Write<T>(Span<T> values) where T : unmanaged =>
        (T*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(values));
    private static T* Bind<T>(SystemChunk rows, bool writes) where T : unmanaged =>
        rows.TryRead<T>(out ReadOnlySpan<T> values) ? writes ? Write(rows.Write<T>()) : Read(values) : null;

    private readonly struct BindFields<TFields>(BodyColumns columns) : IChunkAction where TFields : struct, IBodyFields
    {
        public static ReadOnlySpan<ChunkAccess> Components => BodyFieldAccess<TFields>.Components;
        public void Execute(SystemChunk rows)
        {
            BodyWrites writes = TFields.Writes;
            BodyPose* poses = Bind<BodyPose>(rows, (writes & BodyWrites.Pose) != 0);
            BodyMass* masses = Bind<BodyMass>(rows, false);
            BodyVelocity* velocities = Bind<BodyVelocity>(rows, (writes & BodyWrites.Velocity) != 0);
            BodyStepState* steps = Bind<BodyStepState>(rows, (writes & BodyWrites.Step) != 0);
            BodyActivity* activities = Bind<BodyActivity>(rows, (writes & BodyWrites.Activity) != 0);
            AvbdBodyEquation* equations = Bind<AvbdBodyEquation>(rows, (writes & BodyWrites.Equation) != 0);
            for (int row = 0; row < rows.Count; row++)
            {
                Entity entity = rows.Entities[row];
                int index = columns._indices[entity.Id] - 1;
                columns._fields[index] = new Fields
                {
                    Writes = index < columns.Count ? writes : BodyWrites.None,
                    Pose = poses == null ? null : poses + row, Mass = masses == null ? null : masses + row,
                    Velocity = velocities == null ? null : velocities + row, Step = steps == null ? null : steps + row,
                    Activity = activities == null ? null : activities + row, Equation = equations == null ? null : equations + row,
                };
            }
        }
    }

    private static class BodyFieldAccess<TFields> where TFields : struct, IBodyFields
    {
        internal static readonly ChunkAccess[] Components =
        [
            (TFields.Writes & BodyWrites.Pose) != 0 ? ChunkAccess.WriteOptional<BodyPose>() : ChunkAccess.ReadOptional<BodyPose>(),
            ChunkAccess.ReadOptional<BodyMass>(),
            (TFields.Writes & BodyWrites.Velocity) != 0 ? ChunkAccess.WriteOptional<BodyVelocity>() : ChunkAccess.ReadOptional<BodyVelocity>(),
            (TFields.Writes & BodyWrites.Step) != 0 ? ChunkAccess.WriteOptional<BodyStepState>() : ChunkAccess.ReadOptional<BodyStepState>(),
            (TFields.Writes & BodyWrites.Activity) != 0 ? ChunkAccess.WriteOptional<BodyActivity>() : ChunkAccess.ReadOptional<BodyActivity>(),
            (TFields.Writes & BodyWrites.Equation) != 0 ? ChunkAccess.WriteOptional<AvbdBodyEquation>() : ChunkAccess.ReadOptional<AvbdBodyEquation>(),
        ];
    }
}
