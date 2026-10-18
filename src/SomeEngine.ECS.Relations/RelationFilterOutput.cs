namespace SomeEngine.ECS.Relations;

public ref struct RelationFilterOutput<TRelation, TOutput, TRow> : IRelationOutput<TRow>
    where TRelation : struct, IRelation<TRow>, allows ref struct
    where TOutput : struct, IRelationOutput<TRow>, allows ref struct
{
    private TRelation _relation;

    public RelationFilterOutput(TRelation relation, TOutput output)
    {
        _relation = relation;
        Output = output;
    }

    public TOutput Output;

    public bool Add(in TRow row) => !_relation.Matches(in row) || Output.Add(in row);
}
