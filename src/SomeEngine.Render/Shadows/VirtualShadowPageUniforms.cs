using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Shadows;

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 80)]
public struct VirtualShadowReceiverProjection
{
    public Vector4 WorldToClipW;
    public float ScreenPixelRadius;
    public uint Pad0;
    public uint Pad1;
    public uint Pad2;
    public Vector4 WorldToViewDepth;
    public CameraDepthOfField DepthOfField;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 224)]
public struct VirtualShadowPageDemandUniforms
{
    public Matrix4x4 ClipToWorld;
    public uint ReceiverWidth;
    public uint ReceiverHeight;
    public uint ViewCount;
    public uint PageTableEntryCount;
    public uint DirectionalLightCount;
    public uint DirectionalClipmapLevels;
    public uint PointLightCount;
    public uint PointLightBufferOffset;
    public uint PointViewOffset;
    public uint SpotLightCount;
    public uint SpotLightBufferOffset;
    public uint SpotViewOffset;
    public uint ReceiverViewIndex;
    public uint FrameIndex;
    public float PageDilationBorderPages;
    public float NormalBias;
    public uint AdditionalReceiverLayerCount;
    public uint CullBackfacingPixels;
    public float FirstPersonPixelRequestBias;
    public int FirstPersonPixelRequestMinLevel;
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
    public uint Pad0;
    public uint HierarchyWordsPerView;
    public uint MipLevelCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 24)]
public struct VirtualShadowStateInitUniforms
{
    public uint PageTableEntryCount;
    public uint MaxPhysicalPages;
    public uint RequestEpochEntryCount;
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
    public uint Pad2;
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
    public uint Pad1;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public struct VirtualShadowPageClearUniforms
{
    public uint PageSize;
    public uint PhysicalPagesPerRow;
    public uint AtlasSize;
    public uint Pad0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 144)]
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
    public uint Pad0;
    public uint Pad1;
    public VirtualShadowReceiverProjection ReceiverProjection;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public struct VirtualShadowPageHiZUniforms
{
    public uint PageSize;
    public uint PhysicalPagesPerRow;
    public uint SourceMip;
    public uint DestinationMip;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct VirtualShadowDynamicResolutionUniforms
{
    public uint MaxPhysicalPages;
    public uint PadRasterWork;
    public float PagePoolLoadStart;
    public float MaxDirectionalLodBias;
    public float MaxLocalLodBias;
    public uint Enabled;
    public uint Pad0;
    public uint Pad1;
}
