using System.Buffers;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;

namespace SomeEngine.Render.Cluster;

internal readonly struct ClusterGeometryProducer : IRenderInstanceProducer
{
    private readonly ResolvedRenderInstanceProperty<RenderTransform> _currentTransform;
    private readonly ResolvedRenderInstanceProperty<RenderPreviousTransform> _previousTransform;
    private readonly ResolvedRenderInstanceProperty<uint> _bvhRoot;
    private readonly ResolvedRenderInstanceProperty<float> _boundsExpansion;
    private readonly ResolvedRenderInstanceProperty<RenderDeformBounds> _deformBounds;

    private readonly ResolvedRenderInstanceProperty<uint> _depthCacheClass;
    private readonly ResolvedRenderInstanceProperty<FirstPersonMeshData> _firstPerson;

    public ClusterGeometryProducer()
    {
        RenderInstancePropertyLayout clusterProperties = ClusterRenderFeature.GeometryLayout;
        _currentTransform = clusterProperties.Resolve<RenderTransform>(
            RenderInstanceTransformProperties.CurrentTransformKey);
        _previousTransform = clusterProperties.Resolve<RenderPreviousTransform>(
            RenderInstanceTransformProperties.PreviousTransformKey);
        _bvhRoot = clusterProperties.Resolve<uint>(ClusterRenderFeature.BvhRootKey);
        _boundsExpansion = clusterProperties.Resolve<float>(ClusterRenderFeature.BoundsExpansionKey);
        _deformBounds = clusterProperties.Resolve<RenderDeformBounds>(ClusterRenderFeature.DeformBoundsKey);
        _depthCacheClass = clusterProperties.Resolve<uint>(ClusterRenderFeature.DepthCacheClassKey);
        _firstPerson = clusterProperties.Resolve<FirstPersonMeshData>(ClusterRenderFeature.FirstPersonKey);
    }

    public RenderInstancePropertyLayout Properties => ClusterRenderFeature.GeometryLayout;

    public RenderInstanceChanges GetChanges(
        ReadOnlyQueryPacket packet,
        uint lastSystemVersion) =>
        packet.ChangedSince<RenderTransform>(lastSystemVersion)
        || packet.ChangedSince<RenderPreviousTransform>(lastSystemVersion)
        || MeshChanged(packet, lastSystemVersion)
            ? RenderInstanceChanges.Values
            : RenderInstanceChanges.None;

    internal static bool MeshChanged(ReadOnlyQueryPacket packet, uint lastSystemVersion) =>
        packet.ChangedSince<RenderClusterGeometry>(lastSystemVersion)
        || packet.Has<RenderMesh>() && packet.ChangedSince<RenderMesh>(lastSystemVersion)
        || packet.Has<RenderDeformBounds>() && packet.ChangedSince<RenderDeformBounds>(lastSystemVersion);

    public void Bind(RenderInstanceWriteSlice destination)
        => Bind(destination, true, true, true);

    internal void Bind(RenderInstanceWriteSlice destination, bool current, bool previous, bool mesh)
    {
        if (current) destination.BindPerInstance(_currentTransform);
        if (previous) destination.BindPerInstance(_previousTransform);
        if (mesh)
        {
            destination.BindPerInstance(_depthCacheClass);
            destination.BindPerInstance(_firstPerson);
            destination.BindPerInstance(_bvhRoot);
            destination.BindPerInstance(_boundsExpansion);
            destination.BindPerInstance(_deformBounds);
        }
    }

    public void Write(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet)
        => Write(destination, packet, true, true, true);

    internal void Write(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet,
        bool current, bool previous, bool mesh)
    {
        if (current) destination.Write(_currentTransform, packet.Read<RenderTransform>());
        if (previous) destination.Write(_previousTransform, packet.Read<RenderPreviousTransform>());
        if (!mesh) return;
        bool hasMesh = packet.TryRead<RenderMesh>(out var meshes);
        for (int row = 0; row < packet.Count; row++)
        {
            destination.Write(_depthCacheClass, row, hasMesh ? (uint)meshes[row].DepthCacheClass : 0u);
            destination.Write(_firstPerson, row, hasMesh ? meshes[row].FirstPerson : default);
        }

        ReadOnlySpan<RenderClusterGeometry> geometries = packet.Read<RenderClusterGeometry>();
        uint[]? rentedRoots = null;
        float[]? rentedExpansions = null;
        scoped Span<uint> roots;
        scoped Span<float> expansions;
        if (packet.Count <= 1_024)
        {
            roots = stackalloc uint[packet.Count];
            expansions = stackalloc float[packet.Count];
        }
        else
        {
            rentedRoots = ArrayPool<uint>.Shared.Rent(packet.Count);
            rentedExpansions = ArrayPool<float>.Shared.Rent(packet.Count);
            roots = rentedRoots.AsSpan(0, packet.Count);
            expansions = rentedExpansions.AsSpan(0, packet.Count);
        }
        try
        {
            for (int row = 0; row < geometries.Length; row++)
            {
                roots[row] = geometries[row].Geometry.RootBvh;
                expansions[row] = geometries[row].BoundsExpansion;
            }
            destination.Write(_bvhRoot, roots);
            destination.Write(_boundsExpansion, expansions);
            if (packet.TryRead<RenderDeformBounds>(out var bounds)) destination.Write(_deformBounds, bounds);
            else
                for (int row = 0; row < packet.Count; row++) destination.Write(_deformBounds, row, default(RenderDeformBounds));
        }
        finally
        {
            if (rentedRoots is not null)
                ArrayPool<uint>.Shared.Return(rentedRoots);
            if (rentedExpansions is not null)
                ArrayPool<float>.Shared.Return(rentedExpansions);
        }
    }
}
