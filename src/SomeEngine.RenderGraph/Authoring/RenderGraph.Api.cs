namespace SomeEngine.RenderGraph;

using System.Numerics;

public sealed partial class RenderGraph : IDisposable
{
    public bool IsValid => !_consumed && !_disposed;
    public BufferHandle CreateBuffer(in BufferDesc desc)
    {
        GraphDescriptionValidation.Validate(desc);
        return AddBuffer(desc);
    }

    /// <summary>Creates a graph-owned buffer in an explicit host/device memory domain.</summary>
    public BufferHandle CreateBuffer(in BufferDesc desc, MemoryType memoryType)
    {
        GraphDescriptionValidation.Validate(desc);
        if (!Enum.IsDefined(memoryType)) throw new ArgumentOutOfRangeException(nameof(memoryType));
        return AddBuffer(desc, memoryType);
    }

    /// <summary>Creates a transient graph-owned readback buffer.</summary>
    public BufferHandle CreateReadbackBuffer(ulong size, string? name = null) =>
        CreateBuffer(
            new BufferDesc(size, BufferUsages.CopyDestination, name),
            MemoryType.Readback);

    /// <summary>Creates a graph-owned upload buffer for an explicit host-to-GPU copy.</summary>
    public BufferHandle CreateUploadBuffer(ulong size, string? name = null) =>
        CreateBuffer(
            new BufferDesc(size, BufferUsages.CopySource, name),
            MemoryType.Upload);

    /// <summary>
    /// Creates an upload buffer and copies the exact bytes directly into invocation-owned
    /// canonical storage. No caller storage is retained.
    /// </summary>
    public BufferHandle CreateUploadBuffer(
        ReadOnlySpan<byte> initialData,
        string? name = null)
    {
        if (initialData.IsEmpty)
            throw new ArgumentException(
                "Upload initialization data cannot be empty.",
                nameof(initialData));
        BufferHandle buffer = CreateUploadBuffer(
            checked((ulong)initialData.Length),
            name);
        InitializeUploadBuffer(buffer, initialData);
        return buffer;
    }

    /// <summary>
    /// Copies the complete upload-buffer value into invocation-owned canonical storage. The
    /// supplied span is borrowed only for the duration of this call.
    /// </summary>
    public void InitializeUploadBuffer(
        BufferHandle buffer,
        ReadOnlySpan<byte> initialData) =>
        SetUploadData(buffer, initialData);

    public TextureHandle CreateTexture(in TextureDesc desc)
    {
        return AddTexture(new GraphTextureDescription(desc));
    }

    public BufferViewHandle CreateBufferView(
        BufferHandle buffer,
        BufferRange? range,
        GraphBindingType kind,
        Format? format = null,
        uint stride = 0,
        string? name = null) =>
        AddBufferView(buffer, range, kind, format, stride, name);

    /// <summary>
    /// Returns one canonical view identity for an exact buffer-view description within this graph
    /// invocation. The first diagnostic name is retained; no identity or physical view survives
    /// the invocation.
    /// </summary>
    public BufferViewHandle CreateSharedBufferView(
        BufferHandle buffer,
        BufferRange? range,
        GraphBindingType kind,
        Format? format = null,
        uint stride = 0,
        string? name = null) =>
        AddSharedBufferView(buffer, range, kind, format, stride, name);

    /// <summary>
    /// Declares an exact top-level acceleration structure for shader access. BLAS build/copy
    /// commands continue to use typed <see cref="AccelerationStructure"/> values directly.
    /// </summary>
    public AccelerationStructureHandle CreateAccelerationStructure(
        BufferHandle storage,
        BufferRange range,
        AccelerationStructureType type,
        string? name = null) =>
        AddAccelerationStructure(storage, range, type, name);

    public TextureViewHandle CreateTextureView(
        TextureHandle texture,
        TextureSubresourceRange? range,
        GraphTextureViewUsage usage,
        Format? format = null,
        string? name = null,
        TextureViewDimension? dimension = null) =>
        AddTextureView(texture, range, usage, format, name, dimension);

    /// <summary>
    /// Returns one canonical view identity for an exact texture-view description within this graph
    /// invocation. The first diagnostic name is retained; no identity or physical view survives
    /// the invocation.
    /// </summary>
    public TextureViewHandle CreateSharedTextureView(
        TextureHandle texture,
        TextureSubresourceRange? range,
        GraphTextureViewUsage usage,
        Format? format = null,
        string? name = null,
        TextureViewDimension? dimension = null) =>
        AddSharedTextureView(texture, range, usage, format, name, dimension);

    public BufferHandle Import(
        Buffer buffer,
        GraphResourceUsage initialState,
        GraphResourceUsage finalState,
        ReadOnlySpan<QueueCompletion> readiness = default,
        bool contentsAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!Enum.IsDefined(initialState)) throw new ArgumentOutOfRangeException(nameof(initialState));
        if (!Enum.IsDefined(finalState)) throw new ArgumentOutOfRangeException(nameof(finalState));
        ValidateReadiness(buffer.Device, readiness);
        return AddBufferImport(
            buffer,
            initialState,
            finalState,
            contentsAvailable,
            readiness);
    }

    public TextureHandle Import(
        Texture texture,
        GraphResourceUsage initialState,
        GraphResourceUsage finalState,
        ReadOnlySpan<QueueCompletion> readiness = default,
        bool contentsAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (!Enum.IsDefined(initialState)) throw new ArgumentOutOfRangeException(nameof(initialState));
        if (!Enum.IsDefined(finalState)) throw new ArgumentOutOfRangeException(nameof(finalState));
        ValidateReadiness(texture.Device, readiness);
        return AddTextureImport(
            texture,
            initialState,
            finalState,
            contentsAvailable,
            readiness);
    }

    /// <summary>
    /// Imports one acquired presentation image and transfers its unique Submit right to this graph
    /// invocation. The graph preserves the acquired initial state and returns the image to Present.
    /// </summary>
    public TextureHandle Import(in SwapchainImage image)
    {
        Texture texture = image.Texture;
        if (!ReferenceEquals(texture.Device, _device))
            throw new ArgumentException("The swapchain image belongs to another Device.", nameof(image));
        if (image.Status != SwapchainImageStatus.Acquired)
            throw new InvalidOperationException("Only an acquired swapchain image can enter a Render Graph.");
        if (image.InitialSync != PipelineSync.None || image.InitialAccess != ResourceAccess.NoAccess)
        {
            throw new InvalidOperationException(
                "The acquired swapchain image has non-canonical initial synchronization facts.");
        }

        GraphResourceUsage initial = image.InitialLayout switch
        {
            TextureLayout.Undefined => GraphResourceUsage.Undefined,
            TextureLayout.Present => GraphResourceUsage.Present,
            _ => throw new InvalidOperationException(
                $"The acquired swapchain image has unsupported initial layout {image.InitialLayout}."),
        };
        return AddTextureImport(
            texture,
            initial,
            GraphResourceUsage.Present,
            contentsAvailable: initial != GraphResourceUsage.Undefined,
            readiness: default,
            image);
    }

    /// <summary>
    /// Returns the graph-scoped identity of an already imported physical texture. This lets
    /// independently authored final passes extend the same canonical resource row without a
    /// duplicate physical import.
    /// </summary>
    public TextureHandle GetImported(Texture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        EnsureAuthoring();
        for (int ordinal = 0; ordinal < _textures.Count; ordinal++)
        {
            ref ResourceUnversionedData row = ref _textures[ordinal];
            if (row.IsImported && GetImportedTexture(row) == texture)
                return new TextureHandle(GraphSerial, ordinal);
        }
        throw new ArgumentException(
            "The physical texture has not been imported by this graph invocation.",
            nameof(texture));
    }

    /// <summary>
    /// Registers an externally owned sampler for this graph invocation. Pass parameters carry only
    /// the returned graph-scoped locator; the graph borrows the sampler owner until execution ends.
    /// </summary>
    public SamplerHandle Import(Sampler sampler) => AddSamplerImport(sampler);

    /// <summary>
    /// Registers an externally owned bindless table for this graph invocation. Pass parameters
    /// carry only the returned graph-scoped locator; the graph borrows the table owner.
    /// </summary>
    public DescriptorTableHandle Import(DescriptorTable table) => AddDescriptorTableImport(table);

    /// <summary>
    /// Registers an externally owned query pool for this graph invocation. Pass parameters carry
    /// only the returned graph-scoped locator; the graph borrows the owner until execution ends.
    /// </summary>
    public QueryPoolHandle Import(QueryPool pool) => AddQueryPoolImport(pool);

    private void BeginBuilderDeclarations(int pass)
    {
        if (_declarationPass >= 0)
            throw new InvalidOperationException("A render graph builder is already active.");

        ref PassData row = ref GetPass(pass);
        if (row.AccessCount != 0 ||
            row.ColorAttachmentCount != 0 ||
            row.DepthStencilAttachmentOrdinal >= 0 ||
            row.ShaderArgumentCount != 0 ||
            row.QueryAccessCount != 0 ||
            row.BindlessAccessCount != 0)
        {
            throw new InvalidOperationException(
                "A render graph builder must begin with an empty pass.");
        }

        _declarationAccessCursor = _declarationAccessEnd = _accesses.Count;
        row.AccessOffset = _declarationAccessCursor;
        _declarationShaderArgumentCursor =
            _declarationShaderArgumentEnd =
                _shaderArgumentTypes.Count;
        row.ShaderArgumentOffset = _declarationShaderArgumentCursor;
        _declarationColorCursor = _declarationColorEnd = _colorAttachments.Count;
        row.ColorAttachmentOffset = _declarationColorCursor;
        _declarationDepthStencilStart =
            _declarationDepthStencilCursor =
                _declarationDepthStencilEnd =
                    _depthStencilAttachments.Count;
        _declarationQueryCursor = _declarationQueryEnd = _passQueries.Count;
        row.QueryAccessOffset = _declarationQueryCursor;
        _declarationBindlessCursor =
            _declarationBindlessEnd =
                _bindlessAccessTypes.Count;
        row.BindlessAccessOffset = _declarationBindlessCursor;
        _declarationPass = pass;
        _dynamicDeclarations = true;
    }

    private void DeclareBufferAccess(
        int pass,
        BufferHandle buffer,
        GraphResourceUsage state,
        GraphAccess flags,
        BufferRange? requestedRange)
    {
        int resource = ResolveBuffer(buffer);
        ulong size = GetBufferDescription(resource).Size;
        BufferRange range = requestedRange ?? new BufferRange(0, size);
        BufferRange normalized = AccessNormalizer.NormalizeBuffer(
            size,
            range);
        _ = AppendBufferInput(
            pass,
            resource,
            -1,
            flags,
            state,
            normalized);
    }

    private void DeclareTextureAccess(
        int pass,
        TextureHandle textureHandle,
        GraphResourceUsage state,
        GraphAccess flags,
        TextureSubresourceRange? requestedRange)
    {
        int resource = ResolveTexture(textureHandle);
        if (state is GraphResourceUsage.RenderTarget or GraphResourceUsage.DepthRead or GraphResourceUsage.DepthWrite)
            throw new ArgumentException("Rendering attachments require SetRenderAttachment or SetRenderAttachmentDepth.", nameof(state));
        GraphTextureDescription texture = GetTextureDescription(resource);
        TextureSubresourceRange range = requestedRange ?? new TextureSubresourceRange(
            0,
            checked((uint)texture.MipLevels),
            0,
            checked((uint)texture.ArrayLayers),
            GraphFormat.AllowedAspects(texture.Format));
        TextureSubresourceRange normalized = AccessNormalizer.NormalizeTexture(
            texture,
            range);
        _ = AppendTextureInput(
            pass,
            resource,
            -1,
            flags,
            state,
            normalized);
    }

    private int DeclareBufferViewAccess(
        int pass,
        BufferViewHandle view,
        GraphAccess flags)
    {
        int accessOrdinal = AddBufferViewAccess(
            pass,
            view,
            flags);
        MarkViewMaterialization(ref GetPass(pass), accessOrdinal);
        return accessOrdinal;
    }

    private int DeclareTextureViewAccess(
        int pass,
        TextureViewHandle view,
        GraphAccess flags)
    {
        int accessOrdinal = AddTextureViewAccess(
            pass,
            view,
            flags);
        MarkViewMaterialization(ref GetPass(pass), accessOrdinal);
        return accessOrdinal;
    }

    private static void ValidateReadiness(
        Device resourceDevice,
        ReadOnlySpan<QueueCompletion> readiness)
    {
        foreach (ref readonly QueueCompletion fence in readiness)
        {
            if (fence == default || !ReferenceEquals(fence.Queue.Device, resourceDevice))
                throw new ArgumentException(
                    "Imported-resource readiness is invalid or belongs to another device.",
                    nameof(readiness));
        }
    }
}
