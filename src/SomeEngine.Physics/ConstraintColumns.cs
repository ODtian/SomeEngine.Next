using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Typed addresses into one persistent constraint set's original fields, borrowed by its parent Job.</summary>
public sealed unsafe class ConstraintColumns<TConstraint, TState>
    where TConstraint : unmanaged where TState : unmanaged
{
    private struct Fields
    {
        internal TConstraint* Value;
        internal TState* State;
        internal RelationEndpoints<TConstraint>* Endpoints;
    }
    private Fields[] _fields = [];
    private int[] _indices = [];
    private int _count;
    public ref readonly TConstraint ReadConstraint(Entity entity) => ref *Get(entity).Value;
    public ref TState WriteState(Entity entity) => ref *Get(entity).State;
    public ref readonly TState ReadState(Entity entity) => ref *Get(entity).State;
    public ref readonly RelationEndpoints<TConstraint> ReadEndpoints(Entity entity) => ref *Get(entity).Endpoints;
    private ref readonly Fields Get(Entity entity) => ref _fields[_indices[entity.Id] - 1];
    public static JobResourceAccess[] Accesses(World world, Query members) =>
        ((WorldJobs)world.Execution!).CollectAccess<BindFields>(members);
    public void Bind(World world, Query members)
    {
        _count = 0; Array.Clear(_indices);
        if (_fields.Length < members.Count) Array.Resize(ref _fields, members.Count);
        using (var scope = members.OpenRead())
            foreach (QueryChunk chunk in scope)
                foreach (Entity entity in chunk.Entities)
                {
                    if (entity.Id >= _indices.Length)
                        Array.Resize(ref _indices, Math.Max(entity.Id + 1, Math.Max(16, _indices.Length * 2)));
                    _indices[entity.Id] = ++_count;
                }
        new SystemRunner(world).ExecuteParallel(members, new BindFields(this));
    }
    private readonly struct BindFields(ConstraintColumns<TConstraint, TState> columns) : IChunkAction
    {
        private static readonly ChunkAccess[] ComponentFields =
            [ChunkAccess.Read<TConstraint>(), ChunkAccess.Write<TState>(), ChunkAccess.ReadOptional<RelationEndpoints<TConstraint>>()];
        public static ReadOnlySpan<ChunkAccess> Components => ComponentFields;
        public void Execute(SystemChunk rows)
        {
            TConstraint* values = (TConstraint*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(rows.Read<TConstraint>()));
            TState* states = (TState*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(rows.Write<TState>()));
            RelationEndpoints<TConstraint>* endpoints = rows.TryRead<RelationEndpoints<TConstraint>>(out var data)
                ? (RelationEndpoints<TConstraint>*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(data)) : null;
            for (int row = 0; row < rows.Count; row++)
            {
                Entity entity = rows.Entities[row];
                int index = columns._indices[entity.Id] - 1;
                columns._fields[index] = new()
                {
                    Value = values + row,
                    State = states + row,
                    Endpoints = endpoints == null ? null : endpoints + row
                };
            }
        }
    }
}

/// <summary>The two motor input columns used by typed joint kernels during the same parent borrow.</summary>
internal sealed unsafe class JointMotorColumns
{
    private Fields[] _fields = [];
    private int[] _indices = [];
    private readonly struct Fields(JointMotor* motor, JointMotorTarget* target)
    {
        internal readonly JointMotor* Motor = motor;
        internal readonly JointMotorTarget* Target = target;
    }
    internal ref readonly JointMotor ReadMotor(Entity entity) => ref *_fields[_indices[entity.Id] - 1].Motor;
    internal ref readonly JointMotorTarget ReadTarget(Entity entity) => ref *_fields[_indices[entity.Id] - 1].Target;
    internal static JobResourceAccess[] Accesses(World world, Query members) =>
        ((WorldJobs)world.Execution!).CollectAccess<BindFields>(members);
    internal void Bind(World world, Query members)
    {
        Array.Clear(_indices); int count = members.Count, next = 0;
        if (_fields.Length < count) Array.Resize(ref _fields, count);
        using (var scope = members.OpenRead())
            foreach (QueryChunk chunk in scope)
                foreach (Entity entity in chunk.Entities)
                {
                    if (entity.Id >= _indices.Length)
                        Array.Resize(ref _indices, Math.Max(entity.Id + 1, Math.Max(16, _indices.Length * 2)));
                    _indices[entity.Id] = ++next;
                }
        new SystemRunner(world).ExecuteParallel(members, new BindFields(this));
    }
    private readonly struct BindFields(JointMotorColumns columns) : IChunkAction
    {
        private static readonly ChunkAccess[] ComponentFields = [ChunkAccess.Read<JointMotor>(), ChunkAccess.Read<JointMotorTarget>()];
        public static ReadOnlySpan<ChunkAccess> Components => ComponentFields;
        public void Execute(SystemChunk rows)
        {
            JointMotor* motors = (JointMotor*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(rows.Read<JointMotor>()));
            JointMotorTarget* targets = (JointMotorTarget*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(rows.Read<JointMotorTarget>()));
            for (int row = 0; row < rows.Count; row++)
                columns._fields[columns._indices[rows.Entities[row].Id] - 1] = new(motors + row, targets + row);
        }
    }
}
