using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render;

/// <summary>The indexed raster work produced by one geometry stage's shared patch processing.</summary>
public readonly record struct RenderTessellationRasterData(
    uint StageIndex, GraphBufferId DrawArguments, ulong DrawArgumentsOffset,
    GraphBufferId PatternIndices, GraphBufferId PatchIndices,
    uint Width, uint Height, GraphRegionId Region);

public delegate void RenderRasterResourceBinder(ref RenderGraphEdit edit, ref PassDefinition pass,
    in ShaderParameterResource resource, PipelineSync sync);

/// <summary>Creates the target's terminal draw and returns its graph identity.</summary>
public delegate GraphPassId RenderTessellationRasterRegistration(ref RenderGraphEdit edit,
    in RenderTessellationRasterData data, Material material, IndirectCommandLayout drawLayout,
    RenderRasterResourceBinder bindResource);

/// <summary>A representative material for one Vertex, Domain and Visibility combination.</summary>
public readonly record struct RenderRasterStage(uint Index, RenderMaterialEntry Material, bool Tessellated);
