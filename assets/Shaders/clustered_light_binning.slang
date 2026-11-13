__exported import render_lighting;
__exported import screen_binning;

struct ClusteredLightBinningView
{
    float4x4 View;
    float4x4 Projection;
    uint TileSizeX;
    uint TileSizeY;
    uint TileCountX;
    uint TileCountY;
    float4 ZParams;
    uint DepthSliceCount;
    uint MaxLightsPerCell;
    uint DirectionalCount;
    uint NonDirectionalCount;
    uint ScreenWidth;
    uint ScreenHeight;
    uint CellCountPerView;
    uint Padding;
    float4x4 InverseProjection;
};

StructuredBuffer<ClusteredLightBinningView> LightBinningViews;
StructuredBuffer<GPULight> LightBuffer;
StructuredBuffer<uint> LightBinningKinds;

uint LightBinningDepthSlice(ClusteredLightBinningView view, float depth)
{
    return uint(clamp(floor(log2(max(depth, view.ZParams.x)) * view.ZParams.z + view.ZParams.w),
        0.0, float(view.DepthSliceCount - 1u)));
}

float3 LightBinningViewPosition(ClusteredLightBinningView view, float2 ndc, float depth)
{
    float4 first = mul(float4(ndc, 0.25, 1.0), view.InverseProjection);
    float4 second = mul(float4(ndc, 0.75, 1.0), view.InverseProjection);
    float3 a = first.xyz / first.w;
    float3 b = second.xyz / second.w;
    return a + (b - a) * ((-depth - a.z) / (b.z - a.z));
}

void LightBinningCellBounds(ClusteredLightBinningView view, uint3 cell,
    out float3 minimum, out float3 maximum)
{
    float2 pixelMinimum = float2(cell.xy * uint2(view.TileSizeX, view.TileSizeY));
    float2 pixelMaximum = min(pixelMinimum + float2(view.TileSizeX, view.TileSizeY),
        float2(view.ScreenWidth, view.ScreenHeight));
    float2 ndcMinimum = float2(pixelMinimum.x / view.ScreenWidth * 2.0 - 1.0,
        1.0 - pixelMaximum.y / view.ScreenHeight * 2.0);
    float2 ndcMaximum = float2(pixelMaximum.x / view.ScreenWidth * 2.0 - 1.0,
        1.0 - pixelMinimum.y / view.ScreenHeight * 2.0);
    float nearDepth = view.ZParams.x * exp2(float(cell.z) / view.ZParams.z);
    float farDepth = view.ZParams.x * exp2(float(cell.z + 1u) / view.ZParams.z);
    minimum = float3(1e30);
    maximum = float3(-1e30);
    for (uint corner = 0u; corner < 8u; corner++)
    {
        float2 ndc = float2((corner & 1u) == 0u ? ndcMinimum.x : ndcMaximum.x,
            (corner & 2u) == 0u ? ndcMinimum.y : ndcMaximum.y);
        float3 point = LightBinningViewPosition(view, ndc, (corner & 4u) == 0u ? nearDepth : farDepth);
        minimum = min(minimum, point);
        maximum = max(maximum, point);
    }
}

bool ClusteredLightIntersectsCell(GPULight light, uint kind,
    ClusteredLightBinningView view, uint3 cell)
{
    float3 minimum, maximum;
    LightBinningCellBounds(view, cell, minimum, maximum);
    float3 position = mul(float4(light.Position, 1.0), view.View).xyz;
    float3 delta = position - clamp(position, minimum, maximum);
    if (dot(delta, delta) > light.Range * light.Range)
        return false;
    if (kind == 0u)
        return true;

    float3 direction = normalize(mul(float4(light.Direction, 0.0), view.View).xyz);
    float3 centerOffset = (minimum + maximum) * 0.5 - position;
    float radius = length((maximum - minimum) * 0.5);
    float axialDistance = dot(centerOffset, direction);
    if (axialDistance + radius < 0.0 || axialDistance - radius > light.Range)
        return false;
    float radialDistance = length(centerOffset - direction * axialDistance);
    float coneCos = light.OuterConeCos;
    float coneSin = sqrt(1.0 - coneCos * coneCos);
    return radialDistance * coneCos - axialDistance * coneSin <= radius;
}

ScreenBinningBounds LightBinningBounds(GPULight light, ClusteredLightBinningView view)
{
    ScreenBinningBounds bounds = { uint3(0u), uint3(0u) };
    float radius = light.Range;
    float3 position = mul(float4(light.Position, 1.0), view.View).xyz;
    float depth = -position.z;
    float minimumDepth = max(view.ZParams.x, depth - radius);
    float maximumDepth = min(view.ZParams.y, depth + radius);
    if (radius <= 0.0 || maximumDepth < view.ZParams.x || minimumDepth > view.ZParams.y)
        return bounds;

    uint2 minimumTile = uint2(0u);
    uint2 maximumTile = uint2(view.TileCountX - 1u, view.TileCountY - 1u);
    if (depth > radius + view.ZParams.x)
    {
        float2 ndcMinimum = float2(1e30);
        float2 ndcMaximum = float2(-1e30);
        for (uint corner = 0u; corner < 8u; corner++)
        {
            float3 offset = float3((corner & 1u) == 0u ? -radius : radius,
                (corner & 2u) == 0u ? -radius : radius,
                (corner & 4u) == 0u ? -radius : radius);
            float4 clip = mul(float4(position + offset, 1.0), view.Projection);
            ndcMinimum = min(ndcMinimum, clip.xy / clip.w);
            ndcMaximum = max(ndcMaximum, clip.xy / clip.w);
        }
        float2 pixelMinimum = float2(ndcMinimum.x + 1.0, 1.0 - ndcMaximum.y) * 0.5 *
            float2(view.ScreenWidth, view.ScreenHeight);
        float2 pixelMaximum = float2(ndcMaximum.x + 1.0, 1.0 - ndcMinimum.y) * 0.5 *
            float2(view.ScreenWidth, view.ScreenHeight);
        if (pixelMaximum.x < 0.0 || pixelMaximum.y < 0.0 ||
            pixelMinimum.x >= view.ScreenWidth || pixelMinimum.y >= view.ScreenHeight)
            return bounds;
        minimumTile = uint2(clamp(int2(floor(pixelMinimum / float2(view.TileSizeX, view.TileSizeY))),
            int2(0), int2(view.TileCountX - 1u, view.TileCountY - 1u)));
        maximumTile = uint2(clamp(int2(floor(pixelMaximum / float2(view.TileSizeX, view.TileSizeY))),
            int2(0), int2(view.TileCountX - 1u, view.TileCountY - 1u)));
    }
    uint minimumSlice = LightBinningDepthSlice(view, minimumDepth);
    uint maximumSlice = LightBinningDepthSlice(view, maximumDepth);
    bounds.Minimum = uint3(minimumTile, minimumSlice);
    bounds.Size = uint3(maximumTile - minimumTile + 1u, maximumSlice - minimumSlice + 1u);
    return bounds;
}

struct ClusteredLightBinningItem : IScreenBinningItem
{
    static ScreenBinningBounds GetBounds(uint itemIndex)
    {
        uint count = LightBinningViews[0].NonDirectionalCount;
        ClusteredLightBinningView view = LightBinningViews[itemIndex / count];
        return LightBinningBounds(LightBuffer[view.DirectionalCount + itemIndex % count], view);
    }

    static bool Intersects(uint itemIndex, uint3 cell)
    {
        uint count = LightBinningViews[0].NonDirectionalCount;
        ClusteredLightBinningView view = LightBinningViews[itemIndex / count];
        uint lightIndex = view.DirectionalCount + itemIndex % count;
        return ClusteredLightIntersectsCell(LightBuffer[lightIndex], LightBinningKinds[lightIndex], view, cell);
    }

    static uint GetCellIndex(uint itemIndex, uint3 cell)
    {
        uint viewIndex = itemIndex / LightBinningViews[0].NonDirectionalCount;
        ClusteredLightBinningView view = LightBinningViews[viewIndex];
        return viewIndex * view.CellCountPerView + (cell.z * view.TileCountY + cell.y) * view.TileCountX + cell.x;
    }

    static uint GetItemIndex(uint itemIndex)
    {
        ClusteredLightBinningView view = LightBinningViews[0];
        return view.DirectionalCount + itemIndex % view.NonDirectionalCount;
    }
}

[shader("compute")]
[numthreads(256, 1, 1)]
void CountScreenItems(uint3 id : SV_DispatchThreadID)
{
    ScreenBinningCount<ClusteredLightBinningItem>(ScreenBinningThreadIndex(id));
}

[shader("compute")]
[numthreads(256, 1, 1)]
void ExpandScreenItems(uint3 id : SV_DispatchThreadID)
{
    ScreenBinningExpand<ClusteredLightBinningItem>(ScreenBinningThreadIndex(id));
}

[shader("compute")]
[numthreads(256, 1, 1)]
void FillScreenItems(uint3 id : SV_DispatchThreadID)
{
    ScreenBinningFill<ClusteredLightBinningItem>(ScreenBinningThreadIndex(id));
}
