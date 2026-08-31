namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private QueryPool CreateQueryPoolCore(RhiDevice device, in QueryPoolDesc desc)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        if (!Enum.IsDefined(desc.Type) || !Enum.IsDefined(desc.QueueType) || desc.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(desc));
        if (desc.Type != SomeEngine.Graphics.QueryType.Timestamp && desc.QueueType != QueueType.Graphics)
            throw new ArgumentException("Non-timestamp Vulkan queries require a Graphics Queue.", nameof(desc));
        if (desc.Type == SomeEngine.Graphics.QueryType.StreamOutputStatistics &&
            !nativeDevice.ExtendedFeatures.TransformFeedbackQueries)
            throw new NotSupportedException("The Vulkan Device exposes no transform-feedback queries.");
        if (desc.Type == SomeEngine.Graphics.QueryType.StreamOutputStatistics &&
            desc.StreamIndex >= nativeDevice.ExtendedFeatures.MaximumTransformFeedbackStreams)
            throw new ArgumentOutOfRangeException(nameof(desc));
        if (desc.Type != SomeEngine.Graphics.QueryType.StreamOutputStatistics &&
            desc.StreamIndex != 0)
            throw new ArgumentException("Only stream-output queries select a stream.", nameof(desc));
        QueryPoolCreateInfo createInfo = new()
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = ToNative(desc.Type),
            QueryCount = desc.Count,
            PipelineStatistics = desc.Type == SomeEngine.Graphics.QueryType.PipelineStatistics
                ? AllPipelineStatistics
                : 0,
        };
        VkQueryPool native = default;
        nativeDevice.ThrowIfDeviceCallFailed(
            Api.CreateQueryPool(nativeDevice.Native, &createInfo, null, &native),
            "vkCreateQueryPool");
        var pool = new VulkanQueryPool(
            nativeDevice,
            native,
            desc with { NodeIndex = 0 },
            GetQueryResultInfo(desc.Type));
        return RegisterChildOrDispose(nativeDevice, pool);
    }

    private void BeginQueryCore(CommandContext context, QueryPool pool, uint queryIndex)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanQueryPool native = RequireQueryPool(command, pool, queryIndex);
        if (native.Description.Type == SomeEngine.Graphics.QueryType.Timestamp)
            throw new InvalidOperationException("Timestamp queries are written, not begun.");
        command.Capture(native);
        Api.CmdResetQueryPool(command.NativeRecording, native.Native, queryIndex, 1);
        QueryControlFlags flags = native.Description.Type == SomeEngine.Graphics.QueryType.Occlusion
            ? QueryControlFlags.PreciseBit
            : QueryControlFlags.None;
        if (native.Description.Type == SomeEngine.Graphics.QueryType.StreamOutputStatistics)
        {
            ((VulkanDevice)command.Device).TransformFeedbackApi.CmdBeginQueryIndexed(
                command.NativeRecording,
                native.Native,
                queryIndex,
                flags,
                native.Description.StreamIndex);
        }
        else
        {
            Api.CmdBeginQuery(command.NativeRecording, native.Native, queryIndex, flags);
        }
    }

    private void EndQueryCore(CommandContext context, QueryPool pool, uint queryIndex)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanQueryPool native = RequireQueryPool(command, pool, queryIndex);
        if (native.Description.Type == SomeEngine.Graphics.QueryType.Timestamp)
            throw new InvalidOperationException("Timestamp queries are written, not ended.");
        command.Capture(native);
        if (native.Description.Type == SomeEngine.Graphics.QueryType.StreamOutputStatistics)
        {
            ((VulkanDevice)command.Device).TransformFeedbackApi.CmdEndQueryIndexed(
                command.NativeRecording,
                native.Native,
                queryIndex,
                native.Description.StreamIndex);
        }
        else
        {
            Api.CmdEndQuery(command.NativeRecording, native.Native, queryIndex);
        }
    }

    private void WriteTimestampCore(CommandContext context, QueryPool pool, uint queryIndex)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanQueryPool native = RequireQueryPool(command, pool, queryIndex);
        if (native.Description.Type != SomeEngine.Graphics.QueryType.Timestamp)
            throw new InvalidOperationException("WriteTimestamp requires a Timestamp QueryPool.");
        command.Capture(native);
        Api.CmdResetQueryPool(command.NativeRecording, native.Native, queryIndex, 1);
        Api.CmdWriteTimestamp2(
            command.NativeRecording,
            PipelineStageFlags2.AllCommandsBit,
            native.Native,
            queryIndex);
    }

    private void ResolveQueriesCore(
        CommandContext context,
        QueryPool pool,
        uint firstQuery,
        uint queryCount,
        RhiBuffer destination,
        in BufferRange destinationRange)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanQueryPool native = RequireQueryPool(command, pool, firstQuery);
        if (queryCount == 0 || queryCount > native.Description.Count - firstQuery)
            throw new ArgumentOutOfRangeException(nameof(queryCount));
        VulkanBuffer buffer = RequireBuffer((VulkanDevice)command.Device, destination, nameof(destination));
        if ((buffer.Info.Usages & BufferUsages.QueryResolve) == 0)
            throw new ArgumentException("The destination Buffer requires QueryResolve usage.", nameof(destination));
        BufferRange range = destinationRange.Resolve(buffer.Info.Size);
        ulong required = checked((ulong)queryCount * native.ResultInfo.ResultStride);
        if (range.Size < required || (range.Offset & 7) != 0)
            throw new ArgumentOutOfRangeException(nameof(destinationRange));
        command.Capture(native);
        command.Capture(buffer);
        Api.CmdCopyQueryPoolResults(
            command.NativeRecording,
            native.Native,
            firstQuery,
            queryCount,
            buffer.Native,
            range.Offset,
            native.ResultInfo.ResultStride,
            QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit);
    }

    private VulkanQueryPool RequireQueryPool(
        VulkanCommandContext command,
        QueryPool pool,
        uint index)
    {
        if (pool is not VulkanQueryPool native || !ReferenceEquals(native.Device, command.Device))
            throw new ArgumentException("The QueryPool belongs to a different Vulkan Device.", nameof(pool));
        native.ThrowIfDisposed();
        if (native.Description.QueueType != command.QueueType)
            throw new InvalidOperationException("The QueryPool belongs to another Queue family.");
        if (index >= native.Description.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return native;
    }

    private static Silk.NET.Vulkan.QueryType ToNative(SomeEngine.Graphics.QueryType type) => type switch
    {
        SomeEngine.Graphics.QueryType.Timestamp => Silk.NET.Vulkan.QueryType.Timestamp,
        SomeEngine.Graphics.QueryType.Occlusion or SomeEngine.Graphics.QueryType.BinaryOcclusion =>
            Silk.NET.Vulkan.QueryType.Occlusion,
        SomeEngine.Graphics.QueryType.PipelineStatistics => Silk.NET.Vulkan.QueryType.PipelineStatistics,
        SomeEngine.Graphics.QueryType.StreamOutputStatistics =>
            Silk.NET.Vulkan.QueryType.TransformFeedbackStreamExt,
        _ => throw new NotSupportedException($"Vulkan QueryType {type} is unsupported."),
    };

    private static QueryResultInfo GetQueryResultInfo(SomeEngine.Graphics.QueryType type) => type switch
    {
        SomeEngine.Graphics.QueryType.Timestamp or SomeEngine.Graphics.QueryType.Occlusion or
            SomeEngine.Graphics.QueryType.BinaryOcclusion => new QueryResultInfo(8, 8, 8),
        SomeEngine.Graphics.QueryType.PipelineStatistics => new QueryResultInfo(88, 8, 88),
        SomeEngine.Graphics.QueryType.StreamOutputStatistics =>
            new QueryResultInfo(16, 8, 16),
        _ => throw new NotSupportedException($"Vulkan QueryType {type} is unsupported."),
    };

    private const QueryPipelineStatisticFlags AllPipelineStatistics =
        QueryPipelineStatisticFlags.InputAssemblyVerticesBit |
        QueryPipelineStatisticFlags.InputAssemblyPrimitivesBit |
        QueryPipelineStatisticFlags.VertexShaderInvocationsBit |
        QueryPipelineStatisticFlags.GeometryShaderInvocationsBit |
        QueryPipelineStatisticFlags.GeometryShaderPrimitivesBit |
        QueryPipelineStatisticFlags.ClippingInvocationsBit |
        QueryPipelineStatisticFlags.ClippingPrimitivesBit |
        QueryPipelineStatisticFlags.FragmentShaderInvocationsBit |
        QueryPipelineStatisticFlags.TessellationControlShaderPatchesBit |
        QueryPipelineStatisticFlags.TessellationEvaluationShaderInvocationsBit |
        QueryPipelineStatisticFlags.ComputeShaderInvocationsBit;

    private sealed class VulkanQueryPool : QueryPool, IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanLifetime _lifetime;
        private VkQueryPool _native;

        internal VulkanQueryPool(
            VulkanDevice device,
            VkQueryPool native,
            in QueryPoolDesc description,
            in QueryResultInfo resultInfo)
            : base(device, description, resultInfo)
        {
            _device = device;
            _native = native;
            _lifetime = new VulkanLifetime(DestroyNative);
        }

        internal VkQueryPool Native => _native;
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        internal override void Release(bool fromParent) { _device.UnregisterChild(this); _lifetime.Release(); }
        private void DestroyNative() { if (_native.Handle != 0) _device.Backend.Api.DestroyQueryPool(_device.Native, _native, null); _native = default; }
    }
}
