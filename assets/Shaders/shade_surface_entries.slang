#ifndef SHADE_SURFACE_ENTRIES_SLANG
#define SHADE_SURFACE_ENTRIES_SLANG

__exported import shade_input_interfaces;

// Geometry, input reconstruction and output storage are selected when the
// entry is baked. Every target calls the same complete material program.
void ShadeSurface<M : IMaterial, TInput, TOutput, let debugMode : uint>(
    uniform M material, uniform M.Vertex vertex, uniform M.Domain domain,
    TInput source, TOutput output, uint3 thread)
    where M.Visibility : IVisibilityEvaluate<M.Vertex.Vertex>
    where TInput : IShadeInput<M.Vertex, M.Domain>
    where TOutput : ISurfaceOutput
{
    SurfacePixelInput input;
    M.Domain.Surface surface;
    M.Domain.TriangleData triangle;
    float3 bary;
    uint outputIndex;
    if (!source.Read(vertex, domain, thread, input, surface, bary, triangle, outputIndex)) return;
    output.SetIndex(outputIndex);
    if (debugMode == 0u)
        material.evaluateSurface<TOutput>(input, surface, bary, triangle, output);
    else
        material.evaluateDebug<TOutput>(debugMode, input, surface, bary, triangle, output);
}

#endif
