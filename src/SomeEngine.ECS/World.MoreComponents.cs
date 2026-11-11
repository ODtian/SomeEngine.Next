using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity Create<T1, T2, T3, T4, T5, T6, T7>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth, in T6 sixth, in T7 seventh)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct
    {
        if (Execution is not null) return CreateWithExecution(first, second, third, fourth, fifth, sixth, seventh);
        return CreateCore(first, second, third, fourth, fifth, sixth, seventh);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Entity CreateWithExecution<T1, T2, T3, T4, T5, T6, T7>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth, in T6 sixth, in T7 seventh)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct
    {
        using WorldAccess access = OpenStructure();
        return CreateCore(first, second, third, fourth, fifth, sixth, seventh);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entity CreateCore<T1, T2, T3, T4, T5, T6, T7>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth, in T6 sixth, in T7 seventh)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct
    {
        if (ComponentType<T1>.HasInitializerType) ComponentType<T1>.ValidateCreate(this, first);
        if (ComponentType<T2>.HasInitializerType) ComponentType<T2>.ValidateCreate(this, second);
        if (ComponentType<T3>.HasInitializerType) ComponentType<T3>.ValidateCreate(this, third);
        if (ComponentType<T4>.HasInitializerType) ComponentType<T4>.ValidateCreate(this, fourth);
        if (ComponentType<T5>.HasInitializerType) ComponentType<T5>.ValidateCreate(this, fifth);
        if (ComponentType<T6>.HasInitializerType) ComponentType<T6>.ValidateCreate(this, sixth);
        if (ComponentType<T7>.HasInitializerType) ComponentType<T7>.ValidateCreate(this, seventh);
        Chunk chunk = Find(TypeSet<T1, T2, T3, T4, T5, T6, T7>.Value).WritableChunk;
        Entity entity = CreateKnown(ref chunk);
        using MembershipEdit membership = BeginMembershipEdit(entity, created: true);
        int row = chunk.Count - 1;
        WriteCreatedValue(entity, chunk, row, first);
        WriteCreatedValue(entity, chunk, row, second);
        WriteCreatedValue(entity, chunk, row, third);
        WriteCreatedValue(entity, chunk, row, fourth);
        WriteCreatedValue(entity, chunk, row, fifth);
        WriteCreatedValue(entity, chunk, row, sixth);
        WriteCreatedValue(entity, chunk, row, seventh);
        if (ComponentType<T1>.IsIndexedType) IndexCreated(entity, first);
        if (ComponentType<T2>.IsIndexedType) IndexCreated(entity, second);
        if (ComponentType<T3>.IsIndexedType) IndexCreated(entity, third);
        if (ComponentType<T4>.IsIndexedType) IndexCreated(entity, fourth);
        if (ComponentType<T5>.IsIndexedType) IndexCreated(entity, fifth);
        if (ComponentType<T6>.IsIndexedType) IndexCreated(entity, sixth);
        if (ComponentType<T7>.IsIndexedType) IndexCreated(entity, seventh);
        if (ComponentType<T1>.IsGroupedType || ComponentType<T2>.IsGroupedType || ComponentType<T3>.IsGroupedType || ComponentType<T4>.IsGroupedType || ComponentType<T5>.IsGroupedType || ComponentType<T6>.IsGroupedType || ComponentType<T7>.IsGroupedType)
            ArrangeCreatedGroups(entity);
        if (ComponentType<T1>.HasInitializerType) ComponentType<T1>.Initialize(this, entity);
        if (ComponentType<T2>.HasInitializerType) ComponentType<T2>.Initialize(this, entity);
        if (ComponentType<T3>.HasInitializerType) ComponentType<T3>.Initialize(this, entity);
        if (ComponentType<T4>.HasInitializerType) ComponentType<T4>.Initialize(this, entity);
        if (ComponentType<T5>.HasInitializerType) ComponentType<T5>.Initialize(this, entity);
        if (ComponentType<T6>.HasInitializerType) ComponentType<T6>.Initialize(this, entity);
        if (ComponentType<T7>.HasInitializerType) ComponentType<T7>.Initialize(this, entity);
        return entity;
    }

    public void Reserve<T1, T2, T3, T4, T5, T6, T7>(int capacity)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct
        => ReserveTypes(TypeSet<T1, T2, T3, T4, T5, T6, T7>.Value, capacity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth, in T6 sixth, in T7 seventh, in T8 eighth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct where T8 : struct
    {
        if (Execution is not null) return CreateWithExecution(first, second, third, fourth, fifth, sixth, seventh, eighth);
        return CreateCore(first, second, third, fourth, fifth, sixth, seventh, eighth);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Entity CreateWithExecution<T1, T2, T3, T4, T5, T6, T7, T8>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth, in T6 sixth, in T7 seventh, in T8 eighth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct where T8 : struct
    {
        using WorldAccess access = OpenStructure();
        return CreateCore(first, second, third, fourth, fifth, sixth, seventh, eighth);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entity CreateCore<T1, T2, T3, T4, T5, T6, T7, T8>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth, in T6 sixth, in T7 seventh, in T8 eighth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct where T8 : struct
    {
        if (ComponentType<T1>.HasInitializerType) ComponentType<T1>.ValidateCreate(this, first);
        if (ComponentType<T2>.HasInitializerType) ComponentType<T2>.ValidateCreate(this, second);
        if (ComponentType<T3>.HasInitializerType) ComponentType<T3>.ValidateCreate(this, third);
        if (ComponentType<T4>.HasInitializerType) ComponentType<T4>.ValidateCreate(this, fourth);
        if (ComponentType<T5>.HasInitializerType) ComponentType<T5>.ValidateCreate(this, fifth);
        if (ComponentType<T6>.HasInitializerType) ComponentType<T6>.ValidateCreate(this, sixth);
        if (ComponentType<T7>.HasInitializerType) ComponentType<T7>.ValidateCreate(this, seventh);
        if (ComponentType<T8>.HasInitializerType) ComponentType<T8>.ValidateCreate(this, eighth);
        Chunk chunk = Find(TypeSet<T1, T2, T3, T4, T5, T6, T7, T8>.Value).WritableChunk;
        Entity entity = CreateKnown(ref chunk);
        using MembershipEdit membership = BeginMembershipEdit(entity, created: true);
        int row = chunk.Count - 1;
        WriteCreatedValue(entity, chunk, row, first);
        WriteCreatedValue(entity, chunk, row, second);
        WriteCreatedValue(entity, chunk, row, third);
        WriteCreatedValue(entity, chunk, row, fourth);
        WriteCreatedValue(entity, chunk, row, fifth);
        WriteCreatedValue(entity, chunk, row, sixth);
        WriteCreatedValue(entity, chunk, row, seventh);
        WriteCreatedValue(entity, chunk, row, eighth);
        if (ComponentType<T1>.IsIndexedType) IndexCreated(entity, first);
        if (ComponentType<T2>.IsIndexedType) IndexCreated(entity, second);
        if (ComponentType<T3>.IsIndexedType) IndexCreated(entity, third);
        if (ComponentType<T4>.IsIndexedType) IndexCreated(entity, fourth);
        if (ComponentType<T5>.IsIndexedType) IndexCreated(entity, fifth);
        if (ComponentType<T6>.IsIndexedType) IndexCreated(entity, sixth);
        if (ComponentType<T7>.IsIndexedType) IndexCreated(entity, seventh);
        if (ComponentType<T8>.IsIndexedType) IndexCreated(entity, eighth);
        if (ComponentType<T1>.IsGroupedType || ComponentType<T2>.IsGroupedType || ComponentType<T3>.IsGroupedType || ComponentType<T4>.IsGroupedType || ComponentType<T5>.IsGroupedType || ComponentType<T6>.IsGroupedType || ComponentType<T7>.IsGroupedType || ComponentType<T8>.IsGroupedType)
            ArrangeCreatedGroups(entity);
        if (ComponentType<T1>.HasInitializerType) ComponentType<T1>.Initialize(this, entity);
        if (ComponentType<T2>.HasInitializerType) ComponentType<T2>.Initialize(this, entity);
        if (ComponentType<T3>.HasInitializerType) ComponentType<T3>.Initialize(this, entity);
        if (ComponentType<T4>.HasInitializerType) ComponentType<T4>.Initialize(this, entity);
        if (ComponentType<T5>.HasInitializerType) ComponentType<T5>.Initialize(this, entity);
        if (ComponentType<T6>.HasInitializerType) ComponentType<T6>.Initialize(this, entity);
        if (ComponentType<T7>.HasInitializerType) ComponentType<T7>.Initialize(this, entity);
        if (ComponentType<T8>.HasInitializerType) ComponentType<T8>.Initialize(this, entity);
        return entity;
    }

    public void Reserve<T1, T2, T3, T4, T5, T6, T7, T8>(int capacity)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct where T8 : struct
        => ReserveTypes(TypeSet<T1, T2, T3, T4, T5, T6, T7, T8>.Value, capacity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth, in T6 sixth, in T7 seventh, in T8 eighth, in T9 ninth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct where T8 : struct where T9 : struct
    {
        if (Execution is not null) return CreateWithExecution(first, second, third, fourth, fifth, sixth, seventh, eighth, ninth);
        return CreateCore(first, second, third, fourth, fifth, sixth, seventh, eighth, ninth);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Entity CreateWithExecution<T1, T2, T3, T4, T5, T6, T7, T8, T9>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth, in T6 sixth, in T7 seventh, in T8 eighth, in T9 ninth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct where T8 : struct where T9 : struct
    {
        using WorldAccess access = OpenStructure();
        return CreateCore(first, second, third, fourth, fifth, sixth, seventh, eighth, ninth);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entity CreateCore<T1, T2, T3, T4, T5, T6, T7, T8, T9>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth, in T6 sixth, in T7 seventh, in T8 eighth, in T9 ninth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct where T8 : struct where T9 : struct
    {
        if (ComponentType<T1>.HasInitializerType) ComponentType<T1>.ValidateCreate(this, first);
        if (ComponentType<T2>.HasInitializerType) ComponentType<T2>.ValidateCreate(this, second);
        if (ComponentType<T3>.HasInitializerType) ComponentType<T3>.ValidateCreate(this, third);
        if (ComponentType<T4>.HasInitializerType) ComponentType<T4>.ValidateCreate(this, fourth);
        if (ComponentType<T5>.HasInitializerType) ComponentType<T5>.ValidateCreate(this, fifth);
        if (ComponentType<T6>.HasInitializerType) ComponentType<T6>.ValidateCreate(this, sixth);
        if (ComponentType<T7>.HasInitializerType) ComponentType<T7>.ValidateCreate(this, seventh);
        if (ComponentType<T8>.HasInitializerType) ComponentType<T8>.ValidateCreate(this, eighth);
        if (ComponentType<T9>.HasInitializerType) ComponentType<T9>.ValidateCreate(this, ninth);
        Chunk chunk = Find(TypeSet<T1, T2, T3, T4, T5, T6, T7, T8, T9>.Value).WritableChunk;
        Entity entity = CreateKnown(ref chunk);
        using MembershipEdit membership = BeginMembershipEdit(entity, created: true);
        int row = chunk.Count - 1;
        WriteCreatedValue(entity, chunk, row, first);
        WriteCreatedValue(entity, chunk, row, second);
        WriteCreatedValue(entity, chunk, row, third);
        WriteCreatedValue(entity, chunk, row, fourth);
        WriteCreatedValue(entity, chunk, row, fifth);
        WriteCreatedValue(entity, chunk, row, sixth);
        WriteCreatedValue(entity, chunk, row, seventh);
        WriteCreatedValue(entity, chunk, row, eighth);
        WriteCreatedValue(entity, chunk, row, ninth);
        if (ComponentType<T1>.IsIndexedType) IndexCreated(entity, first);
        if (ComponentType<T2>.IsIndexedType) IndexCreated(entity, second);
        if (ComponentType<T3>.IsIndexedType) IndexCreated(entity, third);
        if (ComponentType<T4>.IsIndexedType) IndexCreated(entity, fourth);
        if (ComponentType<T5>.IsIndexedType) IndexCreated(entity, fifth);
        if (ComponentType<T6>.IsIndexedType) IndexCreated(entity, sixth);
        if (ComponentType<T7>.IsIndexedType) IndexCreated(entity, seventh);
        if (ComponentType<T8>.IsIndexedType) IndexCreated(entity, eighth);
        if (ComponentType<T9>.IsIndexedType) IndexCreated(entity, ninth);
        if (ComponentType<T1>.IsGroupedType || ComponentType<T2>.IsGroupedType || ComponentType<T3>.IsGroupedType || ComponentType<T4>.IsGroupedType || ComponentType<T5>.IsGroupedType || ComponentType<T6>.IsGroupedType || ComponentType<T7>.IsGroupedType || ComponentType<T8>.IsGroupedType || ComponentType<T9>.IsGroupedType)
            ArrangeCreatedGroups(entity);
        if (ComponentType<T1>.HasInitializerType) ComponentType<T1>.Initialize(this, entity);
        if (ComponentType<T2>.HasInitializerType) ComponentType<T2>.Initialize(this, entity);
        if (ComponentType<T3>.HasInitializerType) ComponentType<T3>.Initialize(this, entity);
        if (ComponentType<T4>.HasInitializerType) ComponentType<T4>.Initialize(this, entity);
        if (ComponentType<T5>.HasInitializerType) ComponentType<T5>.Initialize(this, entity);
        if (ComponentType<T6>.HasInitializerType) ComponentType<T6>.Initialize(this, entity);
        if (ComponentType<T7>.HasInitializerType) ComponentType<T7>.Initialize(this, entity);
        if (ComponentType<T8>.HasInitializerType) ComponentType<T8>.Initialize(this, entity);
        if (ComponentType<T9>.HasInitializerType) ComponentType<T9>.Initialize(this, entity);
        return entity;
    }

    public void Reserve<T1, T2, T3, T4, T5, T6, T7, T8, T9>(int capacity)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct where T8 : struct where T9 : struct
        => ReserveTypes(TypeSet<T1, T2, T3, T4, T5, T6, T7, T8, T9>.Value, capacity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth, in T6 sixth, in T7 seventh, in T8 eighth, in T9 ninth, in T10 tenth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct where T8 : struct where T9 : struct where T10 : struct
    {
        if (Execution is not null) return CreateWithExecution(first, second, third, fourth, fifth, sixth, seventh, eighth, ninth, tenth);
        return CreateCore(first, second, third, fourth, fifth, sixth, seventh, eighth, ninth, tenth);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Entity CreateWithExecution<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth, in T6 sixth, in T7 seventh, in T8 eighth, in T9 ninth, in T10 tenth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct where T8 : struct where T9 : struct where T10 : struct
    {
        using WorldAccess access = OpenStructure();
        return CreateCore(first, second, third, fourth, fifth, sixth, seventh, eighth, ninth, tenth);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entity CreateCore<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth, in T6 sixth, in T7 seventh, in T8 eighth, in T9 ninth, in T10 tenth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct where T8 : struct where T9 : struct where T10 : struct
    {
        if (ComponentType<T1>.HasInitializerType) ComponentType<T1>.ValidateCreate(this, first);
        if (ComponentType<T2>.HasInitializerType) ComponentType<T2>.ValidateCreate(this, second);
        if (ComponentType<T3>.HasInitializerType) ComponentType<T3>.ValidateCreate(this, third);
        if (ComponentType<T4>.HasInitializerType) ComponentType<T4>.ValidateCreate(this, fourth);
        if (ComponentType<T5>.HasInitializerType) ComponentType<T5>.ValidateCreate(this, fifth);
        if (ComponentType<T6>.HasInitializerType) ComponentType<T6>.ValidateCreate(this, sixth);
        if (ComponentType<T7>.HasInitializerType) ComponentType<T7>.ValidateCreate(this, seventh);
        if (ComponentType<T8>.HasInitializerType) ComponentType<T8>.ValidateCreate(this, eighth);
        if (ComponentType<T9>.HasInitializerType) ComponentType<T9>.ValidateCreate(this, ninth);
        if (ComponentType<T10>.HasInitializerType) ComponentType<T10>.ValidateCreate(this, tenth);
        Chunk chunk = Find(TypeSet<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>.Value).WritableChunk;
        Entity entity = CreateKnown(ref chunk);
        using MembershipEdit membership = BeginMembershipEdit(entity, created: true);
        int row = chunk.Count - 1;
        WriteCreatedValue(entity, chunk, row, first);
        WriteCreatedValue(entity, chunk, row, second);
        WriteCreatedValue(entity, chunk, row, third);
        WriteCreatedValue(entity, chunk, row, fourth);
        WriteCreatedValue(entity, chunk, row, fifth);
        WriteCreatedValue(entity, chunk, row, sixth);
        WriteCreatedValue(entity, chunk, row, seventh);
        WriteCreatedValue(entity, chunk, row, eighth);
        WriteCreatedValue(entity, chunk, row, ninth);
        WriteCreatedValue(entity, chunk, row, tenth);
        if (ComponentType<T1>.IsIndexedType) IndexCreated(entity, first);
        if (ComponentType<T2>.IsIndexedType) IndexCreated(entity, second);
        if (ComponentType<T3>.IsIndexedType) IndexCreated(entity, third);
        if (ComponentType<T4>.IsIndexedType) IndexCreated(entity, fourth);
        if (ComponentType<T5>.IsIndexedType) IndexCreated(entity, fifth);
        if (ComponentType<T6>.IsIndexedType) IndexCreated(entity, sixth);
        if (ComponentType<T7>.IsIndexedType) IndexCreated(entity, seventh);
        if (ComponentType<T8>.IsIndexedType) IndexCreated(entity, eighth);
        if (ComponentType<T9>.IsIndexedType) IndexCreated(entity, ninth);
        if (ComponentType<T10>.IsIndexedType) IndexCreated(entity, tenth);
        if (ComponentType<T1>.IsGroupedType || ComponentType<T2>.IsGroupedType || ComponentType<T3>.IsGroupedType || ComponentType<T4>.IsGroupedType || ComponentType<T5>.IsGroupedType || ComponentType<T6>.IsGroupedType || ComponentType<T7>.IsGroupedType || ComponentType<T8>.IsGroupedType || ComponentType<T9>.IsGroupedType || ComponentType<T10>.IsGroupedType)
            ArrangeCreatedGroups(entity);
        if (ComponentType<T1>.HasInitializerType) ComponentType<T1>.Initialize(this, entity);
        if (ComponentType<T2>.HasInitializerType) ComponentType<T2>.Initialize(this, entity);
        if (ComponentType<T3>.HasInitializerType) ComponentType<T3>.Initialize(this, entity);
        if (ComponentType<T4>.HasInitializerType) ComponentType<T4>.Initialize(this, entity);
        if (ComponentType<T5>.HasInitializerType) ComponentType<T5>.Initialize(this, entity);
        if (ComponentType<T6>.HasInitializerType) ComponentType<T6>.Initialize(this, entity);
        if (ComponentType<T7>.HasInitializerType) ComponentType<T7>.Initialize(this, entity);
        if (ComponentType<T8>.HasInitializerType) ComponentType<T8>.Initialize(this, entity);
        if (ComponentType<T9>.HasInitializerType) ComponentType<T9>.Initialize(this, entity);
        if (ComponentType<T10>.HasInitializerType) ComponentType<T10>.Initialize(this, entity);
        return entity;
    }

    public void Reserve<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(int capacity)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct where T8 : struct where T9 : struct where T10 : struct
        => ReserveTypes(TypeSet<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>.Value, capacity);

}
