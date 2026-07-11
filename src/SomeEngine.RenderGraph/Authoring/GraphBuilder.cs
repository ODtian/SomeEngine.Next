namespace SomeEngine.RenderGraph;

using System.Numerics;

public ref struct GraphBuilder
{
    private readonly RenderGraph _owner;
    private GraphRecording? _recording;

    internal GraphBuilder(RenderGraph owner, GraphRecording recording)
    {
        _owner = owner;
        _recording = recording;
    }

    public bool IsValid => _recording is not null;

    public BufferId CreateBuffer(in BufferDesc desc)
    {
        GraphRecording recording = GetRecording();
        desc.Validate();
        return recording.AddBuffer(desc, default);
    }

    public TextureId CreateTexture(in TextureDesc desc)
    {
        GraphRecording recording = GetRecording();
        desc.Validate();
        return recording.AddTexture(desc, default);
    }

    public BufferViewId CreateBufferView(
        BufferId buffer,
        BufferRange range,
        BindingKind kind,
        Format format = Format.Unknown,
        uint stride = 0,
        string? name = null) =>
        GetRecording().AddBufferView(buffer, range, kind, format, stride, name);

    public TextureViewId CreateTextureView(
        TextureId texture,
        TextureSubresourceRange range,
        TextureViewUsage usage,
        Format format = Format.Unknown,
        string? name = null,
        TextureViewDimension? dimension = null) =>
        GetRecording().AddTextureView(texture, range, usage, format, name, dimension);

    public BufferId ImportBuffer(
        BufferHandle buffer,
        BufferUse initialUse,
        BufferUse finalUse,
        bool contentsAvailable = true,
        GpuCompletionSet? readiness = null)
    {
        if (!buffer.IsValid) throw new ArgumentException("Imported buffer handle is invalid.", nameof(buffer));
        BufferMetadata metadata = _owner.GetBufferMetadata(buffer);
        ValidateReadiness(buffer.Domain, readiness);
        return GetRecording().AddBuffer(metadata.Description, new ImportedBuffer(
            buffer,
            metadata,
            initialUse,
            finalUse,
            contentsAvailable,
            readiness?.ToArray() ?? []));
    }

    public TextureId ImportTexture(
        TextureHandle texture,
        TextureUse initialUse,
        TextureUse finalUse,
        bool contentsAvailable = true,
        GpuCompletionSet? readiness = null)
    {
        if (!texture.IsValid) throw new ArgumentException("Imported texture handle is invalid.", nameof(texture));
        TextureMetadata metadata = _owner.GetTextureMetadata(texture);
        ValidateReadiness(texture.Domain, readiness);
        return GetRecording().AddTexture(metadata.Description, new ImportedTexture(
            texture,
            metadata,
            initialUse,
            finalUse,
            contentsAvailable,
            readiness?.ToArray() ?? []));
    }

    public PassBuilder AddPass(
        string name,
        QueueSelection allowedQueues,
        PassRecordingLane recordingLane = PassRecordingLane.Worker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(recordingLane)) throw new ArgumentOutOfRangeException(nameof(recordingLane));
        GraphRecording recording = GetRecording();
        int pass = recording.AddPass(name, allowedQueues, recordingLane);
        return new PassBuilder(recording, pass);
    }

    internal GraphRecording Consume(RenderGraph owner)
    {
        if (!ReferenceEquals(owner, _owner)) throw new ArgumentException("The builder belongs to a different RenderGraph.", nameof(owner));
        GraphRecording recording = GetRecording();
        _recording = null;
        return recording;
    }

    public void Dispose()
    {
        if (_recording is null) return;
        GraphRecording recording = _recording;
        _owner.Abandon(recording);
        _recording = null;
    }

    private GraphRecording GetRecording() => _recording ?? throw new InvalidOperationException("The graph builder has already been consumed or disposed.");

    private static void ValidateReadiness(DeviceDomain resourceDomain, GpuCompletionSet? readiness)
    {
        if (readiness is { Count: > 0 } && readiness.Domain != resourceDomain)
            throw new ArgumentException("Imported-resource readiness belongs to another device domain.", nameof(readiness));
    }
}

public ref struct PassBuilder
{
    private readonly GraphRecording _recording;
    private readonly int _pass;

    internal PassBuilder(GraphRecording recording, int pass)
    {
        _recording = recording;
        _pass = pass;
    }

    public BufferAccess Read(BufferId buffer, BufferUse use, BufferRange range = default) =>
        _recording.AddBufferAccess(_pass, buffer, ResourceEffect.Read, use, Normalize(range), PriorContents.Required, WriteCoverage.Partial);

    public BufferAccess Write(
        BufferId buffer,
        BufferUse use,
        BufferRange range = default,
        PriorContents priorContents = PriorContents.Discard,
        WriteCoverage coverage = WriteCoverage.Full) =>
        _recording.AddBufferAccess(_pass, buffer, ResourceEffect.Write, use, Normalize(range), priorContents, coverage);

    public BufferAccess ReadWrite(
        BufferId buffer,
        BufferUse use,
        BufferRange range = default,
        WriteCoverage coverage = WriteCoverage.Partial) =>
        _recording.AddBufferAccess(_pass, buffer, ResourceEffect.ReadWrite, use, Normalize(range), PriorContents.Required, coverage);

    public TextureAccess Read(TextureId texture, TextureUse use, TextureSubresourceRange range = default) =>
        _recording.AddTextureAccess(_pass, texture, ResourceEffect.Read, use, Normalize(range), PriorContents.Required, WriteCoverage.Partial);

    public TextureAccess Write(
        TextureId texture,
        TextureUse use,
        TextureSubresourceRange range = default,
        PriorContents priorContents = PriorContents.Discard,
        WriteCoverage coverage = WriteCoverage.Full) =>
        _recording.AddTextureAccess(_pass, texture, ResourceEffect.Write, use, Normalize(range), priorContents, coverage);

    public TextureAccess ReadWrite(
        TextureId texture,
        TextureUse use,
        TextureSubresourceRange range = default,
        WriteCoverage coverage = WriteCoverage.Partial) =>
        _recording.AddTextureAccess(_pass, texture, ResourceEffect.ReadWrite, use, Normalize(range), PriorContents.Required, coverage);

    public BufferViewAccess Read(BufferViewId view) =>
        _recording.AddBufferViewAccess(_pass, view, ResourceEffect.Read, PriorContents.Required, WriteCoverage.Partial);

    public BufferViewAccess Write(
        BufferViewId view,
        PriorContents priorContents = PriorContents.Discard,
        WriteCoverage coverage = WriteCoverage.Full) =>
        _recording.AddBufferViewAccess(_pass, view, ResourceEffect.Write, priorContents, coverage);

    public BufferViewAccess ReadWrite(BufferViewId view, WriteCoverage coverage = WriteCoverage.Partial) =>
        _recording.AddBufferViewAccess(_pass, view, ResourceEffect.ReadWrite, PriorContents.Required, coverage);

    public TextureViewAccess Read(TextureViewId view) =>
        _recording.AddTextureViewAccess(_pass, view, ResourceEffect.Read, PriorContents.Required, WriteCoverage.Partial);

    public TextureViewAccess Write(
        TextureViewId view,
        PriorContents priorContents = PriorContents.Discard,
        WriteCoverage coverage = WriteCoverage.Full) =>
        _recording.AddTextureViewAccess(_pass, view, ResourceEffect.Write, priorContents, coverage);

    public TextureViewAccess ReadWrite(TextureViewId view, WriteCoverage coverage = WriteCoverage.Partial) =>
        _recording.AddTextureViewAccess(_pass, view, ResourceEffect.ReadWrite, PriorContents.Required, coverage);

    public ColorAttachmentAccess ColorAttachment(
        int slot,
        TextureViewId view,
        LoadAction load,
        Vector4 clearColor = default) =>
        _recording.AddColorAttachment(_pass, slot, view, load, clearColor);

    public DepthStencilAttachmentAccess DepthStencilAttachment(
        TextureViewId view,
        DepthAttachmentOps? depth,
        StencilAttachmentOps? stencil = null) =>
        _recording.AddDepthStencilAttachment(_pass, view, depth, stencil);

    public ShaderBindingAccess MapShaderBinding(
        uint group,
        uint binding,
        BufferViewAccess access,
        uint element = 0) =>
        _recording.AddShaderBindingAccess(_pass, group, binding, element, access);

    public ShaderBindingAccess MapShaderBinding(
        uint group,
        uint binding,
        TextureViewAccess access,
        uint element = 0) =>
        _recording.AddShaderBindingAccess(_pass, group, binding, element, access);

    /// <summary>
    /// Marks a descriptor element as managed outside the render graph. Only bindings whose resolved
    /// shader effect is read-only may use this marker.
    /// </summary>
    public ShaderBindingAccess MapExternallyManagedShaderBinding(uint group, uint binding, uint element = 0) =>
        _recording.AddExternallyManagedShaderBinding(_pass, group, binding, element);

    public void UsesShader(in ShaderDesc shader) => _recording.AddShader(_pass, shader, ReadOnlySpan<ShaderBindingAccess>.Empty);

    public void UsesShader(in ShaderDesc shader, ReadOnlySpan<ShaderBindingAccess> bindings) =>
        _recording.AddShader(_pass, shader, bindings);

    /// <summary>Freezes one physical pipeline as an allowed execute-time choice for this pass.</summary>
    public void UsesPipeline(PipelineHandle pipeline) => _recording.AddPipeline(_pass, pipeline);

    public void Execute(PassExecution execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _recording.SetExecution(_pass, execute);
    }

    private static BufferRange Normalize(BufferRange range) => range == default ? BufferRange.Whole : range;
    private static TextureSubresourceRange Normalize(TextureSubresourceRange range) => range == default ? TextureSubresourceRange.WholeColor : range;
}

/// <summary>
/// Records one pass on an exclusive context. Worker-lane callbacks may run concurrently with one
/// another and with coordinator-lane callbacks; captured shared state must be thread-safe.
/// </summary>
public delegate void PassExecution(ICommandContext commands, in PassResources resources);
