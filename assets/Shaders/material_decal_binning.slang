__exported import screen_binning;
__exported import material_decals;

uint DecalDepthSlice(MaterialDecalBinningView view, float depth)
{
    return uint(clamp(floor(log2(max(depth, view.Depth.x)) * view.Depth.z + view.Depth.w),
        0.0, float(view.DepthSliceCount - 1u)));
}

void DecalCellBounds(MaterialDecalBinningView view, uint3 cell,
    out float3 center, out float3 halfSize)
{
    float nearDepth = exp2((float(cell.z) - view.Depth.w) / view.Depth.z);
    float farDepth = exp2((float(cell.z + 1u) - view.Depth.w) / view.Depth.z);
    float2 firstPixel = float2(cell.xy * view.TileSize);
    float2 lastPixel = min(float2((cell.xy + 1u) * view.TileSize), float2(view.ScreenSize));
    float3 first = float3(1e30), last = float3(-1e30);
    for (uint corner = 0u; corner < 8u; corner++)
    {
        float2 pixel = float2((corner & 1u) != 0u ? lastPixel.x : firstPixel.x,
            (corner & 2u) != 0u ? lastPixel.y : firstPixel.y);
        float2 ndc = pixel / float2(view.ScreenSize) * float2(2.0, -2.0) + float2(-1.0, 1.0);
        float4 start = mul(float4(ndc, 0.0, 1.0), view.InverseProjection);
        float4 end = mul(float4(ndc, 1.0, 1.0), view.InverseProjection);
        float3 a = start.xyz / start.w, b = end.xyz / end.w;
        float depth = (corner & 4u) != 0u ? farDepth : nearDepth;
        float3 point = a + (b - a) * ((-depth - a.z) / (b.z - a.z));
        first = min(first, point);
        last = max(last, point);
    }
    center = (first + last) * 0.5;
    halfSize = (last - first) * 0.5;
}

bool DecalSeparatingAxis(float3 delta, float3 cellHalfSize,
    float3 edgeX, float3 edgeY, float3 edgeZ, float3 axis)
{
    float radius = dot(cellHalfSize, abs(axis)) + abs(dot(edgeX, axis)) +
        abs(dot(edgeY, axis)) + abs(dot(edgeZ, axis));
    return abs(dot(delta, axis)) > radius;
}

struct MaterialDecalBinningItem : IScreenBinningItem
{
    static ScreenBinningBounds GetBounds(uint itemIndex)
    {
        uint decalCount = MaterialDecalViews[0].DecalCount;
        MaterialDecalBinningView view = MaterialDecalViews[itemIndex / decalCount];
        float4x4 transform = mul(MaterialDecalVolumes[itemIndex % decalCount].DecalToWorld, view.View);
        float2 first = float2(view.ScreenSize), last = float2(0.0);
        float nearDepth = 1e30, farDepth = -1e30;
        bool crossesNear = false;
        for (uint corner = 0u; corner < 8u; corner++)
        {
            float3 local = float3((corner & 1u) != 0u ? 1.0 : -1.0,
                (corner & 2u) != 0u ? 1.0 : -1.0, (corner & 4u) != 0u ? 1.0 : -1.0);
            float4 position = mul(float4(local, 1.0), transform);
            float depth = -position.z;
            nearDepth = min(nearDepth, depth);
            farDepth = max(farDepth, depth);
            crossesNear = crossesNear || depth < view.Depth.x;
            float4 clip = mul(position, view.Projection);
            float2 pixel = (clip.xy / clip.w * float2(0.5, -0.5) + 0.5) * float2(view.ScreenSize);
            first = min(first, pixel);
            last = max(last, pixel);
        }
        if (farDepth < view.Depth.x || nearDepth > view.Depth.y)
            return (ScreenBinningBounds)0;
        if (crossesNear)
        {
            first = float2(0.0);
            last = float2(view.ScreenSize) - 1.0;
        }
        if (any(last < 0.0) || any(first >= float2(view.ScreenSize)))
            return (ScreenBinningBounds)0;
        uint2 firstTile = uint2(clamp(floor(first / float(view.TileSize)), float2(0.0), float2(view.TileCount - 1u)));
        uint2 lastTile = uint2(clamp(floor(last / float(view.TileSize)), float2(0.0), float2(view.TileCount - 1u)));
        uint firstSlice = DecalDepthSlice(view, nearDepth);
        uint lastSlice = DecalDepthSlice(view, farDepth);
        ScreenBinningBounds bounds;
        bounds.Minimum = uint3(firstTile, firstSlice);
        bounds.Size = uint3(lastTile - firstTile + 1u, lastSlice - firstSlice + 1u);
        return bounds;
    }

    static bool Intersects(uint itemIndex, uint3 cell)
    {
        uint decalCount = MaterialDecalViews[0].DecalCount;
        MaterialDecalBinningView view = MaterialDecalViews[itemIndex / decalCount];
        float4x4 transform = mul(MaterialDecalVolumes[itemIndex % decalCount].DecalToWorld, view.View);
        float3 cellCenter, cellHalfSize;
        DecalCellBounds(view, cell, cellCenter, cellHalfSize);
        float3 center = mul(float4(0.0, 0.0, 0.0, 1.0), transform).xyz;
        float3 edgeX = mul(float4(1.0, 0.0, 0.0, 0.0), transform).xyz;
        float3 edgeY = mul(float4(0.0, 1.0, 0.0, 0.0), transform).xyz;
        float3 edgeZ = mul(float4(0.0, 0.0, 1.0, 0.0), transform).xyz;
        float3 delta = center - cellCenter;
        float3 axes[3] = { float3(1.0, 0.0, 0.0), float3(0.0, 1.0, 0.0), float3(0.0, 0.0, 1.0) };
        float3 edges[3] = { edgeX, edgeY, edgeZ };
        for (uint axis = 0u; axis < 3u; axis++)
        {
            if (DecalSeparatingAxis(delta, cellHalfSize, edgeX, edgeY, edgeZ, axes[axis])) return false;
            if (DecalSeparatingAxis(delta, cellHalfSize, edgeX, edgeY, edgeZ,
                    cross(edges[(axis + 1u) % 3u], edges[(axis + 2u) % 3u]))) return false;
            for (uint edge = 0u; edge < 3u; edge++)
                if (DecalSeparatingAxis(delta, cellHalfSize, edgeX, edgeY, edgeZ,
                        cross(axes[axis], edges[edge]))) return false;
        }
        return true;
    }

    static uint GetCellIndex(uint itemIndex, uint3 cell)
    {
        uint decalCount = MaterialDecalViews[0].DecalCount;
        uint viewIndex = itemIndex / decalCount;
        MaterialDecalBinningView view = MaterialDecalViews[viewIndex];
        return viewIndex * view.CellCount + (cell.z * view.TileCount.y + cell.y) * view.TileCount.x + cell.x;
    }

    static uint GetItemIndex(uint itemIndex)
    {
        return itemIndex % MaterialDecalViews[0].DecalCount;
    }
};

[shader("compute")] [numthreads(256, 1, 1)]
void CountScreenItems(uint3 thread : SV_DispatchThreadID)
{
    ScreenBinningCount<MaterialDecalBinningItem>(ScreenBinningThreadIndex(thread));
}

[shader("compute")] [numthreads(256, 1, 1)]
void ExpandScreenItems(uint3 thread : SV_DispatchThreadID)
{
    ScreenBinningExpand<MaterialDecalBinningItem>(ScreenBinningThreadIndex(thread));
}

[shader("compute")] [numthreads(256, 1, 1)]
void FillScreenItems(uint3 thread : SV_DispatchThreadID)
{
    ScreenBinningFill<MaterialDecalBinningItem>(ScreenBinningThreadIndex(thread));
}
