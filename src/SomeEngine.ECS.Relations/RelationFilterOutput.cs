using SomeEngine.ECS.Queries;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Relations;

internal ref struct RelationFilterOutput<TRelation, TOutput, TRow, TSelection> : IQueryRelationOutput<TRow>
    where TRelation : struct, IQueryRelation<TRow>, allows ref struct
    where TOutput : struct, IQueryRelationOutput<TRow>, allows ref struct
    where TSelection : struct, IRelationSelection
{
    public static bool ReadsComponentRows => true;

    private TRelation _relation;

    public RelationFilterOutput(TRelation relation, RelationOutputReference<TOutput> output)
    {
        _relation = relation;
        _output = output;
    }

    private readonly RelationOutputReference<TOutput> _output;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(in TRow row)
    {
        if (TSelection.RequiresMatch)
        {
            if (!_relation.Matches(in row))
                return true;
        }
        else if (_relation.Matches(in row))
            return true;
        return _output.Value.Add(in row);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(in TRow row, scoped QueryRow first, scoped QueryRow second)
    {
        if (TSelection.RequiresMatch)
        {
            if (!_relation.Matches(in row, first, second))
                return true;
        }
        else if (_relation.Matches(in row, first, second))
            return true;
        return TOutput.ReadsComponentRows ? _output.Value.Add(in row, first, second) : _output.Value.Add(in row);
    }
}
