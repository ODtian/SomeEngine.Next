// Production Cluster view/capacity ABI. Diagnostics use separate resources and shaders; no debug
// mode is allowed to alter traversal, LOD, or visibility through this uniform.
struct ClusterViewUniforms
{
    float4x4 ViewProj;
    float3 CameraPos;
    float LodThreshold;

    float LodScale;
    uint MaxCandidates;
    uint MaxTraversalDepth;
    uint InstanceCount;

    uint MaxPageFaults;
    uint ForceHardwareRaster;
    float SoftwareRasterAreaThreshold;
    uint CullingEnabled;

    float4x4 PrevViewProj;
    uint HasPrevHistory;
    uint HiZMipCount;
    float2 HiZInvSize;

    float4x4 View;
    float P00;
    float P11;
    uint ScreenWidth;
    uint ScreenHeight;

    float4x4 PrevView;
    float PrevP00;
    float PrevP11;
    float2 Pad3;
};
