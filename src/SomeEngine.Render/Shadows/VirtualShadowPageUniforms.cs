using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Shadows;

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 96)]
public struct VirtualShadowReceiverProjection
{
    public Vector4 WorldToClipW;
    // Keep the shared uniform layout valid for both DXIL and SPIR-V.
    public Vector3 WorldTranslation;
    public float ScreenPixelRadius;
    public Vector4 WorldToViewDepth;
    public CameraDepthOfField DepthOfField;
    private uint _depthOfFieldPadding;
    public Vector3 CameraPosition;
    public float CotanHalfFieldOfView;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 160)]
public struct VirtualShadowPageDemandUniforms
{
    public uint ViewCount;
    public uint PageTableEntryCount;
    public uint FrameIndex;
    public uint PointLightBufferOffset;
    public uint PointLightCount;
    public uint PointViewOffset;
    public uint SpotLightCount;
    public uint SpotLightBufferOffset;
    public uint SpotViewOffset;
    public float PageDilationBorderPages;
    public float NormalBias;
    public uint CullBackfacingPixels;
    public float FirstPersonPixelRequestBias;
    public int FirstPersonPixelRequestMinLevel;
    private uint _padding0, _padding1;
    public VirtualShadowReceiverProjection ReceiverProjection;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public struct VirtualShadowCoarsePageDemandUniforms
{
    public uint ViewCount;
    public uint PageTableEntryCount;
    public uint FrameIndex;
    public uint Pad0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct VirtualShadowPageAllocationUniforms
{
    public uint PageTableEntryCount;
    public uint PageTableEntriesPerView;
    public uint MaxPhysicalPages;
    public uint ViewCount;
    public uint FrameIndex;
    public int DeferredInvalidationBudget;
    public uint HierarchyWordsPerView;
    public uint MipLevelCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 24)]
public struct VirtualShadowStateInitUniforms
{
    public uint PageTableEntryCount;
    public uint MaxPhysicalPages;
    public uint RequestFlagEntryCount;
    public uint Pad0;
    public uint Pad1;
    public uint Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct VirtualShadowInvalidationUniforms
{
    public uint ChangedInstanceCount;
    public uint ViewCount;
    public uint PageTableEntryCount;
    public uint PageDilation;
    public uint FrameIndex;
    public uint HierarchyWordsPerView;
    public uint MipLevelCount;
    public uint DeferredChangedInstanceCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct VirtualShadowPagePoolUniforms
{
    public uint MaxPhysicalPages;
    public uint PageTableEntryCount;
    public uint FrameIndex;
    public uint PageListPhase;
    public uint PageTableEntriesPerView;
    public uint HierarchyWordsPerView;
    public uint MipLevelCount;
    public uint ViewCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
public struct VirtualShadowRasterWorkUniforms
{
    public uint ViewCount;
    public uint MipLevelCount;
    public uint PageTableEntriesPerView;
    public uint HierarchyWordsPerView;
    public uint ActivePageCapacity;
    public uint PageSize;
    public uint PageHiZMipCount;
    public uint PhysicalPagesPerRow;
    public uint InstanceGroupCount;
    public uint HasPreviousHistory;
    public uint WorkFeedbackEnabled;
    public uint StaticHiZLayerOffset;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public struct VirtualShadowPageClearUniforms
{
    public uint PageSize;
    public uint PhysicalPagesPerRow;
    public uint AtlasSize;
    public uint Pad0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct VirtualShadowRaySettings
{
    public uint RayCount;
    public uint SamplesPerRay;
    public uint AdaptiveRayCount;
    public float ExtrapolateMaxSlope;
    public float TexelDitherScale;
    public float RayLengthScale;
    public float MaxRayAngle;
    public float MaxSlopeBias;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 240)]
public struct VirtualShadowSamplingUniforms
{
    public uint ViewCount;
    public uint DirectionalLightCount;
    public uint DirectionalClipmapLevels;
    public uint PointLightCount;
    public uint PointLightBufferOffset;
    public uint PointViewOffset;
    public uint SpotLightCount;
    public uint SpotLightBufferOffset;
    public uint SpotViewOffset;
    public uint BlockerSampleCount;
    public uint FilterSampleCount;
    public float BlockerSearchRadiusTexels;
    public float MaxFilterRadiusTexels;
    public float NormalBias;
    public uint FrameIndex;
    public uint FirstPersonViewOffset;
    public VirtualShadowReceiverProjection ReceiverProjection;
    public VirtualShadowRaySettings DirectionalRays;
    public VirtualShadowRaySettings LocalRays;
    public uint CullBackfacingPixels;
    public float ScreenRayLength;
    private uint _receiverPadding1, _receiverPadding2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct VirtualShadowPageHiZUniforms
{
    public uint PageSize;
    public uint PhysicalPagesPerRow;
    public uint SourceMip;
    public uint DestinationMip;
    public uint StaticLayerOffset;
    private uint _padding0, _padding1, _padding2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct VirtualShadowDynamicResolutionUniforms
{
    public uint MaxPhysicalPages;
    public uint PadRasterWork;
    public float PagePoolLoadStart;
    public float MaxLodBias;
    public uint PadLodBias;
    public uint Enabled;
    public uint Pad0;
    public uint Pad1;
}
