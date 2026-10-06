using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Exact managed mirror of assets/Shaders/cluster_view.slang.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = SizeInBytes)]
internal struct ClusterViewUniforms
{
    internal const int SizeInBytes = ClusterRenderContext.ViewSizeInBytes;

    internal Matrix4x4 ViewProj;
    internal Vector3 CameraPos;
    internal float LodThreshold;

    internal float GeometryLodScale;
    internal uint MaxCandidates;
    internal float CullingRadius;
    internal uint InstanceCount;

    internal uint MaxPageFaults;
    internal uint RasterMode;
    internal float SoftwareRasterEdgeThreshold;
    internal uint CullingEnabled;

    internal Matrix4x4 PrevViewProj;
    internal uint HasPrevHistory;
    internal uint HiZMipCount;
    internal Vector2 HiZInvSize;

    internal Matrix4x4 View;
    internal float P00;
    internal float P11;
    internal uint ScreenWidth;
    internal uint ScreenHeight;

    internal Matrix4x4 PrevView;
    internal float PrevP00;
    internal float PrevP11;
    internal float TessellationEdgePixels;
    internal uint StreamingPriorityCategory;

    internal Matrix4x4 ClipToWorld;

    internal uint TargetPolicy;
    internal uint TargetViewIndex;
    internal uint CullingProjectionIndex;
    internal float NearPlane;
    internal Matrix4x4 GeometryViewProj;
    internal Vector3 WorldOriginHigh;
    internal float OriginHighPadding;
    internal Vector3 WorldOriginLow;
    internal float OriginLowPadding;
    internal Vector3 PreviousWorldOriginHigh;
    internal float PreviousOriginHighPadding;
    internal Vector3 PreviousWorldOriginLow;
    internal float PreviousOriginLowPadding;

    internal static ClusterViewUniforms Create(
        in RenderView source,
        ClusterPipelineOptions options,
        ClusterViewRasterMode rasterMode,
        bool cullingEnabled,
        int instanceCount,
        int pageFaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (source.ViewportWidth == 0 || source.ViewportHeight == 0)
            throw new ArgumentException("A render view requires a non-empty viewport.", nameof(source));
        if (!float.IsFinite(source.GeometryLodScale) || source.GeometryLodScale <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(source));
        ArgumentOutOfRangeException.ThrowIfNegative(instanceCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageFaultCapacity);
        if (!IsFinite(source.View) || !IsFinite(source.Projection))
            throw new ArgumentException("Render-view matrices must be finite.", nameof(source));
        if (!Matrix4x4.Invert(source.View, out Matrix4x4 worldFromView))
            throw new ArgumentException("The render-view matrix must be invertible.", nameof(source));

        Matrix4x4 viewProjection = source.View * source.Projection;
        if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 clipToWorld))
            throw new ArgumentException("The render-view projection must be invertible.", nameof(source));
        Vector3 cameraPosition = new(worldFromView.M41, worldFromView.M42, worldFromView.M43);
        source.WorldOrigin.Split(out Vector3 originHigh, out Vector3 originLow);
        if (!IsFinite(viewProjection)
            || !IsFinite(cameraPosition))
        {
            throw new ArgumentException(
                "The render view cannot be represented by the Cluster uniform contract.",
                nameof(source));
        }

        return new ClusterViewUniforms
        {
            ViewProj = viewProjection,
            CameraPos = cameraPosition,
            LodThreshold = options.LodThreshold,
            GeometryLodScale = source.GeometryLodScale,
            MaxCandidates = options.MaxCandidates,
            InstanceCount = checked((uint)instanceCount),
            MaxPageFaults = checked((uint)pageFaultCapacity),
            RasterMode = (uint)rasterMode,
            SoftwareRasterEdgeThreshold = options.SoftwareRasterEdgeThreshold,
            CullingEnabled = cullingEnabled ? 1u : 0u,
            PrevViewProj = viewProjection,
            HasPrevHistory = 0,
            HiZMipCount = 0,
            HiZInvSize = default,
            View = source.View,
            P00 = source.Projection.M11,
            P11 = source.Projection.M22,
            ScreenWidth = source.ViewportWidth,
            ScreenHeight = source.ViewportHeight,
            PrevView = source.View,
            PrevP00 = source.Projection.M11,
            PrevP11 = source.Projection.M22,
            TessellationEdgePixels = 0.0f,
            StreamingPriorityCategory = source.StreamingPriorityCategory,
            ClipToWorld = clipToWorld,
            TargetPolicy = 0u,
            TargetViewIndex = 0u,
            NearPlane = source.Projection.M43 / source.Projection.M33,
            GeometryViewProj = source.View * (source.GeometryProjection ?? source.Projection),
            WorldOriginHigh = originHigh,
            WorldOriginLow = originLow,
            PreviousWorldOriginHigh = originHigh,
            PreviousWorldOriginLow = originLow,
        };
    }

    private static bool IsFinite(in Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12)
        && float.IsFinite(value.M13) && float.IsFinite(value.M14)
        && float.IsFinite(value.M21) && float.IsFinite(value.M22)
        && float.IsFinite(value.M23) && float.IsFinite(value.M24)
        && float.IsFinite(value.M31) && float.IsFinite(value.M32)
        && float.IsFinite(value.M33) && float.IsFinite(value.M34)
        && float.IsFinite(value.M41) && float.IsFinite(value.M42)
        && float.IsFinite(value.M43) && float.IsFinite(value.M44);

    private static bool IsFinite(in Vector3 value) =>
        float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z);
}

internal enum ClusterViewRasterMode : uint
{
    Adaptive = 0,
    Hardware = 1,
    Software = 2,
}
