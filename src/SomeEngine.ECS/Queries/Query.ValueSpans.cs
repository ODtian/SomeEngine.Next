using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public partial struct Query
{
    // The returned values follow World.Read/Get's lifetime through the next structural change.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValues<T>(out Span<T> values) where T : struct
    {
        if (_singleValues is { } buffer && buffer.TryFilledValues(out values)) return true;
        if (_data.SingleChunkType == ComponentType<T>.TypeId && _data.SingleChunk is { } chunk
            && chunk.Archetype.ChunkCount == 1)
        {
            values = ColumnEntry.Span<T>(ref chunk.Columns[ComponentType<T>.TypeId], chunk.Count);
            return true;
        }
        return BindValues(out values);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool BindValues<T>(out Span<T> values) where T : struct
    {
        World world = _world;
        if (world.Execution is null && !ComponentType<T>.IsTagType && !ComponentType<T>.IsControlledType && !ComponentType<T>.IsIndexedType
            && !ComponentType<T>.IsGroupedType && !ComponentType<T>.TracksWritesType)
        {
            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>() && !ComponentType<T>.IsTagType
                && !ComponentType<T>.IsEnableableType && _data.SingleValueType == ComponentType<T>.TypeId)
            {
                PodColumnBuffer buffer = _data.SingleValues ?? BindSingleValues<T>();
                _singleValues = buffer;
                if (buffer.TryFilledValues(out values)) return true;
            }
            return TryGeneralValues(out values);
        }
        values = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private PodColumnBuffer BindSingleValues<T>() where T : struct
        => _data.SingleValues = _world.ObservePodValues<T>();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryGeneralValues<T>(out Span<T> values) where T : struct
    {
        RefreshIfNeeded();
        if (_data.AllRows && _world.TryPodValues(out values)) return true;
        return TrySingleValues(out values);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValues<T1, T2>(out Span<T1> first, out ReadOnlySpan<T2> second)
        where T1 : struct where T2 : struct
    {
        QueryData data = _data;
        if (data.ValueTypes == typeof(ValueTuple<T1, T2>).TypeHandle.Value)
        {
            if (data.FirstValues!.TryBoundFilledValues(out first))
            {
                second = data.SecondValues!.AllValues<T2>(first.Length);
                return true;
            }
            return TryTwoChunk(out first, out second);
        }
        return BindTwoValues(out first, out second);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool BindTwoValues<T1, T2>(out Span<T1> first, out ReadOnlySpan<T2> second)
        where T1 : struct where T2 : struct
    {
        World world = _world;
        if (world.Execution is null && !ComponentType<T1>.IsTagType && !ComponentType<T2>.IsTagType
            && !ComponentType<T1>.IsControlledType && !ComponentType<T1>.IsIndexedType
            && !ComponentType<T1>.IsGroupedType && !ComponentType<T1>.TracksWritesType && !ComponentType<T2>.IsSharedType)
        {
            RefreshIfNeeded();
            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T1>()
                && !RuntimeHelpers.IsReferenceOrContainsReferences<T2>()
                && _description.Required.Has(ComponentType<T1>.TypeId)
                && _description.Required.Has(ComponentType<T2>.TypeId))
            {
                _data.FirstValues = world.ObservePodValues<T1>();
                _data.SecondValues = world.PodBuffer(ComponentType<T2>.Value);
                _data.ThirdValues = null;
                _data.ValueTypes = _data.AllRows ? typeof(ValueTuple<T1, T2>).TypeHandle.Value : 0;
                if (_data.AllRows && _data.FirstValues.TryFilledValues(out first))
                {
                    second = _data.SecondValues.AllValues<T2>(first.Length);
                    return true;
                }
            }
            return TryTwoChunk(out first, out second);
        }
        first = default;
        second = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValues<T1, T2, T3>(out Span<T1> first, out ReadOnlySpan<T2> second, out ReadOnlySpan<T3> third)
        where T1 : struct where T2 : struct where T3 : struct
    {
        QueryData data = _data;
        if (data.ValueTypes == typeof(ValueTuple<T1, T2, T3>).TypeHandle.Value)
        {
            if (data.FirstValues!.TryBoundFilledValues(out first))
            {
                second = data.SecondValues!.AllValues<T2>(first.Length);
                third = data.ThirdValues!.AllValues<T3>(first.Length);
                return true;
            }
            return TryThreeChunk(out first, out second, out third);
        }
        return BindThreeValues(out first, out second, out third);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool BindThreeValues<T1, T2, T3>(out Span<T1> first, out ReadOnlySpan<T2> second, out ReadOnlySpan<T3> third)
        where T1 : struct where T2 : struct where T3 : struct
    {
        World world = _world;
        if (world.Execution is null && !ComponentType<T1>.IsTagType && !ComponentType<T2>.IsTagType && !ComponentType<T3>.IsTagType
            && !ComponentType<T1>.IsControlledType && !ComponentType<T1>.IsIndexedType
            && !ComponentType<T1>.IsGroupedType && !ComponentType<T1>.TracksWritesType
            && !ComponentType<T2>.IsSharedType && !ComponentType<T3>.IsSharedType)
        {
            RefreshIfNeeded();
            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T1>() && !ComponentType<T1>.IsTagType
                && !RuntimeHelpers.IsReferenceOrContainsReferences<T2>() && !ComponentType<T2>.IsTagType
                && !RuntimeHelpers.IsReferenceOrContainsReferences<T3>() && !ComponentType<T3>.IsTagType
                && _description.Required.Has(ComponentType<T1>.TypeId)
                && _description.Required.Has(ComponentType<T2>.TypeId)
                && _description.Required.Has(ComponentType<T3>.TypeId))
            {
                _data.FirstValues = world.ObservePodValues<T1>();
                _data.SecondValues = world.PodBuffer(ComponentType<T2>.Value);
                _data.ThirdValues = world.PodBuffer(ComponentType<T3>.Value);
                _data.ValueTypes = _data.AllRows ? typeof(ValueTuple<T1, T2, T3>).TypeHandle.Value : 0;
                if (_data.AllRows && _data.FirstValues.TryFilledValues(out first))
                {
                    second = _data.SecondValues.AllValues<T2>(first.Length);
                    third = _data.ThirdValues.AllValues<T3>(first.Length);
                    return true;
                }
            }
            return TryThreeChunk(out first, out second, out third);
        }
        first = default;
        second = default;
        third = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryTwoChunk<T1, T2>(out Span<T1> first, out ReadOnlySpan<T2> second)
        where T1 : struct where T2 : struct
    {
        if (TrySingleChunk(out Chunk? chunk))
        {
            ColumnEntry[] columns = chunk.Columns;
            first = ColumnEntry.Span<T1>(ref columns[ComponentType<T1>.TypeId], chunk.Count);
            second = ColumnEntry.Span<T2>(ref columns[ComponentType<T2>.TypeId], chunk.Count);
            return true;
        }
        first = default; second = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryThreeChunk<T1, T2, T3>(out Span<T1> first, out ReadOnlySpan<T2> second, out ReadOnlySpan<T3> third)
        where T1 : struct where T2 : struct where T3 : struct
    {
        if (TrySingleChunk(out Chunk? chunk))
        {
            ColumnEntry[] columns = chunk.Columns;
            first = ColumnEntry.Span<T1>(ref columns[ComponentType<T1>.TypeId], chunk.Count);
            second = ColumnEntry.Span<T2>(ref columns[ComponentType<T2>.TypeId], chunk.Count);
            third = ColumnEntry.Span<T3>(ref columns[ComponentType<T3>.TypeId], chunk.Count);
            return true;
        }
        first = default; second = default; third = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TrySingleValues<T>(out Span<T> values) where T : struct
    {
        if (TrySingleChunk(out Chunk? chunk))
        {
            _data.SingleChunkType = ComponentType<T>.TypeId;
            _data.SingleChunk = chunk;
            values = ColumnEntry.Span<T>(ref chunk.Columns[ComponentType<T>.TypeId], chunk.Count);
            return true;
        }
        values = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TrySingleChunk([NotNullWhen(true)] out Chunk? chunk)
    {
        if (_count == 1)
        {
            QueryMatch match = _matches[0];
            if (match.Filter is null && match.Archetype.ChunkCount == 1)
            {
                chunk = match.Archetype.PrimaryChunk;
                return true;
            }
        }
        chunk = null;
        return false;
    }
}
