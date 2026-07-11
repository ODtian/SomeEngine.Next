namespace SomeEngine.RenderGraph;

using System.Numerics;

internal enum ResourceNodeKind : byte
{
    Buffer,
    Texture,
}

internal sealed class FrozenGraph
{
    public FrozenGraph(
        GraphToken token,
        FrozenResource[] resources,
        FrozenBufferView[] bufferViews,
        FrozenTextureView[] textureViews,
        FrozenPass[] passes,
        GraphCanonicalData canonical)
    {
        Token = token;
        Resources = resources;
        BufferViews = bufferViews;
        TextureViews = textureViews;
        Passes = passes;
        Canonical = canonical;
    }

    public GraphToken Token { get; }
    public FrozenResource[] Resources { get; }
    public FrozenBufferView[] BufferViews { get; }
    public FrozenTextureView[] TextureViews { get; }
    public FrozenPass[] Passes { get; }
    public GraphCanonicalData Canonical { get; }

    public FrozenGraph DetachForCompilation()
    {
        FrozenResource[] resources = new FrozenResource[Resources.Length];
        for (int index = 0; index < resources.Length; index++)
        {
            FrozenResource resource = Resources[index];
            resources[index] = resource with
            {
                ImportedBuffer = resource.ImportedBuffer with
                {
                    Handle = default,
                    Readiness = DetachReadiness(resource.ImportedBuffer.Readiness),
                },
                ImportedTexture = resource.ImportedTexture with
                {
                    Handle = default,
                    Readiness = DetachReadiness(resource.ImportedTexture.Readiness),
                },
            };
            resources[index] = resources[index] with
            {
                ImportedBuffer = resources[index].ImportedBuffer with
                {
                    Metadata = resources[index].ImportedBuffer.Metadata with { Allocation = default },
                },
                ImportedTexture = resources[index].ImportedTexture with
                {
                    Metadata = resources[index].ImportedTexture.Metadata with { Allocation = default },
                },
            };
        }

        FrozenPass[] passes = new FrozenPass[Passes.Length];
        for (int index = 0; index < passes.Length; index++)
        {
            FrozenPass pass = Passes[index];
            FrozenColorAttachment[] colors = pass.ColorAttachments
                .Select(static attachment => attachment with { ClearColor = default })
                .ToArray();
            passes[index] = new FrozenPass(
                pass.Name,
                pass.Queues,
                pass.RecordingLane,
                pass.Accesses,
                colors,
                pass.DepthStencilAttachment is FrozenDepthStencilAttachment depthStencil
                    ? depthStencil with
                    {
                        Depth = depthStencil.Depth is DepthAttachmentOps depth
                            ? depth with { ClearValue = default }
                            : null,
                        Stencil = depthStencil.Stencil is StencilAttachmentOps stencil
                            ? stencil with { ClearValue = default }
                            : null,
                    }
                    : null,
                pass.Shaders,
                [],
                null,
                pass.Identity);
        }
        return new FrozenGraph(Token, resources, BufferViews, TextureViews, passes, Canonical);
    }

    private static GpuCompletion[] DetachReadiness(GpuCompletion[]? readiness)
    {
        if (readiness is null || readiness.Length == 0) return [];
        // Queue shape participates in compilation (for example, deciding whether merging would
        // hoist a cross-queue wait). Domain and timeline values belong only to the invocation.
        return readiness
            .Select(static completion => new GpuCompletion(default, completion.Queue, 0))
            .ToArray();
    }
}

internal readonly record struct FrozenResource(
    ResourceNodeKind Kind,
    BufferDesc BufferDesc,
    TextureDesc TextureDesc,
    bool Imported,
    ImportedBuffer ImportedBuffer,
    ImportedTexture ImportedTexture,
    ResourceRequirements Requirements)
{
    public bool IsImported => Imported;
}

internal readonly record struct FrozenBufferView(
    int Resource,
    BufferRange Range,
    BindingKind Kind,
    Format Format,
    uint Stride,
    string? Name);

internal readonly record struct FrozenTextureView(
    int Resource,
    TextureSubresourceRange Range,
    TextureViewUsage Usage,
    Format Format,
    TextureViewDimension Dimension,
    string? Name);

internal sealed class FrozenPass
{
    public FrozenPass(
        string name,
        QueueSelection queues,
        PassRecordingLane recordingLane,
        FrozenAccess[] accesses,
        FrozenColorAttachment[] colorAttachments,
        FrozenDepthStencilAttachment? depthStencilAttachment,
        FrozenShaderContract[] shaders,
        PipelineHandle[] pipelines,
        PassExecution? execution,
        ExecutorIdentity identity)
    {
        Name = name;
        Queues = queues;
        RecordingLane = recordingLane;
        Accesses = accesses;
        ColorAttachments = colorAttachments;
        DepthStencilAttachment = depthStencilAttachment;
        Shaders = shaders;
        Pipelines = pipelines;
        Execution = execution;
        Identity = identity;
    }

    public string Name { get; }
    public QueueSelection Queues { get; }
    public PassRecordingLane RecordingLane { get; }
    public FrozenAccess[] Accesses { get; }
    public FrozenColorAttachment[] ColorAttachments { get; }
    public FrozenDepthStencilAttachment? DepthStencilAttachment { get; }
    public FrozenShaderContract[] Shaders { get; }
    public PipelineHandle[] Pipelines { get; }
    public PassExecution? Execution { get; }
    public ExecutorIdentity Identity { get; }
}

internal readonly record struct FrozenAccess(
    ResourceNodeKind Kind,
    int Resource,
    int View,
    ResourceEffect Effect,
    BufferUse BufferUse,
    TextureUse TextureUse,
    BufferRange BufferRange,
    TextureSubresourceRange TextureRange,
    PriorContents PriorContents,
    WriteCoverage Coverage);

internal readonly record struct FrozenColorAttachment(
    int Slot,
    int View,
    int Access,
    LoadAction Load,
    Vector4 ClearColor);

internal readonly record struct FrozenDepthStencilAttachment(
    int View,
    int DepthAccess,
    int StencilAccess,
    DepthAttachmentOps? Depth,
    StencilAttachmentOps? Stencil);

internal sealed class FrozenShaderContract
{
    public FrozenShaderContract(
        ShaderArtifactKey key,
        ShaderStage stage,
        ulong layoutHash,
        ShaderBinding[] bindings,
        PushConstantRange[] pushConstants,
        FrozenShaderBindingAccess[] accesses)
    {
        Key = key;
        Stage = stage;
        LayoutHash = layoutHash;
        Bindings = bindings;
        PushConstants = pushConstants;
        Accesses = accesses;
    }

    public ShaderArtifactKey Key { get; }
    public ShaderStage Stage { get; }
    public ulong LayoutHash { get; }
    public ShaderBinding[] Bindings { get; }
    public PushConstantRange[] PushConstants { get; }
    public FrozenShaderBindingAccess[] Accesses { get; }
}

internal readonly record struct FrozenShaderBindingAccess(
    uint Group,
    uint Binding,
    uint Element,
    ShaderBindingAccessKind Kind,
    int Access,
    int View);
