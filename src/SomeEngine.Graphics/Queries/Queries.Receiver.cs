namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    QueryPool CreateQueryPool(Device device, in QueryPoolDesc desc);
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
