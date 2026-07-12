namespace SomeEngine.Graphics;

/// <summary>Portable limits used for admission and validation. Zero never means “unbounded”.</summary>
public readonly record struct DeviceLimits(
    ulong MaxBufferSize,
    uint MaxTextureDimension1D,
    uint MaxTextureDimension2D,
    uint MaxTextureDimension3D,
    uint MaxTextureArrayLayers,
    uint MaxBindGroups,
    uint MaxBindingsPerGroup,
    uint MaxDescriptorArrayLength,
    uint MaxPushConstantBytes,
    uint MinConstantBufferOffsetAlignment,
    uint MinStorageBufferOffsetAlignment,
    uint TextureDataPitchAlignment,
    uint TextureDataPlacementAlignment);

/// <summary>
/// Immutable capability truth. Optional features are false unless the backend has completed its
/// native discovery path; mandatory traditional binding remains independent of bindless.
/// </summary>
public readonly record struct DeviceCapabilities(
    bool SupportsTraditionalBinding,
    bool SupportsIndirectDraw,
    bool SupportsIndirectDrawIndexed,
    bool SupportsIndirectDispatch,
    bool SupportsTimestampQueries,
    bool SupportsOcclusionQueries,
    bool SupportsPipelineStatisticsQueries,
    bool SupportsSwapchain,
    bool SupportsPipelineCache,
    bool SupportsMemoryBudget,
    bool SupportsBindless,
    bool SupportsMeshShaders,
    bool SupportsVariableRateShading,
    bool SupportsRayTracing,
    bool SupportsSparseResources,
    bool SupportsSamplerFeedback,
    bool SupportsWorkGraphs,
    Version HighestShaderModel,
    DeviceLimits Limits);

[Flags]
public enum FormatSupport : uint
{
    None = 0,
    Sampled = 1u << 0,
    Storage = 1u << 1,
    RenderTarget = 1u << 2,
    DepthStencil = 1u << 3,
    VertexBuffer = 1u << 4,
    IndexBuffer = 1u << 5,
    Copy = 1u << 6,
    Present = 1u << 7,
    Resolve = 1u << 8,
}
