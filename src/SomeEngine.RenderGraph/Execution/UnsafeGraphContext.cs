using System.Buffers;
using System.Numerics;
using System.Text;

namespace SomeEngine.RenderGraph;

/// <summary>
/// Pass-scoped command facade. Every resource-returning or resource-consuming operation verifies
/// that the pass declared the matching graph access before forwarding the exact command to the RHI
/// receiver selected for this graph invocation.
/// </summary>
public ref struct UnsafeGraphContext
{
    private readonly IGraphicsBackend _backend;
    private readonly CommandContext _commands;
    private readonly RenderGraph _graph;
    private readonly int _pass;

    internal UnsafeGraphContext(
        IGraphicsBackend backend,
        CommandContext commands,
        RenderGraph graph,
        int pass)
    {
        _backend = backend;
        _commands = commands;
        _graph = graph;
        _pass = pass;
    }

    public void CopyBufferRegion(
        in BufferHandle source,
        ulong sourceOffset,
        in BufferHandle destination,
        ulong destinationOffset,
        ulong size)
    {
        if (size == 0) throw new ArgumentOutOfRangeException(nameof(size));
        Buffer sourceBuffer = RequireBuffer(
            source,
            GraphResourceUsage.CopySource,
            GraphAccess.Read,
            new BufferRange(sourceOffset, size));
        Buffer destinationBuffer = RequireBuffer(
            destination,
            GraphResourceUsage.CopyDestination,
            GraphAccess.Write,
            new BufferRange(destinationOffset, size));
        _backend.CopyBuffer(
            _commands,
            new BufferCopy(sourceBuffer, sourceOffset, destinationBuffer, destinationOffset, size));
    }

    public void FillBuffer(
        in BufferHandle buffer,
        ulong offset,
        ulong size,
        uint pattern = 0)
    {
        if (size == 0) throw new ArgumentOutOfRangeException(nameof(size));
        Buffer target = RequireBuffer(
            buffer,
            GraphResourceUsage.CopyDestination,
            GraphAccess.Write,
            new BufferRange(offset, size));
        _backend.ClearBuffer(_commands, target, new BufferRange(offset, size), pattern);
    }

    public void FillBuffer(in BufferHandle buffer, uint pattern = 0)
    {
        Buffer target = RequireBuffer(
            buffer,
            GraphResourceUsage.CopyDestination,
            GraphAccess.Write);
        _backend.ClearBuffer(_commands, target, BufferRange.Whole, pattern);
    }

    public void ClearColorTexture(
        in TextureHandle texture,
        in TextureSubresourceRange range,
        in Vector4 color)
    {
        Texture target = RequireTexture(
            texture,
            GraphResourceUsage.CopyDestination,
            GraphAccess.Write,
            range);
        _backend.ClearTexture(_commands, target, range, color);
    }

    public void ClearDepthStencilTexture(
        in TextureHandle texture,
        in TextureSubresourceRange range,
        float depth = 1,
        byte stencil = 0)
    {
        Texture target = RequireTexture(
            texture,
            GraphResourceUsage.CopyDestination,
            GraphAccess.Write,
            range);
        _backend.ClearDepthStencil(_commands, target, range, depth, stencil);
    }

    public void CopyTexture(
        in TextureHandle source,
        in TextureHandle destination,
        in GraphTextureCopy copy)
    {
        Texture sourceTexture = RequireTexture(
            source,
            GraphResourceUsage.CopySource,
            GraphAccess.Read,
            Range(copy.Source));
        Texture destinationTexture = RequireTexture(
            destination,
            GraphResourceUsage.CopyDestination,
            GraphAccess.Write,
            Range(copy.Destination));
        _backend.CopyTexture(
            _commands,
            new TextureCopy(
                sourceTexture,
                copy.Source.MipLevel,
                copy.Source.ArrayLayer,
                copy.Source.Aspect,
                copy.Source.X,
                copy.Source.Y,
                copy.Source.Z,
                destinationTexture,
                copy.Destination.MipLevel,
                copy.Destination.ArrayLayer,
                copy.Destination.Aspect,
                copy.Destination.X,
                copy.Destination.Y,
                copy.Destination.Z,
                copy.Source.Width,
                copy.Source.Height,
                copy.Source.Depth));
    }

    public void CopyBufferToTexture(
        in BufferHandle source,
        in TextureHandle destination,
        in GraphBufferTextureCopy copy)
    {
        ulong byteCount = RequiredBufferBytes(copy);
        Buffer sourceBuffer = RequireBuffer(
            source,
            GraphResourceUsage.CopySource,
            GraphAccess.Read,
            new BufferRange(copy.BufferOffset, byteCount));
        Texture destinationTexture = RequireTexture(
            destination,
            GraphResourceUsage.CopyDestination,
            GraphAccess.Write,
            Range(copy.Texture));
        _backend.CopyBufferToTexture(
            _commands,
            MaterializeBufferTextureCopy(sourceBuffer, destinationTexture, copy));
    }

    public void CopyTextureToBuffer(
        in TextureHandle source,
        in BufferHandle destination,
        in GraphBufferTextureCopy copy)
    {
        Texture sourceTexture = RequireTexture(
            source,
            GraphResourceUsage.CopySource,
            GraphAccess.Read,
            Range(copy.Texture));
        ulong byteCount = RequiredBufferBytes(copy);
        Buffer destinationBuffer = RequireBuffer(
            destination,
            GraphResourceUsage.CopyDestination,
            GraphAccess.Write,
            new BufferRange(copy.BufferOffset, byteCount));
        _backend.CopyTextureToBuffer(
            _commands,
            MaterializeBufferTextureCopy(destinationBuffer, sourceTexture, copy));
    }

    public void ResolveTexture(
        in TextureHandle source,
        uint sourceMipLevel,
        uint sourceArrayLayer,
        in TextureHandle destination,
        uint destinationMipLevel,
        uint destinationArrayLayer,
        Format format,
        ResolveType type = ResolveType.Average)
    {
        Texture sourceTexture = RequireTexture(
            source,
            GraphResourceUsage.ResolveSource,
            GraphAccess.Read);
        Texture destinationTexture = RequireTexture(
            destination,
            GraphResourceUsage.ResolveDestination,
            GraphAccess.Write);
        _backend.ResolveTexture(
            _commands,
            new TextureResolve(
                sourceTexture,
                sourceMipLevel,
                sourceArrayLayer,
                destinationTexture,
                destinationMipLevel,
                destinationArrayLayer,
                format,
                type));
    }

    public void SetViewport(in Viewport viewport)
    {
        ReadOnlySpan<Viewport> values = new[] { viewport };
        _backend.SetViewports(_commands, values);
    }

    public void SetScissor(in ScissorRect rect)
    {
        ReadOnlySpan<ScissorRect> values = new[] { rect };
        _backend.SetScissors(_commands, values);
    }

    public void SetStencilReference(uint reference) =>
        _backend.SetStencilReference(_commands, reference);

    public void BindVertexBuffers(
        uint slot,
        in BufferHandle buffer,
        ulong offset,
        uint stride,
        ulong size = ulong.MaxValue)
    {
        Buffer resource = RequireBuffer(
            buffer,
            GraphResourceUsage.VertexOrConstantBuffer,
            GraphAccess.Read);
        ulong resolvedSize = size == ulong.MaxValue
            ? checked(resource.Info.Size - offset)
            : size;
        ReadOnlySpan<VertexBufferBinding> bindings =
            new[] { new VertexBufferBinding(resource, offset, stride, resolvedSize) };
        _backend.SetVertexBuffers(_commands, slot, bindings);
    }

    public void BindIndexBuffer(
        in BufferHandle buffer,
        ulong offset,
        IndexType type,
        ulong size = ulong.MaxValue)
    {
        Buffer resource = RequireBuffer(
            buffer,
            GraphResourceUsage.IndexBuffer,
            GraphAccess.Read);
        ulong resolvedSize = size == ulong.MaxValue
            ? checked(resource.Info.Size - offset)
            : size;
        _backend.SetIndexBuffer(
            _commands,
            new IndexBufferBinding(resource, offset, resolvedSize, type));
    }

    public void Draw(
        uint vertexCount,
        uint instanceCount = 1,
        uint firstVertex = 0,
        uint firstInstance = 0) =>
        _backend.Draw(
            _commands,
            new DrawArguments(vertexCount, instanceCount, firstVertex, firstInstance));

    public void DrawIndexed(
        uint indexCount,
        uint instanceCount = 1,
        uint firstIndex = 0,
        int vertexOffset = 0,
        uint firstInstance = 0) =>
        _backend.DrawIndexed(
            _commands,
            new DrawIndexedArguments(
                indexCount,
                instanceCount,
                firstIndex,
                vertexOffset,
                firstInstance));

    public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) =>
        _backend.Dispatch(
            _commands,
            new DispatchArguments(groupCountX, groupCountY, groupCountZ));

    public void DispatchMesh(
        uint groupCountX,
        uint groupCountY = 1,
        uint groupCountZ = 1) =>
        _backend.DispatchMesh(
            _commands,
            new DispatchArguments(groupCountX, groupCountY, groupCountZ));

    public void ExecuteIndirect(
        IndirectCommandLayout layout,
        in BufferHandle arguments,
        ulong argumentOffset,
        ulong argumentSize,
        uint maximumCommandCount,
        BufferHandle? count = null,
        ulong countOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Buffer argumentBuffer = RequireBuffer(
            arguments,
            GraphResourceUsage.IndirectArgument,
            GraphAccess.Read,
            new BufferRange(argumentOffset, argumentSize));
        BufferRegion? countRegion = count.HasValue
            ? new BufferRegion(
                RequireBuffer(
                    count.Value,
                    GraphResourceUsage.IndirectArgument,
                    GraphAccess.Read,
                    new BufferRange(countOffset, sizeof(uint))),
                new BufferRange(countOffset, sizeof(uint)))
            : null;
        _backend.ExecuteIndirect(
            _commands,
            layout,
            new BufferRegion(argumentBuffer, new BufferRange(argumentOffset, argumentSize)),
            maximumCommandCount,
            countRegion);
    }

    public void SetShadingRate(
        ShadingRate rate,
        ShadingRateCombiner primitiveCombiner = ShadingRateCombiner.Passthrough,
        ShadingRateCombiner imageCombiner = ShadingRateCombiner.Passthrough) =>
        _backend.SetShadingRate(_commands, rate, primitiveCombiner, imageCombiner);

    public void SetShadingRateImage() => _backend.SetShadingRateImage(_commands, null);

    public void SetShadingRateImage(in TextureHandle texture) =>
        _backend.SetShadingRateImage(
            _commands,
            RequireTexture(
                texture,
                GraphResourceUsage.ShadingRateSource,
                GraphAccess.Read));

    public Buffer GetBuffer(in BufferHandle buffer) => RequireDeclaredBuffer(buffer);

    public BufferCbv GetConstantBufferView(
        in BufferViewHandle view,
        GraphAccess flags = GraphAccess.Read) => RequireBufferView(view, flags).ConstantBuffer;

    public BufferSrv GetReadOnlyBufferView(
        in BufferViewHandle view,
        GraphAccess flags = GraphAccess.Read) => RequireBufferView(view, flags).ReadOnlyBuffer;

    public BufferUav GetStorageBufferView(
        in BufferViewHandle view,
        GraphAccess flags = GraphAccess.ReadWrite) => RequireBufferView(view, flags).StorageBuffer;

    public TextureSrv GetSampledTextureView(
        in TextureViewHandle view,
        GraphAccess flags = GraphAccess.Read) =>
        RequireTextureView(view, flags).ShaderResource ??
        throw new InvalidOperationException("The graph view has no texture SRV.");

    public TextureUav GetStorageTextureView(
        in TextureViewHandle view,
        GraphAccess flags = GraphAccess.ReadWrite) =>
        RequireTextureView(view, flags).Storage ??
        throw new InvalidOperationException("The graph view has no texture UAV.");

    public AccelerationStructure GetAccelerationStructure(
        in AccelerationStructureHandle accelerationStructure) =>
        RequireAccelerationStructure(accelerationStructure);

    public uint GetDescriptorIndex(in DescriptorTableHandle table, uint slot) =>
        _backend.GetDescriptorIndex(_graph.GetDescriptorTable(table), slot);

    public void BuildAccelerationStructure(in AccelerationStructureBuildDesc description) =>
        _backend.BuildAccelerationStructure(_commands, description);

    public void BeginQuery(in QueryPoolHandle queryPool, uint queryIndex) =>
        _backend.BeginQuery(_commands, RequireQueryPool(queryPool), queryIndex);

    public void EndQuery(in QueryPoolHandle queryPool, uint queryIndex) =>
        _backend.EndQuery(_commands, RequireQueryPool(queryPool), queryIndex);

    public void WriteTimestamp(in QueryPoolHandle queryPool, uint queryIndex) =>
        _backend.WriteTimestamp(_commands, RequireQueryPool(queryPool), queryIndex);

    public void ResolveQueries(
        in QueryPoolHandle queryPool,
        uint firstQuery,
        uint queryCount,
        in BufferHandle destination,
        ulong destinationOffset)
    {
        QueryPool pool = RequireQueryPool(queryPool);
        ulong size = checked((ulong)pool.ResultInfo.ResultStride * queryCount);
        Buffer buffer = RequireBuffer(
            destination,
            GraphResourceUsage.CopyDestination,
            GraphAccess.Write,
            new BufferRange(destinationOffset, size));
        _backend.ResolveQueries(
            _commands,
            pool,
            firstQuery,
            queryCount,
            buffer,
            new BufferRange(destinationOffset, size));
    }

    public void BeginEvent(string name) => EmitLabel(name, begin: true, marker: false);
    public void EndEvent() => _backend.EndEvent(_commands);
    public void SetMarker(string name) => EmitLabel(name, begin: false, marker: true);

    private void EmitLabel(string name, bool begin, bool marker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        int byteCount = Encoding.UTF8.GetByteCount(name);
        byte[]? rented = null;
        Span<byte> utf8 = byteCount <= 512
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
        try
        {
            Encoding.UTF8.GetBytes(name, utf8);
            if (marker) _backend.SetMarker(_commands, utf8);
            else if (begin) _backend.BeginEvent(_commands, utf8);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private Buffer RequireDeclaredBuffer(in BufferHandle handle)
    {
        ValidateHandle(handle.Graph, handle.Ordinal, _graph.Buffers.Length, nameof(handle));
        foreach (ref readonly PassInputData access in _graph.GetPassAccesses(_graph.Passes[_pass]))
            if (access.IsBuffer && access.Buffer == handle.Ordinal)
                return _graph.MaterializedBuffers[handle.Ordinal];
        throw new InvalidOperationException("The pass does not declare this buffer.");
    }

    private Buffer RequireBuffer(
        in BufferHandle handle,
        GraphResourceUsage usage,
        GraphAccess flags,
        BufferRange? range = null)
    {
        ValidateHandle(handle.Graph, handle.Ordinal, _graph.Buffers.Length, nameof(handle));
        GraphAccess required = flags & GraphAccess.ReadWrite;
        foreach (ref readonly PassInputData access in _graph.GetPassAccesses(_graph.Passes[_pass]))
        {
            if (!access.IsBuffer || access.Buffer != handle.Ordinal ||
                access.State != usage || (access.Flags & required) != required)
                continue;
            if (!range.HasValue || Contains(access.BufferRange, range.Value))
                return _graph.MaterializedBuffers[handle.Ordinal];
        }
        throw new InvalidOperationException(
            "The pass does not declare a compatible buffer access covering this command.");
    }

    private Texture RequireTexture(
        in TextureHandle handle,
        GraphResourceUsage usage,
        GraphAccess flags,
        TextureSubresourceRange? range = null)
    {
        ValidateHandle(handle.Graph, handle.Ordinal, _graph.Textures.Length, nameof(handle));
        GraphAccess required = flags & GraphAccess.ReadWrite;
        foreach (ref readonly PassInputData access in _graph.GetPassAccesses(_graph.Passes[_pass]))
        {
            if (access.IsBuffer || access.Texture != handle.Ordinal ||
                access.State != usage || (access.Flags & required) != required)
                continue;
            if (!range.HasValue || Contains(access.TextureRange, range.Value))
                return _graph.MaterializedTextures[handle.Ordinal];
        }
        throw new InvalidOperationException(
            "The pass does not declare a compatible texture access covering this command.");
    }

    private MaterializedBufferView RequireBufferView(
        in BufferViewHandle view,
        GraphAccess flags)
    {
        PassInputData key = _graph.CreateAccessKey(view, flags);
        RequireAccess(key);
        return _graph.MaterializedBufferViews[view.Ordinal] ??
            throw new InvalidOperationException("The declared buffer view was not materialized.");
    }

    private MaterializedTextureView RequireTextureView(
        in TextureViewHandle view,
        GraphAccess flags)
    {
        PassInputData key = _graph.CreateAccessKey(view, flags);
        RequireAccess(key);
        return _graph.MaterializedTextureViews[view.Ordinal] ??
            throw new InvalidOperationException("The declared texture view was not materialized.");
    }

    private AccelerationStructure RequireAccelerationStructure(
        in AccelerationStructureHandle accelerationStructure)
    {
        PassInputData key = _graph.CreateAccelerationStructureAccessKey(accelerationStructure);
        RequireAccess(key);
        return _graph.AccelerationStructures[accelerationStructure.Ordinal];
    }

    private QueryPool RequireQueryPool(in QueryPoolHandle queryPool)
    {
        QueryPool pool = _graph.GetQueryPool(queryPool);
        if (!_graph.ContainsPassQuery(_pass, queryPool.Ordinal))
            throw new InvalidOperationException("The pass does not declare this query pool.");
        return pool;
    }

    private void RequireAccess(in PassInputData key)
    {
        if (!_graph.ContainsPassAccess(_pass, key))
            throw new InvalidOperationException("The pass does not declare this exact access.");
    }

    private static void ValidateHandle(
        long graph,
        int ordinal,
        int count,
        string parameterName)
    {
        if (graph == 0 || ordinal < 0 || ordinal >= count)
            throw new ArgumentException("The resource locator is invalid.", parameterName);
    }

    private static bool Contains(in BufferRange declared, in BufferRange requested) =>
        declared.Offset <= requested.Offset &&
        requested.Size <= declared.Size - (requested.Offset - declared.Offset);

    private static bool Contains(
        in TextureSubresourceRange declared,
        in TextureSubresourceRange requested) =>
        (declared.Aspects & requested.Aspects) == requested.Aspects &&
        declared.FirstMipLevel <= requested.FirstMipLevel &&
        requested.FirstMipLevel + requested.MipLevelCount <=
            declared.FirstMipLevel + declared.MipLevelCount &&
        declared.FirstArrayLayer <= requested.FirstArrayLayer &&
        requested.FirstArrayLayer + requested.ArrayLayerCount <=
            declared.FirstArrayLayer + declared.ArrayLayerCount;

    private static TextureSubresourceRange Range(in GraphTextureRegion region) => new(
        region.MipLevel,
        1,
        region.ArrayLayer,
        1,
        region.Aspect);

    private static ulong RequiredBufferBytes(in GraphBufferTextureCopy copy)
    {
        if (copy.BufferRowPitch == 0 || copy.BufferImageHeight == 0 ||
            copy.Texture.Width == 0 || copy.Texture.Height == 0 || copy.Texture.Depth == 0)
            throw new ArgumentOutOfRangeException(nameof(copy));
        return checked(
            (ulong)copy.BufferRowPitch *
            copy.BufferImageHeight *
            copy.Texture.Depth);
    }

    private static BufferTextureCopy MaterializeBufferTextureCopy(
        Buffer buffer,
        Texture texture,
        in GraphBufferTextureCopy copy) => new(
        buffer,
        copy.BufferOffset,
        copy.BufferRowPitch,
        copy.BufferImageHeight,
        texture,
        copy.Texture.MipLevel,
        copy.Texture.ArrayLayer,
        copy.Texture.Aspect,
        copy.Texture.X,
        copy.Texture.Y,
        copy.Texture.Z,
        copy.Texture.Width,
        copy.Texture.Height,
        copy.Texture.Depth);
}
