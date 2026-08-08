using System.Runtime.CompilerServices;

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

public sealed partial class Graphics<TBackend>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public QueryPool CreateQueryPool(Device device, in QueryPoolDesc desc) =>
        Receiver.CreateQueryPool(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginQuery(CommandContext context, QueryPool pool, uint queryIndex) =>
        Receiver.BeginQuery(context, pool, queryIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EndQuery(CommandContext context, QueryPool pool, uint queryIndex) =>
        Receiver.EndQuery(context, pool, queryIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTimestamp(CommandContext context, QueryPool pool, uint queryIndex) =>
        Receiver.WriteTimestamp(context, pool, queryIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResolveQueries(
        CommandContext context,
        QueryPool pool,
        uint firstQuery,
        uint queryCount,
        Buffer destination,
        in BufferRange destinationRange) =>
        Receiver.ResolveQueries(
            context,
            pool,
            firstQuery,
            queryCount,
            destination,
            destinationRange);
}
