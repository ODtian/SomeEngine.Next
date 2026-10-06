#ifndef SURFACE_RECEIVER_SLANG
#define SURFACE_RECEIVER_SLANG

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
};

interface ISurfaceReceiverEvaluate<TSurface, TTriangle>
{
    SurfaceReceiver evaluateReceiver(uint3 pixelCoord, uint instanceID, uint materialIndex,
        TSurface surface, float3 bary, TTriangle triangle);
};

bool SurfaceReceiverHasLighting(SurfaceReceiver receiver)
{
    return (receiver.Flags & SURFACE_RECEIVER_LIGHTING) != 0u;
}

bool SurfaceReceiverUsesBackfaceCulling(SurfaceReceiver receiver)
{
    return (receiver.Flags & (SURFACE_RECEIVER_SUBSURFACE | SURFACE_RECEIVER_HAIR |
        SURFACE_RECEIVER_MULTIPLE_NORMAL_BASES)) == 0u;
}

bool SurfaceReceiverIsBackfaceToDirectional(SurfaceReceiver receiver, float3 towardLight, float sourceAngleRadians)
{
    return dot(receiver.ShadingNormal, towardLight) < -max(abs(sin(sourceAngleRadians * .5)), .1);
}

bool SurfaceReceiverIsBackfaceToLocal(SurfaceReceiver receiver, float3 towardLight, float sourceRadiusCentimeters)
{
    float distanceSquared = dot(towardLight, towardLight);
    float sinAlpha = sqrt(saturate(sourceRadiusCentimeters * sourceRadiusCentimeters / (distanceSquared + 1.0)));
    return dot(receiver.ShadingNormal, towardLight * rsqrt(distanceSquared)) < -sinAlpha;
}

#endif
