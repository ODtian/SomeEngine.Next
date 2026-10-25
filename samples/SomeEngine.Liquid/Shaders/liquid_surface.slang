#if STAGE_DrawLiquidMesh
StructuredBuffer<SurfaceVertex> MeshVerticesRead;
StructuredBuffer<uint> MeshIndicesRead;
#endif

struct LiquidMeshVaryings
{
    float4 Position : SV_Position;
    float3 World : WORLD;
    float3 Normal : NORMAL;
    float3 Edges : EDGES;
    float ContactHeight : CONTACT_HEIGHT;
    float Depth : DEPTH;
    float3 Water : WATER;
    nointerpolation uint ContactSurface : CONTACT_SURFACE;
};
#if STAGE_DrawLiquidMesh
[shader("vertex")]
LiquidMeshVaryings DrawLiquidMesh(uint vertex : SV_VertexID)
{
    uint index = MeshIndicesRead[vertex];
    SurfaceVertex data = MeshVerticesRead[index];
    float3 offset = data.Position.xyz - Settings.Camera.xyz;
    float z = dot(offset, Settings.Forward.xyz);
    LiquidMeshVaryings output;
    output.Position = float4(dot(offset, Settings.Right.xyz) / (Settings.Camera.w * Settings.Screen.x / Settings.Screen.y),
        dot(offset, Settings.Up.xyz) / Settings.Camera.w, 20.0 / 19.95 * z - 1.0 / 19.95, z);
    output.World = data.Position.xyz; output.Normal = data.NormalHeight.xyz;
    if (data.Position.w == 0) output.Position = float4(0, 0, 0, 1);
    output.Edges = vertex % 3 == 0 ? float3(1,0,0) : vertex % 3 == 1 ? float3(0,1,0) : float3(0,0,1);
    output.ContactHeight = data.NormalHeight.w; output.Depth = z;
    output.Water = data.Detail.xyz; output.ContactSurface = uint(data.Detail.w);
    return output;
}
#endif

#if STAGE_LiquidMeshPixel
StructuredBuffer<float4> BackgroundRead;
struct LiquidPixelOutput { float4 Surface : SV_Target0; float4 Detail : SV_Target1; float Depth : SV_Depth; };
[shader("fragment")]
LiquidPixelOutput LiquidMeshPixel(LiquidMeshVaryings input)
{
    uint pixel = (uint(input.Position.x) + uint(input.Position.y) * uint(Settings.Screen.x)) * 2;
    float4 background = BackgroundRead[pixel], geometry = BackgroundRead[pixel + 1];
    bool sameSurface = input.ContactSurface == uint(geometry.w);
    if (input.Water.z > .999 && !sameSurface) discard;
    float attachment = sameSurface && abs(input.Depth - background.w) < .015 + input.ContactHeight * 2 ? smoothstep(.75, 1, input.Water.z) : 0;
    float3 ray = CameraRay(input.Position.xy - .5);
    float3 contactPosition = Settings.Camera.xyz + ray * (background.w / dot(ray, Settings.Forward.xyz)) + geometry.xyz * input.ContactHeight;
    float3 world = lerp(input.World, contactPosition, attachment);
    clip(Settings.Fluid.z - abs(world.x));
    clip(Settings.Fluid.w - abs(world.z));
    clip(world.y - Settings.Fluid.y);
    float depth = dot(world - Settings.Camera.xyz, Settings.Forward.xyz);
    clip(background.w - depth + .00005);
    float3 contactNormal = normalize(cross(ddx(world), ddy(world)));
    if (dot(contactNormal, input.Normal) < 0) contactNormal = -contactNormal;
    float3 normal = normalize(lerp(input.Normal, contactNormal, attachment));
    if (dot(normal, Settings.Camera.xyz - world) < 0) normal = -normal;
    float extra = input.Water.y;
    if (Settings.Frame.w == 3)
    {
        float3 width = fwidth(input.Edges) * 1.1;
        float3 smooth = smoothstep(0, width, input.Edges);
        extra = 1 - min(smooth.x, min(smooth.y, smooth.z));
    }
    float3 dx = ddx(normal), dy = ddy(normal);
    float variance = dot(dx, dx) + dot(dy, dy);
    LiquidPixelOutput output;
    output.Surface = float4(normal, depth);
    output.Detail = float4(input.Water.x, extra, variance, 1);
    output.Depth = 20.0 / 19.95 - 1.0 / (19.95 * depth);
    return output;
}
#endif

#if STAGE_ShadeMesh
Texture2D<float4> MeshSurface;
Texture2D<float4> MeshDetail;
StructuredBuffer<float4> BackgroundRead;
RWTexture2DArray<float4> SceneColor;
[shader("compute")]
[numthreads(8, 8, 1)]
void ShadeMesh(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= uint2(Settings.Screen.xy))) return;
    uint pixel = (id.x + id.y * uint(Settings.Screen.x)) * 2;
    float4 surface = MeshSurface[id.xy], detail = MeshDetail[id.xy], background = BackgroundRead[pixel];
    float3 color = background.xyz;
    float coverage = detail.w;
    if (coverage > 0)
    {
        detail /= coverage;
        float extra = detail.y;
        float depth = surface.w / coverage;
        float3 normal = normalize(surface.xyz);
        float3 direction = CameraRay(id.xy);
        float thickness = detail.x / max(.3, dot(-direction, normal));
        float3 position = Settings.Camera.xyz + direction * (depth / dot(direction, Settings.Forward.xyz));
        color = ShadeLiquid(position, direction, normal, thickness, 0, detail.z);
        if (Settings.Frame.w == 4) color = normal * .5 + .5;
        if (Settings.Frame.w == 2) color = thickness * float3(2, 4, 6);
        if (Settings.Frame.w == 3) color = lerp(float3(.15, .55, .65), float3(.01, .03, .04), extra);
        if (Settings.Frame.w == 1 || Settings.Frame.w == 5)
            color = (extra < .5 ? float3(.24,.48,.9) : extra < 1.5 ? float3(.95,.43,.12) : float3(.73,.29,.68)) *
                (.4 + .6 * saturate(dot(normal, normalize(float3(-.4,.8,.3)))));
        color = lerp(background.xyz, color, coverage);
    }
    float2 uv = (float2(id.xy) + .5) / Settings.Screen.xy * 2 - 1;
    SceneColor[uint3(id.xy, 0)] = float4(max(color * (1 - .12 * dot(uv, uv)), 0), 1);
}
#endif
