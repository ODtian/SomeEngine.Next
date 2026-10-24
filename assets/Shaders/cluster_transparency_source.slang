#ifndef CLUSTER_TRANSPARENCY_SOURCE_SLANG
#define CLUSTER_TRANSPARENCY_SOURCE_SLANG

#include "cluster_structures.slang"
#include "visbuffer_encoding.slang"

struct TransparencySource
{
    uint PageOffset;
    uint InstanceAndTriangle;
    uint ClusterAndView;
};

struct TransparencyGeometryParameters
{
    uint MaxFactor;
    float TargetEdgePixels;
};

StructuredBuffer<TransparencySource> TransparencySources;
ConstantBuffer<TransparencyGeometryParameters> TransparencyGeometryUniforms;

DrawRequest TransparencySourceRequest(uint source)
{
    TransparencySource record = TransparencySources[source >> 7u];
    DrawRequest request;
    request.PageOffset = record.PageOffset;
    request.InstanceID = record.InstanceAndTriangle & 0xFFFFFFu;
    request.ClusterAndView = record.ClusterAndView;
    return request;
}

uint TransparencySourceTriangle(uint source)
{
    return source & 127u;
}

#endif
