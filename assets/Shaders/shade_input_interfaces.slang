#ifndef SHADE_INPUT_INTERFACES_SLANG
#define SHADE_INPUT_INTERFACES_SLANG

__exported import material_interfaces;

interface IShadeInput<TVertex : IVertexEvaluate, TDomain : IDomainEvaluate<TVertex.Vertex>>
{
    bool Read(TVertex vertex, TDomain domain, uint3 thread,
        out SurfacePixelInput input, out TDomain.Surface surface,
        out float3 bary, out TDomain.TriangleData triangle, out uint outputIndex);
};

#endif
