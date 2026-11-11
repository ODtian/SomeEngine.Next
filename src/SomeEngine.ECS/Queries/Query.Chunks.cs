using System.Runtime.CompilerServices;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public partial struct Query
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ForEachChunks<T, TAction>(ref TAction action)
        where T : struct where TAction : struct, IComponentAction<T>
    {
        int type1 = ComponentType<T>.TypeId;
        ulong version = 0;
        QueryMatch[] matches = _matches;
        int length = _count;
        for (int i = 0; i < length; i++)
        {
            QueryMatch match = matches[i];
            if (match.Filter is not null) { ForEachFilteredChunks<T, TAction>(match, ref action, ref version); continue; }
            foreach (Chunk chunk in match.Archetype.AllChunks)
            {
                int count = chunk.Count;
                if (count == 0) continue;
                ColumnEntry[] columns = chunk.Columns;
                Span<T> value1 = ColumnEntry.Span<T>(ref columns[type1], count);
                if (ComponentType<T>.TracksWritesType) _world.StampRange<T>(chunk, 0, count, ref version);
                action.Execute(value1);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ForEachChunks<T1, T2, TAction>(ref TAction action)
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        int type1 = ComponentType<T1>.TypeId;
        int type2 = ComponentType<T2>.TypeId;
        ulong version = 0;
        QueryMatch[] matches = _matches;
        int length = _count;
        for (int i = 0; i < length; i++)
        {
            QueryMatch match = matches[i];
            if (match.Filter is not null) { ForEachFilteredChunks<T1, T2, TAction>(match, ref action, ref version); continue; }
            foreach (Chunk chunk in match.Archetype.AllChunks)
            {
                int count = chunk.Count;
                if (count == 0) continue;
                ColumnEntry[] columns = chunk.Columns;
                Span<T1> value1 = ColumnEntry.Span<T1>(ref columns[type1], count);
                ReadOnlySpan<T2> value2 = ColumnEntry.Span<T2>(ref columns[type2], count);
                if (ComponentType<T1>.TracksWritesType) _world.StampRange<T1>(chunk, 0, count, ref version);
                action.Execute(value1, value2);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ForEachChunks<T1, T2, T3, TAction>(ref TAction action)
        where T1 : struct where T2 : struct where T3 : struct where TAction : struct, IComponentAction<T1, T2, T3>
    {
        int type1 = ComponentType<T1>.TypeId;
        int type2 = ComponentType<T2>.TypeId;
        int type3 = ComponentType<T3>.TypeId;
        ulong version = 0;
        QueryMatch[] matches = _matches;
        int length = _count;
        for (int i = 0; i < length; i++)
        {
            QueryMatch match = matches[i];
            if (match.Filter is not null) { ForEachFilteredChunks<T1, T2, T3, TAction>(match, ref action, ref version); continue; }
            foreach (Chunk chunk in match.Archetype.AllChunks)
            {
                int count = chunk.Count;
                if (count == 0) continue;
                ColumnEntry[] columns = chunk.Columns;
                Span<T1> first = ColumnEntry.Span<T1>(ref columns[type1], count);
                ReadOnlySpan<T2> second = ColumnEntry.Span<T2>(ref columns[type2], count);
                ReadOnlySpan<T3> third = ColumnEntry.Span<T3>(ref columns[type3], count);
                if (ComponentType<T1>.TracksWritesType) _world.StampRange<T1>(chunk, 0, count, ref version);
                action.Execute(first, second, third);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ForEachFilteredChunks<T, TAction>(QueryMatch match, ref TAction action, ref ulong version)
        where T : struct where TAction : struct, IComponentAction<T>
    {
        foreach (Chunk chunk in match.Archetype.AllChunks)
        {
            int count = chunk.Count;
            if (count == 0) continue;
            Span<T> values = ColumnEntry.Span<T>(ref chunk.Columns[ComponentType<T>.TypeId], count);
            ExecuteFiltered<T, TAction>(chunk, match.Filter!, values, ref action, ref version);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ForEachFilteredChunks<T1, T2, TAction>(QueryMatch match, ref TAction action, ref ulong version)
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        foreach (Chunk chunk in match.Archetype.AllChunks)
        {
            int count = chunk.Count;
            if (count == 0) continue;
            ColumnEntry[] columns = chunk.Columns;
            Span<T1> first = ColumnEntry.Span<T1>(ref columns[ComponentType<T1>.TypeId], count);
            ReadOnlySpan<T2> second = ColumnEntry.Span<T2>(ref columns[ComponentType<T2>.TypeId], count);
            ExecuteFiltered<T1, T2, TAction>(chunk, match.Filter!, first, second, ref action, ref version);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ForEachFilteredChunks<T1, T2, T3, TAction>(QueryMatch match, ref TAction action, ref ulong version)
        where T1 : struct where T2 : struct where T3 : struct where TAction : struct, IComponentAction<T1, T2, T3>
    {
        foreach (Chunk chunk in match.Archetype.AllChunks)
        {
            int count = chunk.Count;
            if (count == 0) continue;
            ColumnEntry[] columns = chunk.Columns;
            Span<T1> first = ColumnEntry.Span<T1>(ref columns[ComponentType<T1>.TypeId], count);
            ReadOnlySpan<T2> second = ColumnEntry.Span<T2>(ref columns[ComponentType<T2>.TypeId], count);
            ReadOnlySpan<T3> third = ColumnEntry.Span<T3>(ref columns[ComponentType<T3>.TypeId], count);
            ExecuteFiltered<T1, T2, T3, TAction>(chunk, match.Filter!, first, second, third, ref action, ref version);
        }
    }

}
