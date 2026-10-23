namespace SomeEngine.ECS.Relations;

public ref struct NegatedRelation<TRelation, TRow> : IRelation<TRow>
    where TRelation : struct, IRelation<TRow>, allows ref struct
{
    private TRelation _relation;

    public NegatedRelation(TRelation relation) => _relation = relation;

    public bool Matches(in TRow row) => !_relation.Matches(in row);
}
