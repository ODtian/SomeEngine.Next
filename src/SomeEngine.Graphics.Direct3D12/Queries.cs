using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using NativeQueryType = Silk.NET.Direct3D12.QueryType;

namespace SomeEngine.Graphics.Direct3D12;
internal sealed unsafe partial class D3D12Backend
{
    public QueryPool CreateQueryPool(Device device, in QueryPoolDesc desc)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        if (desc.Type == SomeEngine.Graphics.QueryType.StreamOutputStatistics)
        {
        }

        uint nodeIndex = nativeDevice.ResolveNodeIndex(desc.NodeIndex, nameof(desc));
        QueryPoolDesc resolvedDescription = desc with
        {
            NodeIndex = nodeIndex
        };
        QueryResultInfo resultInfo = GetQueryResultInfo(desc.Type);
        QueryHeapDesc nativeDescription = new(ToQueryHeapType(desc.Type, desc.QueueType), desc.Count, 1u << checked((int)nodeIndex));
        ID3D12QueryHeap* heap = null;
        Guid iid = ID3D12QueryHeap.Guid;
        ThrowIfFailed(
            nativeDevice,
            DeviceResourceTrace.CreateQueryHeap(nativeDevice.Native, &nativeDescription, &iid, (void**)&heap),
            NativeOperationType.Ordinary,
            "ID3D12Device::CreateQueryHeap"
        );
        SetNativeName(heap, desc.Label ?? $"{desc.Type} Query Heap");
        D3D12QueryPool result;
        try
        {
            result = new D3D12QueryPool(nativeDevice, heap, resolvedDescription, resultInfo);
        }
        catch
        {
            _ = ResourceTrace.Release(heap);
            throw;
        }

        nativeDevice.RegisterChild(result);
        return result;
    }

    public void ResetQueries(CommandContext context, QueryPool pool, uint firstQuery, uint queryCount)
    {
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12QueryPool native = RequireQueryPool(pool);
        native.CheckIndex(firstQuery);
    // D3D12 initializes a query on BeginQuery/EndQuery; no native reset is needed.
    }

    public void BeginQuery(CommandContext context, QueryPool pool, uint queryIndex)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12QueryPool native = RequireQueryPool(pool);
        native.CheckIndex(queryIndex);
        CommandExecutionTrace.BeginQuery(command, command.List, native.Native, native.NativeType, queryIndex);
    }

    public void EndQuery(CommandContext context, QueryPool pool, uint queryIndex)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12QueryPool native = RequireQueryPool(pool);
        native.CheckIndex(queryIndex);
        CommandExecutionTrace.EndQuery(command, command.List, native.Native, native.NativeType, queryIndex);
    }

    public void WriteTimestamp(CommandContext context, QueryPool pool, uint queryIndex)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12QueryPool native = RequireQueryPool(pool);
        native.CheckIndex(queryIndex);
        CommandExecutionTrace.EndQuery(command, command.List, native.Native, NativeQueryType.Timestamp, queryIndex);
    }

    public void ResolveQueries(
        CommandContext context,
        QueryPool pool,
        uint firstQuery,
        uint queryCount,
        Buffer destination,
        in BufferRange destinationRange
    )
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12QueryPool native = RequireQueryPool(pool);
        D3D12Buffer buffer = RequireBuffer(destination);
        native.CheckRange(firstQuery, queryCount);
        BufferRange range = destinationRange.Expand(buffer.Info.Size);
        ulong required = checked((ulong)queryCount * native.ResultInfo.ResultStride);
        CommandTransferTrace.ResolveQueryData(command, command.List, native.Native, native.NativeType, firstQuery, queryCount, buffer.Native, range.Offset);
    }

    private static QueryHeapType ToQueryHeapType(SomeEngine.Graphics.QueryType type, QueueType queueType) => type switch
    {
        SomeEngine.Graphics.QueryType.Timestamp when queueType == QueueType.Copy => QueryHeapType.CopyQueueTimestamp,
        SomeEngine.Graphics.QueryType.Timestamp => QueryHeapType.Timestamp,
        SomeEngine.Graphics.QueryType.Occlusion or SomeEngine.Graphics.QueryType.BinaryOcclusion => QueryHeapType.Occlusion,
        SomeEngine.Graphics.QueryType.PipelineStatistics => QueryHeapType.PipelineStatistics,
        SomeEngine.Graphics.QueryType.StreamOutputStatistics => QueryHeapType.SOStatistics,
        _ => default!,
    };
    private static NativeQueryType ToNativeQueryType(SomeEngine.Graphics.QueryType type, uint streamIndex) => type switch
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
            _ => default!,
        },
        _ => default!,
    };
    private static QueryResultInfo GetQueryResultInfo(SomeEngine.Graphics.QueryType type) => type switch
    {
        SomeEngine.Graphics.QueryType.Timestamp or SomeEngine.Graphics.QueryType.Occlusion or SomeEngine.Graphics.QueryType.BinaryOcclusion => new QueryResultInfo(8, 8, 8),
        SomeEngine.Graphics.QueryType.PipelineStatistics => new QueryResultInfo(88, 8, 88),
        SomeEngine.Graphics.QueryType.StreamOutputStatistics => new QueryResultInfo(16, 8, 16),
        _ => default!,
    };
    private sealed class D3D12QueryPool : QueryPool
    {
        private readonly D3D12Device _device;
        private readonly NativeResourceData _native;
        internal D3D12QueryPool(
            D3D12Device device,
            ID3D12QueryHeap* native,
            in QueryPoolDesc description,
            in QueryResultInfo resultInfo
        ) : base(device, description, resultInfo)
        {
            _device = device;
            _native = new NativeResourceData((IUnknown*)native, ownsReference: true);
            NativeType = ToNativeQueryType(description.Type, description.StreamIndex);
        }

        internal ID3D12QueryHeap* Native => (ID3D12QueryHeap*)_native.Pointer;
        internal NativeResourceData NativeData => _native;
        internal NativeQueryType NativeType { get; }

        internal void CheckIndex(uint index)
        {
        }

        internal void CheckRange(uint first, uint count)
        {
        }

        internal override void Release(bool fromParent)
        {
            _native.Release();
            _device.UnregisterChild(this);
        }
    }

    private sealed partial class D3D12CommandContext
    {
    }

    private static partial class RequireD3D12
    {
        internal static D3D12QueryPool QueryPool(QueryPool value) => (D3D12QueryPool)value;
    }
}
