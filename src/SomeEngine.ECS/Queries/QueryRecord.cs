namespace SomeEngine.ECS.Queries;

internal sealed class QueryRecord
{
    public QueryRecord(QueryHandle handle, QueryDefinition definition, QueryState state)
    {
        Handle = handle;
        Definition = definition;
        State = state;
    }

    public QueryHandle Handle { get; }

    public QueryDefinition Definition { get; }

    public QueryState State { get; }
}

