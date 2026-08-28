using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;
using GraphicsPipeline = SomeEngine.Graphics.Pipeline;
using Texture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Replays the exact same Cluster authoring code in two modes. The build mode creates persistent
/// RenderGraph structure through <see cref="RenderGraphEdit"/>. The frame mode returns those same
/// logical ids, binds caller-owned resources, writes upload buffers, and publishes only pass data.
/// No dynamic graph row is created in the frame mode.
/// </summary>
internal ref struct ClusterGraphAuthoring
{
    private readonly ClusterGraphPlan _plan;
    private readonly ClusterGraphCursor _cursor;
    private readonly bool _building;
    private RenderGraphEdit _edit;
    private RenderGraphFrame _frame;
    private readonly int _generation;
    private readonly SwapchainImage _presentationImage;
    private readonly Queue? _presentQueue;

    private ClusterGraphAuthoring(
        RenderGraphEdit edit,
        ClusterGraphPlan plan)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _cursor = new ClusterGraphCursor();
        _building = true;
        _edit = edit;
        _frame = default;
        _generation = -1;
        _presentationImage = default;
        _presentQueue = null;
    }

    private ClusterGraphAuthoring(
        RenderGraphFrame frame,
        ClusterGraphPlan plan,
        int generation,
        scoped in SwapchainImage presentationImage,
        Queue presentQueue)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _cursor = new ClusterGraphCursor();
        _building = false;
        _edit = default;
        _frame = frame;
        _generation = generation;
        _presentationImage = presentationImage;
        _presentQueue = presentQueue ?? throw new ArgumentNullException(nameof(presentQueue));
    }

    internal static ClusterGraphAuthoring Build(
        RenderGraphEdit edit,
        ClusterGraphPlan plan) =>
        new(edit, plan);

    internal static ClusterGraphAuthoring Bind(
        RenderGraphFrame frame,
        ClusterGraphPlan plan,
        int generation,
        scoped in SwapchainImage presentationImage,
        Queue presentQueue) =>
        new(frame, plan, generation, presentationImage, presentQueue);

    internal GraphBufferId CreateBuffer(
        in BufferDesc description,
        MemoryType memoryType = MemoryType.DeviceLocal)
    {
        if (_building)
        {
            GraphBufferId result = _edit.CreateBuffer(description, memoryType);
            _plan.CreatedBuffers.Add(result);
            return result;
        }
        return Next(_plan.CreatedBuffers, ref _cursor.CreatedBuffer, "created Buffer");
    }

    internal GraphTextureId CreateTexture(in TextureDesc description)
    {
        if (_building)
        {
            GraphTextureId result = _edit.CreateTexture(description);
            _plan.CreatedTextures.Add(result);
            return result;
        }
        return Next(_plan.CreatedTextures, ref _cursor.CreatedTexture, "created Texture");
    }

    internal GraphBufferId Import(
        Buffer buffer,
        scoped ReadOnlySpan<BufferBoundaryState> boundaryStates)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_building)
        {
            BufferInfo info = buffer.Info;
            BufferDesc description = new(
                info.Size,
                info.Usages,
                "Cluster external buffer");
            GraphBufferId id = _edit.DeclareExternalBuffer(description, info.MemoryType);
            _plan.ExternalBuffers.Add(new ClusterExternalBufferSlot(id, description));
            return id;
        }

        ClusterExternalBufferSlot slot = Next(
            _plan.ExternalBuffers,
            ref _cursor.ExternalBuffer,
            "external Buffer");
        ValidateBuffer(slot.Description, buffer);
        _frame.BindExternalBuffer(slot.Id, buffer, boundaryStates);
        return slot.Id;
    }

    internal GraphTextureId Import(
        Texture texture,
        scoped ReadOnlySpan<TextureBoundaryState> boundaryStates)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (_building)
        {
            TextureDesc description = Description(texture, "Cluster external texture");
            GraphTextureId id = _edit.DeclareExternalTexture(description);
            _plan.ExternalTextures.Add(new ClusterExternalTextureSlot(id, description));
            return id;
        }

        ClusterExternalTextureSlot slot = Next(
            _plan.ExternalTextures,
            ref _cursor.ExternalTexture,
            "external Texture");
        ValidateTexture(slot, texture);
        _frame.BindExternalTexture(slot.Id, texture, boundaryStates);
        return slot.Id;
    }

    internal GraphTextureId ImportPresentation(Texture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (_building)
        {
            TextureDesc description = Description(texture, "Cluster presentation target");
            GraphTextureId id = _edit.DeclareExternalTexture(description);
            _plan.ExternalTextures.Add(new ClusterExternalTextureSlot(id, description));
            _plan.Presentation = id;
            return id;
        }

        ClusterExternalTextureSlot slot = Next(
            _plan.ExternalTextures,
            ref _cursor.ExternalTexture,
            "presentation Texture");
        if (!ReferenceEquals(texture, _presentationImage.Texture))
        {
            throw new InvalidOperationException(
                "The prepared Cluster target does not match the acquired presentation image.");
        }
        ValidateTexture(slot, texture);
        _frame.BindExternalTexture(
            slot.Id,
            _presentationImage,
            _presentQueue ?? throw new InvalidOperationException(
                "The Cluster presentation Queue is unavailable."));
        return slot.Id;
    }

    internal GraphBufferId Upload(
        scoped ReadOnlySpan<byte> data,
        BufferUsages usages,
        string? label = null)
    {
        if (data.IsEmpty)
            throw new ArgumentException("Upload data cannot be empty.", nameof(data));
        if (_building)
        {
            BufferDesc description = new(
                checked((ulong)data.Length),
                usages,
                label);
            GraphBufferId id = _edit.DeclareExternalBuffer(description, MemoryType.Upload);
            _plan.Uploads.Add(new ClusterGraphUploadSlot(id, description));
            return id;
        }

        ClusterGraphUploadSlot slot = Next(
            _plan.Uploads,
            ref _cursor.Upload,
            "upload Buffer");
        slot.WriteAndBind(ref _frame, _generation, data);
        return slot.Id;
    }

    internal GraphBufferId Upload<T>(
        scoped ReadOnlySpan<T> data,
        BufferUsages usages,
        string? label = null)
        where T : unmanaged =>
        Upload(MemoryMarshal.AsBytes(data), usages, label);

    internal GraphBufferCbvId CreateBufferCbv(
        GraphBufferId buffer,
        BufferRange? range = null,
        string? label = null)
    {
        if (_building)
        {
            GraphBufferCbvId result = _edit.CreateBufferCbv(buffer, range, label);
            _plan.BufferCbvs.Add(result);
            return result;
        }
        return Next(_plan.BufferCbvs, ref _cursor.BufferCbv, "Buffer CBV");
    }

    internal GraphBufferSrvId CreateBufferSrv(
        GraphBufferId buffer,
        BufferRange? range = null,
        Format? format = null,
        uint structureStride = 0,
        string? label = null)
    {
        if (_building)
        {
            GraphBufferSrvId result = _edit.CreateBufferSrv(
                buffer,
                range,
                format,
                structureStride,
                label);
            _plan.BufferSrvs.Add(result);
            return result;
        }
        return Next(_plan.BufferSrvs, ref _cursor.BufferSrv, "Buffer SRV");
    }

    internal GraphBufferUavId CreateBufferUav(
        GraphBufferId buffer,
        BufferRange? range = null,
        Format? format = null,
        uint structureStride = 0,
        GraphBufferId counterBuffer = default,
        ulong counterOffset = 0,
        string? label = null)
    {
        if (_building)
        {
            GraphBufferUavId result = _edit.CreateBufferUav(
                buffer,
                range,
                format,
                structureStride,
                counterBuffer,
                counterOffset,
                label);
            _plan.BufferUavs.Add(result);
            return result;
        }
        return Next(_plan.BufferUavs, ref _cursor.BufferUav, "Buffer UAV");
    }

    internal GraphTextureSrvId CreateTextureSrv(
        GraphTextureId texture,
        TextureSubresourceRange? range = null,
        Format? format = null,
        TextureViewDimension? dimension = null,
        string? label = null)
    {
        if (_building)
        {
            GraphTextureSrvId result = _edit.CreateTextureSrv(
                texture,
                range,
                format,
                dimension,
                label);
            _plan.TextureSrvs.Add(result);
            return result;
        }
        return Next(_plan.TextureSrvs, ref _cursor.TextureSrv, "Texture SRV");
    }

    internal GraphTextureUavId CreateTextureUav(
        GraphTextureId texture,
        TextureSubresourceRange? range = null,
        Format? format = null,
        TextureViewDimension? dimension = null,
        string? label = null)
    {
        if (_building)
        {
            GraphTextureUavId result = _edit.CreateTextureUav(
                texture,
                range,
                format,
                dimension,
                label);
            _plan.TextureUavs.Add(result);
            return result;
        }
        return Next(_plan.TextureUavs, ref _cursor.TextureUav, "Texture UAV");
    }

    internal GraphColorAttachmentViewId CreateColorAttachmentView(
        GraphTextureId texture,
        TextureSubresourceRange? range = null,
        Format? format = null,
        TextureViewDimension? dimension = null,
        string? label = null)
    {
        if (_building)
        {
            GraphColorAttachmentViewId result = _edit.CreateColorAttachmentView(
                texture,
                range,
                format,
                dimension,
                label);
            _plan.ColorAttachments.Add(result);
            return result;
        }
        return Next(
            _plan.ColorAttachments,
            ref _cursor.ColorAttachment,
            "color-attachment view");
    }

    internal GraphDepthStencilViewId CreateDepthStencilView(
        GraphTextureId texture,
        TextureSubresourceRange? range = null,
        Format? format = null,
        TextureViewDimension? dimension = null,
        bool readOnlyDepth = false,
        bool readOnlyStencil = false,
        string? label = null)
    {
        if (_building)
        {
            GraphDepthStencilViewId result = _edit.CreateDepthStencilView(
                texture,
                range,
                format,
                dimension,
                readOnlyDepth,
                readOnlyStencil,
                label);
            _plan.DepthStencils.Add(result);
            return result;
        }
        return Next(_plan.DepthStencils, ref _cursor.DepthStencil, "depth-stencil view");
    }

    internal GraphPassId AddRasterPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        RasterFrameCallback<TState> callback) =>
        AddRasterPass(
            label,
            queue,
            state,
            options,
            null,
            VariableLayoutReflection.Null,
            default,
            declaration,
            callback);

    internal GraphPassId AddRasterPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        GraphicsPipeline? pipeline,
        VariableLayoutReflection parameterLayout,
        ReadOnlySpan<GraphParameterResourceBinding> parameterBindings,
        PassDeclaration<TState> declaration,
        RasterFrameCallback<TState> callback)
    {
        if (_building)
        {
            GraphPassId result = pipeline is null
                ? _edit.AddRasterFramePass(
                    label,
                    queue,
                    state,
                    options,
                    declaration,
                    callback)
                : _edit.AddRasterFramePass(
                    label,
                    queue,
                    state,
                    options,
                    pipeline,
                    parameterLayout,
                    parameterBindings,
                    declaration,
                    callback);
            _plan.Passes.Add(result);
            _plan.PassLabels.Add(label);
            return result;
        }
        GraphPassId pass = NextPass(label);
        _frame.SetPassData(pass, state);
        return pass;
    }

    internal GraphPassId AddComputePass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        ComputeFrameCallback<TState> callback) =>
        AddComputePass(
            label,
            queue,
            state,
            options,
            null,
            VariableLayoutReflection.Null,
            default,
            declaration,
            callback);

    internal GraphPassId AddComputePass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        GraphicsPipeline? pipeline,
        VariableLayoutReflection parameterLayout,
        ReadOnlySpan<GraphParameterResourceBinding> parameterBindings,
        PassDeclaration<TState> declaration,
        ComputeFrameCallback<TState> callback)
    {
        if (_building)
        {
            GraphPassId result = pipeline is null
                ? _edit.AddComputeFramePass(
                    label,
                    queue,
                    state,
                    options,
                    declaration,
                    callback)
                : _edit.AddComputeFramePass(
                    label,
                    queue,
                    state,
                    options,
                    pipeline,
                    parameterLayout,
                    parameterBindings,
                    declaration,
                    callback);
            _plan.Passes.Add(result);
            _plan.PassLabels.Add(label);
            return result;
        }
        GraphPassId pass = NextPass(label);
        _frame.SetPassData(pass, state);
        return pass;
    }

    internal GraphPassId AddCopyPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        CopyFrameCallback<TState> callback)
    {
        if (_building)
        {
            GraphPassId result = _edit.AddCopyFramePass(
                label,
                queue,
                state,
                options,
                declaration,
                callback);
            _plan.Passes.Add(result);
            _plan.PassLabels.Add(label);
            return result;
        }
        GraphPassId pass = NextPass(label);
        _frame.SetPassData(pass, state);
        return pass;
    }

    internal GraphPassId AddGeneralPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        GeneralFrameCallback<TState> callback)
    {
        if (_building)
        {
            GraphPassId result = _edit.AddGeneralFramePass(
                label,
                queue,
                state,
                options,
                declaration,
                callback);
            _plan.Passes.Add(result);
            _plan.PassLabels.Add(label);
            return result;
        }
        GraphPassId pass = NextPass(label);
        _frame.SetPassData(pass, state);
        return pass;
    }

    internal void Complete()
    {
        if (_building)
            return;
        RequireComplete(_cursor.CreatedBuffer, _plan.CreatedBuffers.Count, "created Buffers");
        RequireComplete(_cursor.CreatedTexture, _plan.CreatedTextures.Count, "created Textures");
        RequireComplete(_cursor.ExternalBuffer, _plan.ExternalBuffers.Count, "external Buffers");
        RequireComplete(_cursor.ExternalTexture, _plan.ExternalTextures.Count, "external Textures");
        RequireComplete(_cursor.Upload, _plan.Uploads.Count, "upload Buffers");
        RequireComplete(_cursor.BufferCbv, _plan.BufferCbvs.Count, "Buffer CBVs");
        RequireComplete(_cursor.BufferSrv, _plan.BufferSrvs.Count, "Buffer SRVs");
        RequireComplete(_cursor.BufferUav, _plan.BufferUavs.Count, "Buffer UAVs");
        RequireComplete(_cursor.TextureSrv, _plan.TextureSrvs.Count, "Texture SRVs");
        RequireComplete(_cursor.TextureUav, _plan.TextureUavs.Count, "Texture UAVs");
        RequireComplete(
            _cursor.ColorAttachment,
            _plan.ColorAttachments.Count,
            "color-attachment views");
        RequireComplete(_cursor.DepthStencil, _plan.DepthStencils.Count, "depth-stencil views");
        RequireComplete(_cursor.Pass, _plan.Passes.Count, "Passes");
    }

    private GraphPassId NextPass(string label)
    {
        int index = _cursor.Pass++;
        if ((uint)index >= (uint)_plan.Passes.Count)
            throw ReplayMismatch("Pass", index, _plan.Passes.Count);
        string expected = _plan.PassLabels[index];
        if (!string.Equals(expected, label, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The Cluster persistent graph replay expected Pass '{expected}' at {index}, " +
                $"but authored '{label}'. The renderer epoch key is incomplete.");
        }
        return _plan.Passes[index];
    }

    private static T Next<T>(List<T> values, ref int cursor, string kind)
    {
        int index = cursor++;
        if ((uint)index >= (uint)values.Count)
            throw ReplayMismatch(kind, index, values.Count);
        return values[index];
    }

    private static InvalidOperationException ReplayMismatch(
        string kind,
        int index,
        int count) =>
        new(
            $"The Cluster persistent graph replay requested {kind} {index}, but the graph " +
            $"contains {count}. The renderer epoch key is incomplete.");

    private static void RequireComplete(int consumed, int expected, string kind)
    {
        if (consumed != expected)
        {
            throw new InvalidOperationException(
                $"The Cluster persistent graph replay consumed {consumed} {kind}; " +
                $"the graph contains {expected}. The renderer epoch key is incomplete.");
        }
    }

    private static void ValidateBuffer(in BufferDesc expected, Buffer actual)
    {
        BufferInfo info = actual.Info;
        if (info.Size != expected.Size || info.Usages != expected.Usages)
        {
            throw new InvalidOperationException(
                "A Cluster external Buffer changed shape inside a renderer epoch.");
        }
    }

    private static void ValidateTexture(ClusterExternalTextureSlot expected, Texture actual)
    {
        TextureInfo info = actual.Info;
        if (info.Dimension != expected.Dimension ||
            info.Width != expected.Width ||
            info.Height != expected.Height ||
            info.Depth != expected.Depth ||
            info.MipLevelCount != expected.MipLevelCount ||
            info.ArrayLayerCount != expected.ArrayLayerCount ||
            info.SampleCount != expected.SampleCount ||
            info.Format != expected.Format ||
            info.Usages != expected.Usages)
        {
            throw new InvalidOperationException(
                "A Cluster external Texture changed shape inside a renderer epoch.");
        }
    }

    private static TextureDesc Description(Texture texture, string label)
    {
        TextureInfo info = texture.Info;
        return new TextureDesc(
            info.Dimension,
            info.Width,
            info.Height,
            info.Depth,
            info.MipLevelCount,
            info.ArrayLayerCount,
            info.SampleCount,
            info.Format,
            info.Usages,
            info.PermittedViewFormats,
            label);
    }
}

internal sealed class ClusterGraphPlan : IDisposable
{
    internal ClusterGraphPlan(
        IGraphicsBackend backend,
        Device device,
        int generationCount)
    {
        Backend = backend ?? throw new ArgumentNullException(nameof(backend));
        Device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationCount);
        GenerationCount = generationCount;
    }

    internal IGraphicsBackend Backend { get; }
    internal Device Device { get; }
    internal int GenerationCount { get; }
    internal GraphTextureId Presentation { get; set; }
    internal List<GraphBufferId> CreatedBuffers { get; } = [];
    internal List<GraphTextureId> CreatedTextures { get; } = [];
    internal List<ClusterExternalBufferSlot> ExternalBuffers { get; } = [];
    internal List<ClusterExternalTextureSlot> ExternalTextures { get; } = [];
    internal List<ClusterGraphUploadSlot> Uploads { get; } = [];
    internal List<GraphBufferCbvId> BufferCbvs { get; } = [];
    internal List<GraphBufferSrvId> BufferSrvs { get; } = [];
    internal List<GraphBufferUavId> BufferUavs { get; } = [];
    internal List<GraphTextureSrvId> TextureSrvs { get; } = [];
    internal List<GraphTextureUavId> TextureUavs { get; } = [];
    internal List<GraphColorAttachmentViewId> ColorAttachments { get; } = [];
    internal List<GraphDepthStencilViewId> DepthStencils { get; } = [];
    internal List<GraphPassId> Passes { get; } = [];
    internal List<string> PassLabels { get; } = [];

    internal void CreateUploadResources()
    {
        foreach (ClusterGraphUploadSlot upload in Uploads)
            upload.Create(Backend, Device, GenerationCount);
    }

    internal void Remove(ref RenderGraphEdit edit)
    {
        for (int index = Passes.Count - 1; index >= 0; index--)
            edit.Remove(Passes[index]);
        for (int index = DepthStencils.Count - 1; index >= 0; index--)
            edit.Remove(DepthStencils[index]);
        for (int index = ColorAttachments.Count - 1; index >= 0; index--)
            edit.Remove(ColorAttachments[index]);
        for (int index = TextureUavs.Count - 1; index >= 0; index--)
            edit.Remove(TextureUavs[index]);
        for (int index = TextureSrvs.Count - 1; index >= 0; index--)
            edit.Remove(TextureSrvs[index]);
        for (int index = BufferUavs.Count - 1; index >= 0; index--)
            edit.Remove(BufferUavs[index]);
        for (int index = BufferSrvs.Count - 1; index >= 0; index--)
            edit.Remove(BufferSrvs[index]);
        for (int index = BufferCbvs.Count - 1; index >= 0; index--)
            edit.Remove(BufferCbvs[index]);
        for (int index = CreatedTextures.Count - 1; index >= 0; index--)
            edit.Remove(CreatedTextures[index]);
        for (int index = ExternalTextures.Count - 1; index >= 0; index--)
            edit.Remove(ExternalTextures[index].Id);
        for (int index = CreatedBuffers.Count - 1; index >= 0; index--)
            edit.Remove(CreatedBuffers[index]);
        for (int index = Uploads.Count - 1; index >= 0; index--)
            edit.Remove(Uploads[index].Id);
        for (int index = ExternalBuffers.Count - 1; index >= 0; index--)
            edit.Remove(ExternalBuffers[index].Id);
    }

    public void Dispose()
    {
        List<Exception>? failures = null;
        for (int index = Uploads.Count - 1; index >= 0; index--)
        {
            try { Uploads[index].Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }
}

internal sealed class ClusterGraphUploadSlot : IDisposable
{
    private Buffer?[] _buffers = [];
    private IGraphicsBackend? _backend;

    internal ClusterGraphUploadSlot(GraphBufferId id, in BufferDesc description)
    {
        Id = id;
        Description = description;
    }

    internal GraphBufferId Id { get; }
    internal BufferDesc Description { get; }

    internal void Create(
        IGraphicsBackend backend,
        Device device,
        int generationCount)
    {
        if (_buffers.Length != 0)
            throw new InvalidOperationException("Cluster graph upload storage is already created.");
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        var created = new Buffer?[generationCount];
        try
        {
            for (int generation = 0; generation < generationCount; generation++)
            {
                created[generation] = backend.CreateBuffer(
                    device,
                    Description with
                    {
                        Label = Description.Label is null
                            ? $"Cluster graph upload {generation}"
                            : $"{Description.Label} {generation}",
                    },
                    MemoryType.Upload);
            }
            _buffers = created;
        }
        catch
        {
            for (int generation = created.Length - 1; generation >= 0; generation--)
                created[generation]?.Dispose();
            throw;
        }
    }

    internal void WriteAndBind(
        ref RenderGraphFrame graph,
        int generation,
        scoped ReadOnlySpan<byte> data)
    {
        if ((uint)generation >= (uint)_buffers.Length ||
            _buffers[generation] is not { } buffer)
        {
            throw new InvalidOperationException(
                "The Cluster graph has no admitted upload generation.");
        }
        if (checked((ulong)data.Length) != Description.Size)
        {
            throw new InvalidOperationException(
                $"Cluster graph upload '{Description.Label}' changed from {Description.Size} " +
                $"to {data.Length} bytes inside a renderer epoch.");
        }

        BufferRange range = new(0, Description.Size);
        IGraphicsBackend backend = _backend ?? throw new InvalidOperationException(
            "Cluster graph upload storage is not created.");
        using (MappedBuffer mapping = backend.Map(buffer, MapType.Write, range))
        {
            data.CopyTo(mapping.Bytes);
            mapping.Flush(range);
        }
        graph.BindExternalBuffer(
            Id,
            buffer,
            [new BufferBoundaryState(
                range,
                buffer.InitialSync,
                buffer.InitialAccess,
                ResourceContentState.Defined)]);
    }

    public void Dispose()
    {
        List<Exception>? failures = null;
        for (int generation = _buffers.Length - 1; generation >= 0; generation--)
        {
            try { _buffers[generation]?.Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
            _buffers[generation] = null;
        }
        _buffers = [];
        _backend = null;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }
}

internal readonly record struct ClusterExternalBufferSlot(
    GraphBufferId Id,
    BufferDesc Description);

internal sealed class ClusterExternalTextureSlot
{
    internal ClusterExternalTextureSlot(GraphTextureId id, in TextureDesc description)
    {
        Id = id;
        Dimension = description.Dimension;
        Width = description.Width;
        Height = description.Height;
        Depth = description.Depth;
        MipLevelCount = description.MipLevelCount;
        ArrayLayerCount = description.ArrayLayerCount;
        SampleCount = description.SampleCount;
        Format = description.Format;
        Usages = description.Usages;
    }

    internal GraphTextureId Id { get; }
    internal TextureDimension Dimension { get; }
    internal uint Width { get; }
    internal uint Height { get; }
    internal uint Depth { get; }
    internal uint MipLevelCount { get; }
    internal uint ArrayLayerCount { get; }
    internal uint SampleCount { get; }
    internal Format Format { get; }
    internal TextureUsages Usages { get; }
}

internal sealed class ClusterGraphCursor
{
    internal int CreatedBuffer;
    internal int CreatedTexture;
    internal int ExternalBuffer;
    internal int ExternalTexture;
    internal int Upload;
    internal int BufferCbv;
    internal int BufferSrv;
    internal int BufferUav;
    internal int TextureSrv;
    internal int TextureUav;
    internal int ColorAttachment;
    internal int DepthStencil;
    internal int Pass;
}
