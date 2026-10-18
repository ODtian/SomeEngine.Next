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

    public RelationFilterOutput(TRelation relation, TOutput output)
    {
        _relation = relation;
        Output = output;
    }

    public TOutput Output;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(in TRow row) => !TSelection.Accepts(_relation.Matches(in row)) || Output.Add(in row);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(in TRow row, scoped in QueryRow first, scoped in QueryRow second)
    {
        return !TSelection.Accepts(_relation.Matches(in row, in first, in second)) || (TOutput.ReadsComponentRows
            ? Output.Add(in row, in first, in second) : Output.Add(in row));
    }
}
