using System.Collections.Immutable;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render;

public readonly record struct RenderMaterialTextureRead(Material Material, uint Property, SomeEngine.Resources.Texture? Texture);

public readonly record struct RenderMaterialEntry(int Address, uint BinIndex, Material Material,
    uint VisibilityDepth, uint ResourceGroup, ImmutableArray<RenderMaterialTextureRead> TextureReads);

/// <summary>Material dispatch inputs and resource connections shared with independent render features.</summary>
public interface IRenderMaterialResources
{
    IReadOnlyList<RenderMaterialEntry> Materials { get; }
    IReadOnlyList<GraphPassId> MaterialPasses { get; }
    uint MaterialCapacity { get; }
    GraphBufferId DispatchArguments { get; }
    uint DispatchStride { get; }
    uint DebugMode { get; }
    GraphBufferSlice GetUniforms(in RenderMaterialEntry material);
    void BindResource(ref RenderGraphEdit edit, ref PassDefinition pass, in ShaderParameterResource resource,
        in RenderMaterialEntry material, GraphBufferSlice uniforms);
    void ReadTextures(ref RenderGraphEdit edit, ref PassDefinition pass,
        ImmutableArray<RenderMaterialTextureRead> textures, PipelineSync sync);
}
