using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using NativeQueryType = Silk.NET.Direct3D12.QueryType;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    public QueryPool CreateQueryPool(Device device, in QueryPoolDesc desc)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        if (!Enum.IsDefined(desc.Type))
            throw new ArgumentOutOfRangeException(nameof(desc), "The QueryType is unknown.");
        if (!Enum.IsDefined(desc.QueueType))
            throw new ArgumentOutOfRangeException(nameof(desc), "The QueueType is unknown.");
        if (desc.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(desc));
        if (desc.Type != SomeEngine.Graphics.QueryType.Timestamp &&
            desc.QueueType != QueueType.Graphics)
        {
            throw new ArgumentException(
                "Non-timestamp queries require the Graphics Queue family.",
                nameof(desc));
        }
        if (desc.Type == SomeEngine.Graphics.QueryType.StreamOutputStatistics)
        {
            if (desc.StreamIndex > 3)
                throw new ArgumentOutOfRangeException(nameof(desc), "StreamIndex must be in [0, 3].");
        }
        else if (desc.StreamIndex != 0)
        {
            throw new ArgumentException(
                "StreamIndex is only valid for StreamOutputStatistics queries.",
                nameof(desc));
        }
        uint nodeIndex = nativeDevice.ResolveNodeIndex(desc.NodeIndex, nameof(desc));
        QueryPoolDesc resolvedDescription = desc with { NodeIndex = nodeIndex };
        QueryResultInfo resultInfo = GetQueryResultInfo(desc.Type);
        QueryHeapDesc nativeDescription = new(
            ToQueryHeapType(desc.Type, desc.QueueType),
            desc.Count,
            1u << checked((int)nodeIndex));
        ID3D12QueryHeap* heap = null;
        Guid iid = ID3D12QueryHeap.Guid;
        ThrowIfFailed(
            nativeDevice,
            nativeDevice.Native->CreateQueryHeap(
                &nativeDescription,
                &iid,
                (void**)&heap),
            NativeOperationType.Ordinary,
            "ID3D12Device::CreateQueryHeap");
        SetNativeName(heap, desc.Label ?? $"{desc.Type} Query Heap");
        D3D12QueryPool result;
        try
        {
            result = new D3D12QueryPool(
                nativeDevice,
                heap,
                resolvedDescription,
                resultInfo);
        }
        catch
        {
            _ = heap->Release();
            throw;
        }
        nativeDevice.RegisterChild(result);
        return result;
    }

    public void BeginQuery(CommandContext context, QueryPool pool, uint queryIndex)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12QueryPool native = RequireQueryPool(pool);
        native.RequireQueue(command);
        native.CheckIndex(queryIndex);
        if (native.Description.Type == SomeEngine.Graphics.QueryType.Timestamp)
            throw new InvalidOperationException("Timestamp queries are written, not begun.");
        command.PrepareCaptures(1);
        command.Capture(native);
        command.List->BeginQuery(native.Native, native.NativeType, queryIndex);
    }

    public void EndQuery(CommandContext context, QueryPool pool, uint queryIndex)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12QueryPool native = RequireQueryPool(pool);
        native.RequireQueue(command);
        native.CheckIndex(queryIndex);
        if (native.Description.Type == SomeEngine.Graphics.QueryType.Timestamp)
            throw new InvalidOperationException("Timestamp queries are written, not ended.");
        command.PrepareCaptures(1);
        command.Capture(native);
        command.List->EndQuery(native.Native, native.NativeType, queryIndex);
    }

    public void WriteTimestamp(CommandContext context, QueryPool pool, uint queryIndex)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12QueryPool native = RequireQueryPool(pool);
        native.RequireQueue(command);
        native.CheckIndex(queryIndex);
        if (native.Description.Type != SomeEngine.Graphics.QueryType.Timestamp)
            throw new InvalidOperationException("WriteTimestamp requires a Timestamp QueryPool.");
        command.PrepareCaptures(1);
        command.Capture(native);
        command.List->EndQuery(native.Native, NativeQueryType.Timestamp, queryIndex);
    }

    public void ResolveQueries(
        CommandContext context,
        QueryPool pool,
        uint firstQuery,
        uint queryCount,
        Buffer destination,
        in BufferRange destinationRange)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12QueryPool native = RequireQueryPool(pool);
        D3D12Buffer buffer = RequireBuffer(destination);
        native.RequireQueue(command);
        native.CheckRange(firstQuery, queryCount);
        if ((buffer.Info.Usages & BufferUsages.QueryResolve) == 0)
        {
            throw new ArgumentException(
                "The query destination Buffer requires QueryResolve usage.",
                nameof(destination));
        }
        BufferRange range = destinationRange.Resolve(buffer.Info.Size);
        ulong required = checked((ulong)queryCount * native.ResultInfo.ResultStride);
        if (range.Size < required || (range.Offset & 7) != 0)
            throw new ArgumentOutOfRangeException(nameof(destinationRange));
        command.PrepareCaptures(2, 0, 1);
        command.Capture(native);
        command.Capture(buffer);
        command.List->ResolveQueryData(
            native.Native,
            native.NativeType,
            firstQuery,
            queryCount,
            buffer.Native,
            range.Offset);
    }

    private static QueryHeapType ToQueryHeapType(
        SomeEngine.Graphics.QueryType type,
        QueueType queueType) => type switch
    {
        SomeEngine.Graphics.QueryType.Timestamp when queueType == QueueType.Copy =>
            QueryHeapType.CopyQueueTimestamp,
        SomeEngine.Graphics.QueryType.Timestamp => QueryHeapType.Timestamp,
        SomeEngine.Graphics.QueryType.Occlusion or
            SomeEngine.Graphics.QueryType.BinaryOcclusion => QueryHeapType.Occlusion,
        SomeEngine.Graphics.QueryType.PipelineStatistics => QueryHeapType.PipelineStatistics,
        SomeEngine.Graphics.QueryType.StreamOutputStatistics => QueryHeapType.SOStatistics,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static NativeQueryType ToNativeQueryType(
        SomeEngine.Graphics.QueryType type,
        uint streamIndex) => type switch
    {
        SomeEngine.Graphics.QueryType.Timestamp => NativeQueryType.Timestamp,
        SomeEngine.Graphics.QueryType.Occlusion => NativeQueryType.Occlusion,
        SomeEngine.Graphics.QueryType.BinaryOcclusion => NativeQueryType.BinaryOcclusion,
        SomeEngine.Graphics.QueryType.PipelineStatistics => NativeQueryType.PipelineStatistics,
        SomeEngine.Graphics.QueryType.StreamOutputStatistics => streamIndex switch
        {
            0 => NativeQueryType.SOStatisticsStream0,
            1 => NativeQueryType.SOStatisticsStream1,
            2 => NativeQueryType.SOStatisticsStream2,
            3 => NativeQueryType.SOStatisticsStream3,
            _ => throw new ArgumentOutOfRangeException(nameof(streamIndex)),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static QueryResultInfo GetQueryResultInfo(SomeEngine.Graphics.QueryType type) => type switch
    {
        SomeEngine.Graphics.QueryType.Timestamp or
            SomeEngine.Graphics.QueryType.Occlusion or
            SomeEngine.Graphics.QueryType.BinaryOcclusion => new QueryResultInfo(8, 8, 8),
        SomeEngine.Graphics.QueryType.PipelineStatistics => new QueryResultInfo(88, 8, 88),
        SomeEngine.Graphics.QueryType.StreamOutputStatistics => new QueryResultInfo(16, 8, 16),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private sealed class D3D12QueryPool : QueryPool
    {
        private readonly D3D12Device _device;
        private readonly NativeLease _native;

        internal D3D12QueryPool(
            D3D12Device device,
            ID3D12QueryHeap* native,
            in QueryPoolDesc description,
            in QueryResultInfo resultInfo)
            : base(device, description, resultInfo)
        {
            _device = device;
            _native = new NativeLease((IUnknown*)native, ownsReference: true);
            NativeType = ToNativeQueryType(description.Type, description.StreamIndex);
        }

        internal ID3D12QueryHeap* Native => (ID3D12QueryHeap*)_native.Pointer;
        internal NativeLease NativeLifetime => _native;
        internal NativeQueryType NativeType { get; }

        internal void CheckIndex(uint index)
        {
            if (index >= Description.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
        }

        internal void RequireQueue(D3D12CommandContext context)
        {
            if (Description.QueueType != context.QueueType)
            {
                throw new InvalidOperationException(
                    "The QueryPool belongs to another Queue family.");
            }
            if (Description.NodeIndex != context.NativeQueue.NodeIndex)
            {
                throw new InvalidOperationException(
                    "The QueryPool belongs to another linked-adapter node.");
            }
        }

        internal void CheckRange(uint first, uint count)
        {
            if (count == 0 || first >= Description.Count || count > Description.Count - first)
                throw new ArgumentOutOfRangeException(nameof(count));
        }

        internal override void Release(bool fromParent)
        {
            _native.Release();
            _device.UnregisterChild(this);
        }
    }

    private sealed partial class D3D12CommandContext
    {
        internal void Capture(D3D12QueryPool value) =>
            Recording.Capture(value.NativeLifetime);
    }

    private static partial class RequireD3D12
    {
        internal static D3D12QueryPool QueryPool(QueryPool value) =>
            value as D3D12QueryPool ??
            throw new ArgumentException(
                "The QueryPool was not created by the Direct3D 12 backend.",
                nameof(value));
    }
}
