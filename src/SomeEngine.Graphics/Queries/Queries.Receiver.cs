namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    QueryPool CreateQueryPool(Device device, in QueryPoolDesc desc);
    /// <summary>Discards previous results and prepares a range for writing. Record outside rendering,
    /// after prior users have completed or have been ordered before this operation.</summary>
    void ResetQueries(CommandContext context, QueryPool pool, uint firstQuery, uint queryCount);
    /// <summary>Begins a query previously prepared by ResetQueries.</summary>
    void BeginQuery(CommandContext context, QueryPool pool, uint queryIndex);
    void EndQuery(CommandContext context, QueryPool pool, uint queryIndex);
    void WriteTimestamp(CommandContext context, QueryPool pool, uint queryIndex);
    void ResolveQueries(
        CommandContext context,
        QueryPool pool,
        uint firstQuery,
        uint queryCount,
        Buffer destination,
        in BufferRange destinationRange);
}
