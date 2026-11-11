namespace SomeEngine.ECS.Relations;

/// <summary>Accepts identities with row indices in the pair of chunks most recently bound.</summary>
public interface IQueryRelationOutput<TRow> : IRelationOutput<TRow>
{
    static virtual bool UsesBoundColumns => true;
    void Bind(QueryChunk first, QueryChunk second);
    bool Add(in TRow row, int first, int second, bool reversed);
}

/// <summary>Binds columns once per chunk pair and reads their rows by index.</summary>
public interface IQueryRelation<TRow> : IRelation<TRow>
{
    void Bind(QueryChunk first, QueryChunk second);
    bool Matches(in TRow row, int first, int second, bool reversed);
}

internal ref struct QueryIdentityOutput<TOutput, TRow>(TOutput output) : IQueryRelationOutput<TRow>
    where TOutput : struct, IRelationOutput<TRow>, allows ref struct
{
    public static bool UsesBoundColumns => false;
    public TOutput Value = output;
    public bool Add(in TRow row) => Value.Add(in row);
    public void Bind(QueryChunk first, QueryChunk second) { }
    public bool Add(in TRow row, int first, int second, bool reversed) => Value.Add(in row);
}
