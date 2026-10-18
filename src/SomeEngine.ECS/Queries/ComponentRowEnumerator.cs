using SomeEngine.ECS.Archetypes;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Queries;

/// <summary>Member-row traversal with callback-scoped optional component columns.</summary>
public ref struct ComponentRowEnumerator<TFirst, TSecond>
    where TFirst : struct
    where TSecond : struct
{
    private QueryRowEnumerator<NoSharedFilter> _rows;
    private ReadOnlySpan<TFirst> _firstComponents;
    private ReadOnlySpan<TSecond> _secondComponents;

    internal ComponentRowEnumerator(QueryRowEnumerator<NoSharedFilter> rows)
    {
        _rows = rows;
        _firstComponents = default;
        _secondComponents = default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext(out QueryRow row, out ComponentReference<TFirst> first, out ComponentReference<TSecond> second)
    {
        Chunk? previous = _rows.CurrentChunk;
        if (!_rows.MoveNext(out row))
        {
            first = default;
            second = default;
            return false;
        }
        if (!ReferenceEquals(previous, _rows.CurrentChunk))
            BindColumns(_rows.CurrentMatch, _rows.CurrentChunk!);

        first = _firstComponents.IsEmpty ? default : new(in _firstComponents[row.RowIndex]);
        // Repeated positions borrow the same component type, which proves this reference cast.
        second = typeof(TFirst) == typeof(TSecond)
            ? new(in Unsafe.As<TFirst, TSecond>(ref Unsafe.AsRef(in first.Value)))
            : _secondComponents.IsEmpty ? default : new(in _secondComponents[row.RowIndex]);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void BindColumns(QueryArchetypeMatch match, Chunk chunk)
    {
        _firstComponents = QueryAccessGuards.TryRequireReadAccess<TFirst>(match, out int firstColumn)
            ? chunk.ComponentRows<TFirst>(firstColumn) : default;
        if (typeof(TFirst) != typeof(TSecond))
            _secondComponents = QueryAccessGuards.TryRequireReadAccess<TSecond>(match, out int secondColumn)
                ? chunk.ComponentRows<TSecond>(secondColumn) : default;
    }
}
