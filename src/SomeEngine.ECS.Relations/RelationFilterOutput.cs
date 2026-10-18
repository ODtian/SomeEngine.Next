using SomeEngine.ECS.Queries;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Relations;

public ref struct RelationFilterOutput<TRelation, TOutput, TRow> : IQueryRelationOutput<TRow>
    where TRelation : struct, IQueryRelation<TRow>, allows ref struct
    where TOutput : struct, IQueryRelationOutput<TRow>, allows ref struct
{
    public static bool ReadsComponentRows => true;

    private TRelation _relation;
    private readonly bool _exclude;

    public RelationFilterOutput(TRelation relation, TOutput output, bool exclude = false)
    {
        _relation = relation;
        _exclude = exclude;
        Output = output;
    }

    public TOutput Output;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(in TRow row) => _relation.Matches(in row) == _exclude || Output.Add(in row);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(in TRow row, scoped in QueryRow first, scoped in QueryRow second)
    {
        return _relation.Matches(in row, in first, in second) == _exclude || (TOutput.ReadsComponentRows
            ? Output.Add(in row, in first, in second) : Output.Add(in row));
    }
}
