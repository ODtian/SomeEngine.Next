using System.Runtime.CompilerServices;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public interface IComponentAction<T1, T2> where T1 : struct where T2 : struct
{
    void Execute(Span<T1> first, ReadOnlySpan<T2> second);
}

public interface IComponentAction<T1, T2, T3> where T1 : struct where T2 : struct where T3 : struct
{
    void Execute(Span<T1> first, ReadOnlySpan<T2> second, ReadOnlySpan<T3> third);
}

public partial struct Query
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ForEach<T1, T2, TAction>(ref TAction action)
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        if (_world.Execution is null) ForEachValues<T1, T2, TAction>(ref action, false);
        else ForEachWithExecution<T1, T2, TAction, WorldExecutionQueryPolicy>(ref action, new(_world.Execution));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void ForEachWithExecution<T1, T2, TAction, TExecution>(ref TAction action, TExecution execution)
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
        where TExecution : struct, IQueryExecutionPolicy
    {
        World world = _world;
        execution.EnterQuery(this, [ComponentType<T1>.TypeId]);
        try
        {
            if (ComponentType<T1>.IsControlledType || ComponentType<T1>.IsIndexedType || ComponentType<T1>.IsGroupedType) throw new InvalidOperationException("Use the component's typed write view or Set entry.");
            if (ComponentType<T2>.IsSharedType) throw new InvalidOperationException("Use the range's shared value entry.");
            bool borrowRead = execution.CanBorrowStructureRead;
            if (!borrowRead) world.BeginRead(true);
            try
            {
                RefreshIfNeeded();
                if (world.Execution is not null && _data.AllRows && !ComponentType<T1>.IsTagType && !ComponentType<T2>.IsTagType
                    && !ComponentType<T1>.TracksWritesType && !RuntimeHelpers.IsReferenceOrContainsReferences<T1>()
                    && !RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                {
                    PodColumnBuffer? first = _data.FirstValues, second = _data.SecondValues;
                    if (first is null || first.TypeId != ComponentType<T1>.TypeId)
                        _data.FirstValues = first = world.PodBuffer(ComponentType<T1>.Value);
                    if (second is null || second.TypeId != ComponentType<T2>.TypeId)
                        _data.SecondValues = second = world.PodBuffer(ComponentType<T2>.Value);
                    first.ForEach<T1, T2, TAction>(second, ref action);
                    return;
                }
                ForEachMatchedValues<T1, T2, TAction>(ref action);
            }
            finally { if (!borrowRead) world.EndRead(true); }
        }
        finally { execution.Exit(); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ForEachValues<T1, T2, TAction>(ref TAction action, bool withExecution)
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        if (ComponentType<T1>.IsControlledType || ComponentType<T1>.IsIndexedType || ComponentType<T1>.IsGroupedType) throw new InvalidOperationException("Use the component's typed write view or Set entry.");
        if (ComponentType<T2>.IsSharedType) throw new InvalidOperationException("Use the range's shared value entry.");
        World world = _world;
        bool borrowRead = withExecution && world.Execution!.CanBorrowStructureRead;
        if (!borrowRead) world.BeginRead(withExecution);
        try
        {
            RefreshIfNeeded();
            ForEachMatchedValues<T1, T2, TAction>(ref action);
        }
        finally { if (!borrowRead) world.EndRead(withExecution); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ForEachMatchedValues<T1, T2, TAction>(ref TAction action)
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        if (_world.Execution is null && _data.ValueTypes == typeof(ValueTuple<T1, T2>).TypeHandle.Value)
        {
            _data.FirstValues!.ForEach<T1, T2, TAction>(_data.SecondValues!, ref action);
            return;
        }
        if (_world.HasMultipleChunks) { ForEachChunks<T1, T2, TAction>(ref action); return; }
        int type1 = ComponentType<T1>.TypeId;
        int type2 = ComponentType<T2>.TypeId;
        QueryMatch[] matches = _matches;
        int length = _count;
        ulong version = 0;
        for (int i = 0; i < length; i++)
        {
            QueryMatch match = matches[i];
            Chunk chunk = match.Archetype.PrimaryChunk;
            int count = chunk.Count;
            if (count == 0) continue;
            ColumnEntry[] columns = chunk.Columns;
            Span<T1> first = ColumnEntry.Span<T1>(ref columns[type1], count);
            ReadOnlySpan<T2> second = ColumnEntry.Span<T2>(ref columns[type2], count);
            if (match.Filter is null)
            {
                if (ComponentType<T1>.TracksWritesType) _world.StampRange<T1>(chunk, 0, count, ref version);
                action.Execute(first, second);
            }
            else ExecuteFiltered<T1, T2, TAction>(chunk, match.Filter, first, second, ref action, ref version);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ExecuteFiltered<T1, T2, TAction>(Chunk chunk, QueryRowFilter filter,
        Span<T1> first, ReadOnlySpan<T2> second, ref TAction action, ref ulong version)
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        int next = 0;
        while (filter.Next(chunk, _since, ref next, out int start, out int count))
        {
            if (ComponentType<T1>.TracksWritesType) _world.StampRange<T1>(chunk, start, count, ref version);
            action.Execute(first.Slice(start, count), second.Slice(start, count));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ForEach<T1, T2, T3, TAction>(ref TAction action)
        where T1 : struct where T2 : struct where T3 : struct where TAction : struct, IComponentAction<T1, T2, T3>
    {
        if (_world.Execution is null) ForEachValues<T1, T2, T3, TAction>(ref action, false);
        else ForEachWithExecution<T1, T2, T3, TAction, WorldExecutionQueryPolicy>(ref action, new(_world.Execution));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void ForEachWithExecution<T1, T2, T3, TAction, TExecution>(ref TAction action, TExecution execution)
        where T1 : struct where T2 : struct where T3 : struct where TAction : struct, IComponentAction<T1, T2, T3>
        where TExecution : struct, IQueryExecutionPolicy
    {
        World world = _world;
        execution.EnterQuery(this, [ComponentType<T1>.TypeId]);
        try
        {
            if (ComponentType<T1>.IsControlledType || ComponentType<T1>.IsIndexedType || ComponentType<T1>.IsGroupedType) throw new InvalidOperationException("Use the component's typed write view or Set entry.");
            if (ComponentType<T2>.IsSharedType || ComponentType<T3>.IsSharedType)
                throw new InvalidOperationException("Use the range's shared value entry.");
            bool borrowRead = execution.CanBorrowStructureRead;
            if (!borrowRead) world.BeginRead(true);
            try
            {
                RefreshIfNeeded();
                if (world.Execution is not null && _data.AllRows && !ComponentType<T1>.IsTagType && !ComponentType<T2>.IsTagType && !ComponentType<T3>.IsTagType
                    && !ComponentType<T1>.TracksWritesType && !RuntimeHelpers.IsReferenceOrContainsReferences<T1>()
                    && !RuntimeHelpers.IsReferenceOrContainsReferences<T2>()
                    && !RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                {
                    PodColumnBuffer? first = _data.FirstValues, second = _data.SecondValues, third = _data.ThirdValues;
                    if (first is null || first.TypeId != ComponentType<T1>.TypeId)
                        _data.FirstValues = first = world.PodBuffer(ComponentType<T1>.Value);
                    if (second is null || second.TypeId != ComponentType<T2>.TypeId)
                        _data.SecondValues = second = world.PodBuffer(ComponentType<T2>.Value);
                    if (third is null || third.TypeId != ComponentType<T3>.TypeId)
                        _data.ThirdValues = third = world.PodBuffer(ComponentType<T3>.Value);
                    first.ForEach<T1, T2, T3, TAction>(second, third, ref action);
                    return;
                }
                ForEachMatchedValues<T1, T2, T3, TAction>(ref action);
            }
            finally { if (!borrowRead) world.EndRead(true); }
        }
        finally { execution.Exit(); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ForEachValues<T1, T2, T3, TAction>(ref TAction action, bool withExecution)
        where T1 : struct where T2 : struct where T3 : struct where TAction : struct, IComponentAction<T1, T2, T3>
    {
        if (ComponentType<T1>.IsControlledType || ComponentType<T1>.IsIndexedType || ComponentType<T1>.IsGroupedType) throw new InvalidOperationException("Use the component's typed write view or Set entry.");
        if (ComponentType<T2>.IsSharedType || ComponentType<T3>.IsSharedType)
            throw new InvalidOperationException("Use the range's shared value entry.");
        World world = _world;
        bool borrowRead = withExecution && world.Execution!.CanBorrowStructureRead;
        if (!borrowRead) world.BeginRead(withExecution);
        try
        {
            RefreshIfNeeded();
            ForEachMatchedValues<T1, T2, T3, TAction>(ref action);
        }
        finally { if (!borrowRead) world.EndRead(withExecution); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ForEachMatchedValues<T1, T2, T3, TAction>(ref TAction action)
        where T1 : struct where T2 : struct where T3 : struct where TAction : struct, IComponentAction<T1, T2, T3>
    {
        if (_world.Execution is null && _data.ValueTypes == typeof(ValueTuple<T1, T2, T3>).TypeHandle.Value)
        {
            _data.FirstValues!.ForEach<T1, T2, T3, TAction>(_data.SecondValues!, _data.ThirdValues!, ref action);
            return;
        }
        if (_world.HasMultipleChunks) { ForEachChunks<T1, T2, T3, TAction>(ref action); return; }
        int type1 = ComponentType<T1>.TypeId;
        int type2 = ComponentType<T2>.TypeId;
        int type3 = ComponentType<T3>.TypeId;
        QueryMatch[] matches = _matches;
        int length = _count;
        ulong version = 0;
        for (int i = 0; i < length; i++)
        {
            QueryMatch match = matches[i];
            Chunk chunk = match.Archetype.PrimaryChunk;
            int count = chunk.Count;
            if (count == 0) continue;
            ColumnEntry[] columns = chunk.Columns;
            Span<T1> first = ColumnEntry.Span<T1>(ref columns[type1], count);
            ReadOnlySpan<T2> second = ColumnEntry.Span<T2>(ref columns[type2], count);
            ReadOnlySpan<T3> third = ColumnEntry.Span<T3>(ref columns[type3], count);
            if (match.Filter is null)
            {
                if (ComponentType<T1>.TracksWritesType) _world.StampRange<T1>(chunk, 0, count, ref version);
                action.Execute(first, second, third);
            }
            else ExecuteFiltered<T1, T2, T3, TAction>(chunk, match.Filter, first, second, third, ref action, ref version);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ExecuteFiltered<T1, T2, T3, TAction>(Chunk chunk, QueryRowFilter filter,
        Span<T1> first, ReadOnlySpan<T2> second, ReadOnlySpan<T3> third, ref TAction action, ref ulong version)
        where T1 : struct where T2 : struct where T3 : struct where TAction : struct, IComponentAction<T1, T2, T3>
    {
        int next = 0;
        while (filter.Next(chunk, _since, ref next, out int start, out int count))
        {
            if (ComponentType<T1>.TracksWritesType) _world.StampRange<T1>(chunk, start, count, ref version);
            action.Execute(first.Slice(start, count), second.Slice(start, count), third.Slice(start, count));
        }
    }

}
