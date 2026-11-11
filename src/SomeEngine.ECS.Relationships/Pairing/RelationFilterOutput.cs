namespace SomeEngine.ECS.Relations;

internal ref struct RelationFilterOutput<TRelation, TOutput, TRow, TSelection>
    : IQueryRelationOutput<TRow>
    where TRelation : struct, IQueryRelation<TRow>, allows ref struct
    where TOutput : struct, IQueryRelationOutput<TRow>, allows ref struct
    where TSelection : struct, IRelationSelection
{
    private TRelation _relation;
    private readonly RelationOutputReference<TOutput> _output;
    public static bool UsesBoundColumns => true;

    public RelationFilterOutput(TRelation relation, RelationOutputReference<TOutput> output)
    {
        _relation = relation;
        _output = output;
    }

    public void Bind(QueryChunk first, QueryChunk second)
    {
        _relation.Bind(first, second);
        _output.Value.Bind(first, second);
    }

    public bool Add(in TRow row) => _relation.Matches(in row) != TSelection.RequiresMatch || _output.Value.Add(in row);

    public bool Add(in TRow row, int first, int second, bool reversed) =>
        _relation.Matches(in row, first, second, reversed) != TSelection.RequiresMatch ||
        (TOutput.UsesBoundColumns ? _output.Value.Add(in row, first, second, reversed) : _output.Value.Add(in row));
}
