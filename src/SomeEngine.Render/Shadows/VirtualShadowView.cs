using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Shadows;

public enum VirtualShadowLightKind : uint
{
    Directional = 0,
    Point = 1,
    Spot = 2,
}

/// <summary>
/// One shadow-caster projection and virtual-atlas mapping. A geometry pipeline may consume a span
/// of these records in one multi-view submission.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = SizeInBytes)]
public struct VirtualShadowView
{
    public const int SizeInBytes = 448;
    public const int PageTableEntrySizeInBytes = 4;

    private Matrix4x4 _lightViewProjection;
    public Matrix4x4 LightViewProjection
    {
        readonly get => _lightViewProjection;
        set
        {
            _lightViewProjection = value;
            Matrix4x4 clipToUv = new(.5f, 0, 0, 0, 0, -.5f, 0, 0,
                0, 0, 1, 0, .5f, .5f, 0, 1);
            _shadowUv = value * clipToUv;
            Matrix4x4.Invert(_shadowUv, out Matrix4x4 uvToWorld);
            _shadowUvNormal = Matrix4x4.Transpose(uvToWorld);
        }
    }
    public uint VirtualResolution;
    public uint PageSize;
    public uint AtlasSize;
    public uint PhysicalPagesPerRow;
    public uint MaxPhysicalPages;
    public int VirtualPageOriginX;
    public float DepthBias;
    public uint PageTableOffset;
    public uint CacheGeneration;
    public int VirtualPageOriginY;
    public int ClipmapLevel;
    public uint LightIndex;
    public Vector3 ClipmapWorldOrigin;
    public float ResolutionLodBias;
    public int FirstClipmapLevel;
    public uint ClipmapLevelCount;
    public uint MipLevelCount;
    public uint MinMipLevel;
    public uint Flags;
    public VirtualShadowLightKind LightKind;
    public uint FaceIndex;
    /// <summary>Scene distance represented by one native centimeter.</summary>
    public float WorldUnitsPerCentimeter;
    public Vector3 LightPosition;
    public float LightRange;
    public float GeometryLodScale;
    public float MaxPixelsPerEdge;
    public float MinPixelsPerEdgeHardware;
    public float DicingRate;
    public float DepthCenterZ;
    public float DepthRadius;
    public Vector2 DepthCullRange;
    public Vector3 WorldOriginHigh;
    public uint ReceiverMaskOriginX;
    public Vector3 WorldOriginLow;
    public uint ReceiverMaskOriginY;
    /// <summary>Corner offset in level radii from the common coarsest-level reference grid.</summary>
    public int ClipmapCornerRelativeOffsetX;
    public int ClipmapCornerRelativeOffsetY;
    /// <summary>Absolute level-zero page address before the GPU ring tag is reduced to 32 bits.</summary>
    public long WorldPageOriginX;
    public long WorldPageOriginY;
    public float PageDilationBorder;
    private float AddressPadding;
    public Vector3 LightDirection;
    /// <summary>Local emitter radius in engine world units.</summary>
    public float SourceRadius;
    /// <summary>Directional emitter's full angular diameter in radians.</summary>
    public float SourceAngleRadians;
    public float ProjectionDepthScale;
    public float ProjectionDepthOffset;
    public float TexelDitherScale;
    /// <summary>Translation from the current world data into this view's near coordinates.</summary>
    public Vector3 WorldTranslation;
    private float TranslationPadding;
    private Matrix4x4 _shadowUvNormal;
    public readonly Matrix4x4 ShadowUvNormal => _shadowUvNormal;
    private Matrix4x4 _shadowUv;
    public readonly Matrix4x4 ShadowUv => _shadowUv;

    /// <summary>Projects a position expressed relative to the current world data origin.</summary>
    public readonly Vector4 ProjectPosition(Vector3 position) =>
        Vector4.Transform(new Vector4(position + WorldTranslation, 1), _lightViewProjection);
}

public static class VirtualShadowViewFlags
{
    public const uint CoarseClipLevel = 1u << 0;
    public const uint ForceCacheDynamicCoarse = 1u << 1;
    public const uint UseReceiverMask = 1u << 2;
    public const uint PreviousProjectionValid = 1u << 3;
    public const uint OrthographicReceiver = 1u << 4;
    public const uint EvenMipLevels = 1u << 5;
    public const uint OddMipLevels = 1u << 6;
    /// <summary>The local shadow uses the last mip as its one-page representation.</summary>
    public const uint SinglePage = 1u << 7;
    public const uint DistantLight = 1u << 8;
    public const uint CachedDistantLight = 1u << 9;
    public const uint PrefilteredDistant = 1u << 10;
    public const uint FirstPersonShadow = 1u << 11;
}
