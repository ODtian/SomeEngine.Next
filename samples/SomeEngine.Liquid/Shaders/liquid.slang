struct LiquidSettings
{
    float4 Screen, View, Frame, Fluid, GridSize, GridMinimum;
    float4 Camera, Target, Right, Up, Forward;
    float4 Profile, Ridges, Ripple;
    float4 Extinction, Scattering;
};
ConstantBuffer<LiquidSettings> Settings;
struct LiquidObstacle { float4 Center; float4 Size; float4 Rotation; };
StructuredBuffer<LiquidObstacle> Obstacles;
struct SurfaceVertex { float4 Position; float4 NormalHeight; float4 Detail; };
float3 Rotate(float4 rotation, float3 value)
{
    return value + 2 * cross(rotation.xyz, cross(rotation.xyz, value) + rotation.w * value);
}
float3 CameraRay(float2 pixel)
{
    float2 uv = (pixel + .5) / Settings.Screen.xy * 2 - 1;
    uv *= float2(Settings.Screen.x / Settings.Screen.y, -1);
    return normalize(Settings.Forward.xyz + Settings.Camera.w * (uv.x * Settings.Right.xyz + uv.y * Settings.Up.xyz));
}
float3 Sky(float3 direction)
{
    float3 color = lerp(float3(.16, .22, .29), float3(.72, .83, .91), saturate(direction.y * .7 + .35));
    float window = pow(saturate(dot(direction, normalize(float3(-.5, .8, .35)))), 28);
    return color + window * float3(2.8, 2.5, 2);
}
#include "liquid_geometry.slang"
#include "liquid_scene.slang"


#if STAGE_InitializeIndices
RWStructuredBuffer<uint> MeshIndicesWrite;
[shader("compute")]
[numthreads(64,1,1)]
void InitializeIndices(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= LIQUID_CURVES * LIQUID_ALONG * LIQUID_SIDES * 6) return;
    uint quad = id.x / 6, corner = id.x % 6;
    uint section = quad / (LIQUID_ALONG * LIQUID_SIDES);
    uint along = (quad / LIQUID_SIDES) % LIQUID_ALONG, side = quad % LIQUID_SIDES;
    uint first = section * (LIQUID_ALONG+1) * (LIQUID_SIDES+1) + along * (LIQUID_SIDES+1) + side;
    uint offsets[6] = { 0, LIQUID_SIDES+1, 1, 1, LIQUID_SIDES+1, LIQUID_SIDES+2 };
    MeshIndicesWrite[id.x] = first + offsets[corner];
}
#endif

#if STAGE_BuildLiquidMesh
StructuredBuffer<float4> PositionVolume, Contact, SurfaceSize, Footprint;
StructuredBuffer<float4> CurveStart, CurveEnd, CurveFirstTangent, CurveLastTangent, CurveRadius;
RWStructuredBuffer<SurfaceVertex> MeshVerticesWrite;
RWStructuredBuffer<uint> MeshArgumentsWrite;
[shader("compute")]
[numthreads(64,1,1)]
void BuildLiquidMesh(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= uint(Settings.GridSize.z)) return;
    if (id.x == 0)
    {
        MeshArgumentsWrite[0] = uint(Settings.GridSize.w);
        MeshArgumentsWrite[1] = 1; MeshArgumentsWrite[2] = MeshArgumentsWrite[3] = 0;
    }
    SurfaceVertex vertex; vertex.Position = vertex.NormalHeight = vertex.Detail = 0;
    const uint verticesPerSection = (LIQUID_ALONG + 1) * (LIQUID_SIDES + 1);
    uint point = id.x / verticesPerSection, local = id.x % verticesPerSection;
    if (point >= uint(Settings.Screen.z)) { MeshVerticesWrite[id.x] = vertex; return; }
    float t = (local / (LIQUID_SIDES+1)) / float(LIQUID_ALONG);
    float angle = (local % (LIQUID_SIDES+1)) * (6.2831853 / LIQUID_SIDES);
    float3 center = PositionVolume[point].xyz;
    uint surface = uint(Contact[point].w);
    if (Settings.Frame.w == 5 && surface == 0) { MeshVerticesWrite[id.x] = vertex; return; }
    float angleStep = 6.2831853 / LIQUID_SIDES;
    if (Settings.Frame.w == 1)
    {
        float latitude = t * 3.14159265;
        float3 n = float3(sin(latitude)*cos(angle), cos(latitude), sin(latitude)*sin(angle));
        vertex.Position = float4(center + n * .018, 1);
        vertex.NormalHeight = float4(n, 0);
        vertex.Detail = float4(.035, SurfaceSize[point].w, 0, 0);
    }
    else if (surface > 0)
    {
        float a = SurfaceSize[point].x, b = SurfaceSize[point].y;
        float3 axis = Footprint[point].xyz, across = cross(Contact[point].xyz, axis);
        float x = t * cos(angle), z = t * sin(angle);
        float correction = angleStep / sin(angleStep) / (1 - 1.0 / (3 * LIQUID_ALONG * LIQUID_ALONG));
        float peakHeight = SurfaceSize[point].z * correction;
        float height = peakHeight * (1 - t*t);
        float3 normal;
        float3 p = ProjectContact(center + axis * (a*x) + across * (b*z), surface, height, normal);
        float3 capNormal = normalize(normal + axis * (2*peakHeight*x/a) + across * (2*peakHeight*z/b));
        vertex.Position = float4(p, 1);
        vertex.NormalHeight = float4(capNormal, height);
        vertex.Detail = float4(height, SurfaceSize[point].w, 1, surface);
    }
    else if (CurveStart[point].w > 0)
    {
        float t2 = t*t, t3 = t2*t;
        float3 a = CurveStart[point].xyz, b = CurveEnd[point].xyz;
        float3 ma = CurveFirstTangent[point].xyz, mb = CurveLastTangent[point].xyz;
        center = a + (-2*t3+3*t2)*(b-a) + (t3-2*t2+t)*ma + (t3-t2)*mb;
        float3 derivative = (-6*t2+6*t)*(b-a) + (3*t2-4*t+1)*ma + (3*t2-2*t)*mb;
        float3 tangent = normalize(derivative);
        float3 side = normalize(cross(tangent, abs(tangent.z) < .9 ? float3(0,0,1) : float3(0,1,0)));
        float3 up = cross(side, tangent);
        float3 profile = CurveRadius[point].xyz;
        float narrowing = 1 - CurveRadius[point].w * CurveRadius[point].w;
        float wave = sin(3.14159265*t), middle = 1 - narrowing*wave*wave;
        float shoulders = 1 - narrowing + 7*narrowing*(2*t-1)*(2*t-1);
        float blend = t2 * (3 - 2*t), core = 30*t2*(1-t)*(1-t), bubble = core*shoulders;
        float baseRadius = lerp(profile.x, profile.y, blend);
        float squaredRadius = baseRadius*middle + profile.z*bubble;
        float radius = sqrt(squaredRadius * angleStep / sin(angleStep));
        float squaredSlope = (profile.y-profile.x)*6*t*(1-t)*middle - baseRadius*narrowing*3.14159265*sin(6.2831853*t) +
            profile.z*(60*t*(1-t)*(1-2*t)*shoulders + core*28*narrowing*(2*t-1));
        float3 radial = side*cos(angle) + up*sin(angle);
        float3 n = radius > 0 ? normalize(radial - tangent * (squaredSlope * angleStep / sin(angleStep) / (2*radius*length(derivative)))) :
            tangent * (t < .5 ? -1 : 1);
        vertex.Position = float4(center + radial*radius, 1);
        vertex.NormalHeight = float4(n, 0);
        vertex.Detail = float4(2*radius, 0, 0, 0);
    }
    else
    {
        float radius = pow(PositionVolume[point].w * (3 / (4 * 3.14159265)), 1.0 / 3);
        float latitude = t * 3.14159265;
        float3 n = float3(sin(latitude)*cos(angle), cos(latitude), sin(latitude)*sin(angle));
        vertex.Position = float4(center + n*radius, 1);
        vertex.NormalHeight = float4(n, 0);
        vertex.Detail = float4(2*radius, 0, 0, 0);
    }
    MeshVerticesWrite[id.x] = vertex;
}
#endif

#if STAGE_ClearBackground
RWStructuredBuffer<float4> BackgroundWrite;
[shader("compute")]
[numthreads(8,8,1)]
void ClearBackground(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= uint2(Settings.Screen.xy))) return;
    float3 direction = CameraRay(id.xy);
    float distance; float3 normal; uint contactSurface;
    float3 color = TraceOpaque(Settings.Camera.xyz, direction, distance, normal, contactSurface);
    uint pixel = (id.x + id.y * uint(Settings.Screen.x)) * 2;
    BackgroundWrite[pixel] = float4(color, distance * dot(direction, Settings.Forward.xyz));
    BackgroundWrite[pixel + 1] = float4(normal, contactSurface);
}
#endif

#include "liquid_surface.slang"
