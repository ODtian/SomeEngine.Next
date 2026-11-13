using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Physics;

namespace SomeEngine.Animation.Pose;

public sealed partial class DevicePosePhysics
{
    internal uint WriteDefinition(BinaryWriter writer, Entity target, int step, int boneStart,
        GpuRigDescendants descendants)
    {
        Entity chain = _chains[(target, step)];
        GpuXpbdChain definition = _world.Read<GpuPoseChain>(chain).Chain;
        using RelationRead bodies = ConstraintBodies.GetBodies(_world, chain);
        using var lengths = _world.ReadBuffer<GpuPoseLengthLink>(chain);
        using var bends = _world.ReadBuffer<GpuPoseBendLink>(chain);
        var points = new PointDefinition[bodies.Count];
        for (int point = 0; point < points.Length; point++)
        {
            int bone = definition.Bones[point].Bone;
            var range = descendants.Write(boneStart + bone);
            uint length = uint.MaxValue;
            if (point > 0)
            {
                length = (uint)_world.Read<DeviceConstraintBinding<DeviceLengthConstraint>>(lengths[point - 1].Length.Entity).Index;
            }
            points[point] = new((uint)(boneStart + bone), range.Offset, range.Count,
                (uint)_world.Read<DeviceBodyBinding>(bodies.Entries[point].Other).Index,
                point == 0 ? default : definition.Skeleton.ReferencePose[bone].Translation, length);
        }
        Align(writer);
        uint first = (uint)writer.BaseStream.Position;
        foreach (PointDefinition point in points)
        {
            writer.Write(point.Bone); writer.Write(point.Descendants); writer.Write(point.Count); writer.Write(point.Body);
            writer.Write(point.Rest.X); writer.Write(point.Rest.Y); writer.Write(point.Rest.Z); writer.Write(point.Length);
        }
        uint bendStart = (uint)writer.BaseStream.Position;
        foreach (GpuPoseBendLink link in bends.AsSpan())
        {
            writer.Write((uint)_world.Read<DeviceConstraintBinding<DeviceBendConstraint>>(link.Bend).Index);
        }
        Align(writer);
        uint offset = (uint)writer.BaseStream.Position;
        writer.Write(first); writer.Write((uint)points.Length); writer.Write(bendStart); writer.Write((uint)bends.Count);
        writer.Write(definition.Acceleration.X); writer.Write(definition.Acceleration.Y); writer.Write(definition.Acceleration.Z); writer.Write(definition.Step);
        writer.Write(definition.Damping); writer.Write((uint)definition.Iterations); writer.Write(0u); writer.Write(0u);
        return offset;
    }

    private static void Align(BinaryWriter writer)
    { while (writer.BaseStream.Position % 16 != 0) writer.Write((byte)0); }
    private readonly record struct PointDefinition(uint Bone, uint Descendants, uint Count, uint Body, System.Numerics.Vector3 Rest, uint Length);
}
