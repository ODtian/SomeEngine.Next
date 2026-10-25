#ifndef SURFACE_RECEIVER_SLANG
#define SURFACE_RECEIVER_SLANG
#include "surface_receiver_depth.slang"

static const uint SURFACE_RECEIVER_PRESENT = 1u << 0;
static const uint SURFACE_RECEIVER_LIGHTING = 1u << 1;
static const uint SURFACE_RECEIVER_SUBSURFACE = 1u << 2;
static const uint SURFACE_RECEIVER_HAIR = 1u << 3;
static const uint SURFACE_RECEIVER_FIRST_PERSON = 1u << 4;
static const uint SURFACE_RECEIVER_MULTIPLE_NORMAL_BASES = 1u << 5;
static const uint SURFACE_RECEIVER_WATER = 1u << 6;
static const uint SURFACE_RECEIVER_TRANSLUCENCY = 1u << 7;

struct SurfaceReceiver
{
    float3 ShadingNormal;
    uint Flags;
    float3 GeometricNormal;
    float Depth;
    float3 Position;
    float DepthOffset;
};

struct SurfaceShadowReceiver
{
    float3 Position;
    float3 GeometricNormal;
    uint Flags;
    float Depth;
};

void WriteSurfaceReceiverLayer(RWStructuredBuffer<SurfaceReceiver> receivers, uint3 pixel,
    uint2 screenSize, uint viewCount, uint layer, SurfaceReceiver receiver)
{
    uint index = ((layer * viewCount + pixel.z) * screenSize.y + pixel.y) * screenSize.x + pixel.x;
    receiver.Flags |= SURFACE_RECEIVER_PRESENT;
    receivers[index] = receiver;
}


bool SurfaceReceiverHasLighting(SurfaceReceiver receiver)
{
    return (receiver.Flags & (SURFACE_RECEIVER_PRESENT | SURFACE_RECEIVER_LIGHTING)) ==
        (SURFACE_RECEIVER_PRESENT | SURFACE_RECEIVER_LIGHTING) && receiver.Depth >= 0.0 && receiver.Depth < 1.0;
}

bool SurfaceReceiverUsesBackfaceCulling(SurfaceReceiver receiver)
{
    return (receiver.Flags & (SURFACE_RECEIVER_SUBSURFACE | SURFACE_RECEIVER_HAIR |
        SURFACE_RECEIVER_MULTIPLE_NORMAL_BASES)) == 0u;
}

bool SurfaceReceiverIsBackfaceToDirectional(SurfaceReceiver receiver, float3 towardLight, float sourceAngleRadians)
{
    return dot(receiver.GeometricNormal, towardLight) < -max(abs(sin(sourceAngleRadians * .5)), .1);
}

bool SurfaceReceiverIsBackfaceToLocal(SurfaceReceiver receiver, float3 towardLight, float sourceRadiusCentimeters)
{
    float distanceSquared = dot(towardLight, towardLight);
    float sinAlpha = sqrt(saturate(sourceRadiusCentimeters * sourceRadiusCentimeters / (distanceSquared + 1.0)));
    return dot(receiver.GeometricNormal, towardLight * rsqrt(distanceSquared)) < -sinAlpha;
}

bool SurfaceReceiverReceivesDirectionalShadow(SurfaceReceiver receiver, float3 towardLight,
    float sourceAngleRadians, bool cullBackfaces)
{
    return SurfaceReceiverHasLighting(receiver) && (!cullBackfaces ||
        !SurfaceReceiverUsesBackfaceCulling(receiver) ||
        !SurfaceReceiverIsBackfaceToDirectional(receiver, towardLight, sourceAngleRadians));
}

bool SurfaceReceiverReceivesLocalShadow(SurfaceReceiver receiver, float3 towardLightCentimeters,
    float sourceRadiusCentimeters, bool cullBackfaces)
{
    return SurfaceReceiverHasLighting(receiver) && (!cullBackfaces ||
        !SurfaceReceiverUsesBackfaceCulling(receiver) ||
        !SurfaceReceiverIsBackfaceToLocal(receiver, towardLightCentimeters, sourceRadiusCentimeters));
}

#endif
