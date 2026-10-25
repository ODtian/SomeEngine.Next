using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly record struct DeviceGeometryCapacity(int Bodies, int Vertices, int Faces, int FaceIndices);

/// <summary>The entity's geometry resource and its index in that resource.</summary>
public readonly record struct DeviceGeometryBinding(DeviceCollisionGeometry Geometry, int Index) : IComponent;

[StructLayout(LayoutKind.Sequential, Size = 112)]
public struct DeviceGeometryInput
{
    public Vector3 Position;
    public float ConvexRadius;
    public Quaternion Rotation;
    public Vector3 HalfExtents;
    public float HalfHeight;
    public uint VertexStart;
    public uint VertexCount;
    public uint FaceStart;
    public uint FaceCount;
    public uint FaceIndexStart;
    public uint MaximumFaceVertices;
    public float Radius;
    public uint GeometryIndex;
    public Vector3 PlaneNormal;
    public float PlaneOffset;
    public uint GeometryType;
}

public interface IDeviceGeometryReader<TGeometry> : IJobComponentResources<TGeometry>
    where TGeometry : struct, IComponent, ICollisionGeometry<TGeometry>
{
    DeviceGeometryInput Read(in TGeometry geometry, in BodyPose pose, DeviceCollisionGeometry destination);
    CollisionBounds GetBounds(in TGeometry geometry, in BodyPose pose) => TGeometry.Bounds(in geometry, in pose);
    JobHandle Prepare(in PhysicsStepContext context, int rowsPerPacket, JobHandle dependency) => dependency;
}

public readonly struct DeviceSphereReader : IDeviceGeometryReader<SphereGeometry>
{
    public static void Collect(in SphereGeometry component, ref JobResourceAccessBuilder resources) { }
    public DeviceGeometryInput Read(in SphereGeometry geometry, in BodyPose pose, DeviceCollisionGeometry destination) =>
        new() { Position = pose.Position, Rotation = pose.Rotation, ConvexRadius = geometry.Radius };
}

public readonly struct DeviceBoxReader : IDeviceGeometryReader<BoxGeometry>
{
    public static void Collect(in BoxGeometry component, ref JobResourceAccessBuilder resources) { }
    public DeviceGeometryInput Read(in BoxGeometry geometry, in BodyPose pose, DeviceCollisionGeometry destination) =>
        new() { Position = pose.Position, Rotation = pose.Rotation, HalfExtents = geometry.HalfExtents };
}

public readonly struct DeviceCapsuleReader : IDeviceGeometryReader<CapsuleGeometry>
{
    public static void Collect(in CapsuleGeometry component, ref JobResourceAccessBuilder resources) { }
    public DeviceGeometryInput Read(in CapsuleGeometry geometry, in BodyPose pose, DeviceCollisionGeometry destination) =>
        new() { Position = pose.Position, Rotation = pose.Rotation, ConvexRadius = geometry.Radius, HalfHeight = geometry.HalfLength };
}

public readonly struct DeviceCylinderReader : IDeviceGeometryReader<CylinderGeometry>
{
    public static void Collect(in CylinderGeometry component, ref JobResourceAccessBuilder resources) { }
    public DeviceGeometryInput Read(in CylinderGeometry geometry, in BodyPose pose, DeviceCollisionGeometry destination) =>
        new() { Position = pose.Position, Rotation = pose.Rotation, Radius = geometry.Radius, HalfHeight = geometry.HalfHeight };
}

public readonly struct DevicePlaneReader(CollisionBounds candidateRegion) : IDeviceGeometryReader<PlaneGeometry>
{
    public static void Collect(in PlaneGeometry component, ref JobResourceAccessBuilder resources) { }
    public CollisionBounds GetBounds(in PlaneGeometry geometry, in BodyPose pose) => candidateRegion;
    public DeviceGeometryInput Read(in PlaneGeometry geometry, in BodyPose pose, DeviceCollisionGeometry destination) =>
        new() { Position = pose.Position, Rotation = pose.Rotation, PlaneNormal = geometry.Normal, PlaneOffset = geometry.Offset };
}

public readonly struct DeviceGeometryUploadResources : IJobComponentResources<DeviceGeometryBinding>
{
    public static void Collect(in DeviceGeometryBinding component, ref JobResourceAccessBuilder resources) =>
        resources.Write(component.Geometry.Upload, (long)component.Index * DeviceCollisionGeometry.InputStride,
            DeviceCollisionGeometry.InputStride);
}

public readonly struct DeviceHullReader : IDeviceGeometryReader<ConvexHullGeometry>
{
    public static void Collect(in ConvexHullGeometry component, ref JobResourceAccessBuilder resources) =>
        ConvexHullResources.Collect(in component, ref resources);

    public JobHandle Prepare(in PhysicsStepContext context, int rowsPerPacket, JobHandle dependency) =>
        new PrepareDeviceHullData().ScheduleParallel(context.World,
            new JobEntityScheduleOptions(rowsPerPacket: rowsPerPacket, filter: context.Bodies), dependency);

    public DeviceGeometryInput Read(in ConvexHullGeometry geometry, in BodyPose pose, DeviceCollisionGeometry destination)
    {
        DeviceHullRange range = destination.GetHullRange(geometry.Data);
        return new()
        {
            Position = pose.Position, Rotation = pose.Rotation, ConvexRadius = geometry.ConvexRadius,
            VertexStart = (uint)range.VertexStart, VertexCount = (uint)geometry.Data.Vertices.Length,
            FaceStart = (uint)range.FaceStart, FaceCount = (uint)geometry.Data.Faces.Length,
            FaceIndexStart = (uint)range.FaceIndexStart, MaximumFaceVertices = (uint)geometry.Data.MaximumFaceVertices,
        };
    }
}

public readonly struct DeviceHullUploadResources : IJobComponentResources<DeviceGeometryBinding>
{
    public static void Collect(in DeviceGeometryBinding component, ref JobResourceAccessBuilder resources) =>
        component.Geometry.CollectHullUploadAccesses(ref resources);
}

internal readonly partial struct PrepareDeviceHullData : IJobEntity
{
    public void Execute([JobResources<ConvexHullResources>] in ConvexHullGeometry geometry,
        [JobResources<DeviceHullUploadResources>] in DeviceGeometryBinding binding) =>
        binding.Geometry.RegisterHull(geometry.Data);
}
