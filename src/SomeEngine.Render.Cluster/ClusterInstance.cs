using SomeEngine.Render.Instances;

namespace SomeEngine.Render.Cluster;

/// <summary>
/// Cluster's declarations in the composable render-instance ABI. Cluster geometry and material
/// binding are separate contributors: the Cluster geometry contributor writes only
/// <see cref="GeometryLayout"/>,
/// while a material system chooses whether <see cref="MaterialSlotLayout"/> is batch-shared or
/// per-instance. <see cref="InstanceLayout"/> is only their exact shader-facing composition.
/// </summary>
public static class ClusterRenderFeature
{
    public static RenderInstancePropertyKey BvhRootKey { get; } =
        new("someengine.cluster.bvh_root");

    public static RenderInstancePropertyKey MaterialSlotOffsetKey { get; } =
        new("someengine.cluster.material_slot_offset");

    public static RenderInstancePropertyKey BoundsExpansionKey { get; } =
        new("someengine.cluster.bounds_expansion");

    public const uint MissingBvhRoot = uint.MaxValue;
    public const uint MissingMaterialSlots = uint.MaxValue;

    private static RenderInstancePropertyEncoding UInt32Encoding { get; } = new(
        "someengine.render.linear.uint32.v1",
        valueSize: 4,
        storageAlignment: 4,
        storageStride: 4,
        metadataWordCount: 1);

    private static RenderInstancePropertyEncoding Float32Encoding { get; } = new(
        "someengine.render.linear.float32.v1",
        valueSize: 4,
        storageAlignment: 4,
        storageStride: 4,
        metadataWordCount: 1);

    public static RenderInstancePropertyLayout GeometryLayout { get; } = BuildGeometryLayout();

    public static RenderInstancePropertyLayout MaterialSlotLayout { get; } = BuildMaterialSlotLayout();

    public static RenderInstancePropertyLayout InstanceLayout { get; } =
        RenderInstancePropertyLayout.Compose(GeometryLayout, MaterialSlotLayout);

    private static RenderInstancePropertyLayout BuildGeometryLayout()
    {
        var builder = new RenderInstancePropertyLayoutBuilder();
        RenderInstanceTransformProperties.Register(builder);
        _ = builder.Register<uint>(
            "SomeEngine.Render.Cluster",
            BvhRootKey,
            UInt32Encoding);
        _ = builder.Register<float>(
            "SomeEngine.Render.Cluster",
            BoundsExpansionKey,
            Float32Encoding);
        return builder.Freeze();
    }

    private static RenderInstancePropertyLayout BuildMaterialSlotLayout()
    {
        var builder = new RenderInstancePropertyLayoutBuilder();
        _ = builder.Register<uint>(
            "SomeEngine.Render.Cluster.MaterialBinding",
            MaterialSlotOffsetKey,
            UInt32Encoding);
        return builder.Freeze();
    }
}

/// <summary>Mesh preparation state shared by every Cluster entity that references a handle.</summary>
public sealed record ClusterMeshCacheDiagnostics(
    int RegisteredMeshes,
    int PublishedMeshes);

/// <summary>Result of resolving the unique mesh handles visible in one RenderWorld scan.</summary>
public readonly record struct ClusterMeshPrepareResult(
    int ReferencedMeshes,
    int RegisteredMeshes,
    int UnresolvedMeshes);
