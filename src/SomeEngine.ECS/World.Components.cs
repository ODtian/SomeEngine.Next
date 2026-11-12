using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity Create<T1, T2>(in T1 first, in T2 second)
        where T1 : struct where T2 : struct
    {
        if (Execution is not null) return CreateWithExecution(first, second);
        return CreateCore(first, second);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Entity CreateWithExecution<T1, T2>(in T1 first, in T2 second)
        where T1 : struct where T2 : struct
    {
        using WorldAccess access = OpenStructure();
        return CreateCore(first, second);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entity CreateCore<T1, T2>(in T1 first, in T2 second)
        where T1 : struct where T2 : struct
    {
        if (ComponentType<T1>.HasInitializerType) ComponentType<T1>.ValidateCreate(this, first);
        if (ComponentType<T2>.HasInitializerType) ComponentType<T2>.ValidateCreate(this, second);
        Chunk chunk = Find(TypeSet<T1, T2>.Value).WritableChunk;
        Entity entity = CreateKnown(ref chunk);
        using MembershipEdit membership = BeginMembershipEdit(entity, created: true);
        int row = chunk.Count - 1;
        WriteCreatedValue(entity, chunk, row, first);
        WriteCreatedValue(entity, chunk, row, second);
        if (ComponentType<T1>.IsIndexedType) IndexCreated(entity, first);
        if (ComponentType<T2>.IsIndexedType) IndexCreated(entity, second);
        if (ComponentType<T1>.IsGroupedType || ComponentType<T2>.IsGroupedType) ArrangeCreatedGroups(entity);
        if (ComponentType<T1>.HasInitializerType) ComponentType<T1>.Initialize(this, entity);
        if (ComponentType<T2>.HasInitializerType) ComponentType<T2>.Initialize(this, entity);
        return entity;
    }

    public void Reserve<T1, T2>(int capacity)
        where T1 : struct where T2 : struct
        => ReserveTypes(TypeSet<T1, T2>.Value, capacity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity Create<T1, T2, T3, T4>(in T1 first, in T2 second, in T3 third, in T4 fourth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct
    {
        if (Execution is not null) return CreateWithExecution(first, second, third, fourth);
        return CreateCore(first, second, third, fourth);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Entity CreateWithExecution<T1, T2, T3, T4>(in T1 first, in T2 second, in T3 third, in T4 fourth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct
    {
        using WorldAccess access = OpenStructure();
        return CreateCore(first, second, third, fourth);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entity CreateCore<T1, T2, T3, T4>(in T1 first, in T2 second, in T3 third, in T4 fourth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct
    {
        if (ComponentType<T1>.HasInitializerType) ComponentType<T1>.ValidateCreate(this, first);
        if (ComponentType<T2>.HasInitializerType) ComponentType<T2>.ValidateCreate(this, second);
        if (ComponentType<T3>.HasInitializerType) ComponentType<T3>.ValidateCreate(this, third);
        if (ComponentType<T4>.HasInitializerType) ComponentType<T4>.ValidateCreate(this, fourth);
        Chunk chunk = Find(TypeSet<T1, T2, T3, T4>.Value).WritableChunk;
        Entity entity = CreateKnown(ref chunk);
        using MembershipEdit membership = BeginMembershipEdit(entity, created: true);
        int row = chunk.Count - 1;
        WriteCreatedValue(entity, chunk, row, first);
        WriteCreatedValue(entity, chunk, row, second);
        WriteCreatedValue(entity, chunk, row, third);
        WriteCreatedValue(entity, chunk, row, fourth);
        if (ComponentType<T1>.IsIndexedType) IndexCreated(entity, first);
        if (ComponentType<T2>.IsIndexedType) IndexCreated(entity, second);
        if (ComponentType<T3>.IsIndexedType) IndexCreated(entity, third);
        if (ComponentType<T4>.IsIndexedType) IndexCreated(entity, fourth);
        if (ComponentType<T1>.IsGroupedType || ComponentType<T2>.IsGroupedType || ComponentType<T3>.IsGroupedType || ComponentType<T4>.IsGroupedType)
            ArrangeCreatedGroups(entity);
        if (ComponentType<T1>.HasInitializerType) ComponentType<T1>.Initialize(this, entity);
        if (ComponentType<T2>.HasInitializerType) ComponentType<T2>.Initialize(this, entity);
        if (ComponentType<T3>.HasInitializerType) ComponentType<T3>.Initialize(this, entity);
        if (ComponentType<T4>.HasInitializerType) ComponentType<T4>.Initialize(this, entity);
        return entity;
    }

    public void Reserve<T1, T2, T3, T4>(int capacity)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct
        => ReserveTypes(TypeSet<T1, T2, T3, T4>.Value, capacity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity Create<T1, T2, T3, T4, T5>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct
    {
        if (Execution is not null) return CreateWithExecution(first, second, third, fourth, fifth);
        return CreateCore(first, second, third, fourth, fifth);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Entity CreateWithExecution<T1, T2, T3, T4, T5>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct
    {
        using WorldAccess access = OpenStructure();
        return CreateCore(first, second, third, fourth, fifth);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entity CreateCore<T1, T2, T3, T4, T5>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct
    {
        if (ComponentType<T1>.HasInitializerType) ComponentType<T1>.ValidateCreate(this, first);
        if (ComponentType<T2>.HasInitializerType) ComponentType<T2>.ValidateCreate(this, second);
        if (ComponentType<T3>.HasInitializerType) ComponentType<T3>.ValidateCreate(this, third);
        if (ComponentType<T4>.HasInitializerType) ComponentType<T4>.ValidateCreate(this, fourth);
        if (ComponentType<T5>.HasInitializerType) ComponentType<T5>.ValidateCreate(this, fifth);
        Chunk chunk = Find(TypeSet<T1, T2, T3, T4, T5>.Value).WritableChunk;
        Entity entity = CreateKnown(ref chunk);
        using MembershipEdit membership = BeginMembershipEdit(entity, created: true);
        int row = chunk.Count - 1;
        WriteCreatedValue(entity, chunk, row, first);
        WriteCreatedValue(entity, chunk, row, second);
        WriteCreatedValue(entity, chunk, row, third);
        WriteCreatedValue(entity, chunk, row, fourth);
        WriteCreatedValue(entity, chunk, row, fifth);
        if (ComponentType<T1>.IsIndexedType) IndexCreated(entity, first);
        if (ComponentType<T2>.IsIndexedType) IndexCreated(entity, second);
        if (ComponentType<T3>.IsIndexedType) IndexCreated(entity, third);
        if (ComponentType<T4>.IsIndexedType) IndexCreated(entity, fourth);
        if (ComponentType<T5>.IsIndexedType) IndexCreated(entity, fifth);
        if (ComponentType<T1>.IsGroupedType || ComponentType<T2>.IsGroupedType || ComponentType<T3>.IsGroupedType || ComponentType<T4>.IsGroupedType || ComponentType<T5>.IsGroupedType)
            ArrangeCreatedGroups(entity);
        if (ComponentType<T1>.HasInitializerType) ComponentType<T1>.Initialize(this, entity);
        if (ComponentType<T2>.HasInitializerType) ComponentType<T2>.Initialize(this, entity);
        if (ComponentType<T3>.HasInitializerType) ComponentType<T3>.Initialize(this, entity);
        if (ComponentType<T4>.HasInitializerType) ComponentType<T4>.Initialize(this, entity);
        if (ComponentType<T5>.HasInitializerType) ComponentType<T5>.Initialize(this, entity);
        return entity;
    }

    public void Reserve<T1, T2, T3, T4, T5>(int capacity)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct
        => ReserveTypes(TypeSet<T1, T2, T3, T4, T5>.Value, capacity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity Create<T1, T2, T3, T4, T5, T6>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth, in T6 sixth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct
    {
        if (Execution is not null) return CreateWithExecution(first, second, third, fourth, fifth, sixth);
        return CreateCore(first, second, third, fourth, fifth, sixth);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Entity CreateWithExecution<T1, T2, T3, T4, T5, T6>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth, in T6 sixth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct
    {
        using WorldAccess access = OpenStructure();
        return CreateCore(first, second, third, fourth, fifth, sixth);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entity CreateCore<T1, T2, T3, T4, T5, T6>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth, in T6 sixth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct
    {
        if (ComponentType<T1>.HasInitializerType) ComponentType<T1>.ValidateCreate(this, first);
        if (ComponentType<T2>.HasInitializerType) ComponentType<T2>.ValidateCreate(this, second);
        if (ComponentType<T3>.HasInitializerType) ComponentType<T3>.ValidateCreate(this, third);
        if (ComponentType<T4>.HasInitializerType) ComponentType<T4>.ValidateCreate(this, fourth);
        if (ComponentType<T5>.HasInitializerType) ComponentType<T5>.ValidateCreate(this, fifth);
        if (ComponentType<T6>.HasInitializerType) ComponentType<T6>.ValidateCreate(this, sixth);
        Chunk chunk = Find(TypeSet<T1, T2, T3, T4, T5, T6>.Value).WritableChunk;
        Entity entity = CreateKnown(ref chunk);
        using MembershipEdit membership = BeginMembershipEdit(entity, created: true);
        int row = chunk.Count - 1;
        WriteCreatedValue(entity, chunk, row, first);
        WriteCreatedValue(entity, chunk, row, second);
        WriteCreatedValue(entity, chunk, row, third);
        WriteCreatedValue(entity, chunk, row, fourth);
        WriteCreatedValue(entity, chunk, row, fifth);
        WriteCreatedValue(entity, chunk, row, sixth);
        if (ComponentType<T1>.IsIndexedType) IndexCreated(entity, first);
        if (ComponentType<T2>.IsIndexedType) IndexCreated(entity, second);
        if (ComponentType<T3>.IsIndexedType) IndexCreated(entity, third);
        if (ComponentType<T4>.IsIndexedType) IndexCreated(entity, fourth);
        if (ComponentType<T5>.IsIndexedType) IndexCreated(entity, fifth);
        if (ComponentType<T6>.IsIndexedType) IndexCreated(entity, sixth);
        if (ComponentType<T1>.IsGroupedType || ComponentType<T2>.IsGroupedType || ComponentType<T3>.IsGroupedType
            || ComponentType<T4>.IsGroupedType || ComponentType<T5>.IsGroupedType || ComponentType<T6>.IsGroupedType)
            ArrangeCreatedGroups(entity);
        if (ComponentType<T1>.HasInitializerType) ComponentType<T1>.Initialize(this, entity);
        if (ComponentType<T2>.HasInitializerType) ComponentType<T2>.Initialize(this, entity);
        if (ComponentType<T3>.HasInitializerType) ComponentType<T3>.Initialize(this, entity);
        if (ComponentType<T4>.HasInitializerType) ComponentType<T4>.Initialize(this, entity);
        if (ComponentType<T5>.HasInitializerType) ComponentType<T5>.Initialize(this, entity);
        if (ComponentType<T6>.HasInitializerType) ComponentType<T6>.Initialize(this, entity);
        return entity;
    }

    public void Reserve<T1, T2, T3, T4, T5, T6>(int capacity)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct
        => ReserveTypes(TypeSet<T1, T2, T3, T4, T5, T6>.Value, capacity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteCreatedValue<T>(Entity entity, Chunk chunk, int row, in T value) where T : struct
    {
        if (ComponentType<T>.IsSharedType)
        {
            var values = Shared<T>();
            int id = values.Intern(value);
            ColumnEntry.At<int>(ref chunk.Columns[ComponentType<T>.TypeId], row) = id;
            values.Groups.Set(entity, -1, id, true);
        }
        else if (!ComponentType<T>.IsTagType)
            ColumnEntry.At<T>(ref chunk.Columns[ComponentType<T>.TypeId], row) =
                ComponentType<T>.HasValueOperationsType ? ComponentType<T>.CloneValue(this, value) : value;
    }
}
