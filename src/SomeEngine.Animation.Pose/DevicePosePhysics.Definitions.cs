using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Physics;

namespace SomeEngine.Animation.Pose;

public sealed partial class DevicePosePhysics
{
    internal uint WriteDefinition(BinaryWriter writer, Entity target, int step, int boneStart,
        Func<int, (uint Offset, uint Count)> descendants)
    {
        var data = new ChainDefinition(_chains[(target, step)], writer, boneStart, descendants);
        _world.ExecuteReadQuery(_chainRows, ref data, static (scoped QueryCursor rows, ref ChainDefinition data) =>
        {
            rows.TryGetRow(data.Entity, out QueryRow row);
            GpuXpbdChain definition = row.Read<GpuPoseChain>().Chain;
            ReadOnlySpan<ConstraintBodyLink> bodies = row.ReadBuffer<ConstraintBodyLink>().AsSpan();
            ReadOnlySpan<GpuPoseLengthLink> lengths = row.ReadBuffer<GpuPoseLengthLink>().AsSpan();
            ReadOnlySpan<GpuPoseBendLink> bends = row.ReadBuffer<GpuPoseBendLink>().AsSpan();
            var points = new PointDefinition[bodies.Length];
            for (int point = 0; point < points.Length; point++)
            {
                rows.TryGetRow(ConstraintBodies.GetBody(in bodies[point], rows), out QueryRow body);
                int bone = definition.Bones[point].Bone;
                var range = data.Descendants(data.BoneStart + bone);
                uint length = uint.MaxValue;
                if (point > 0)
                {
                    rows.TryGetRow(lengths[point - 1].Length.Entity, out QueryRow constraint);
                    length = (uint)constraint.Read<DeviceConstraintBinding<DeviceLengthConstraint>>().Index;
                }
                points[point] = new((uint)(data.BoneStart + bone), range.Offset, range.Count,
                    (uint)body.Read<DeviceBodyBinding>().Index,
                    point == 0 ? default : definition.Skeleton.ReferencePose[bone].Translation, length);
            }
            Align(data.Writer);
            uint first = (uint)data.Writer.BaseStream.Position;
            foreach (PointDefinition point in points)
            {
                data.Writer.Write(point.Bone); data.Writer.Write(point.Descendants); data.Writer.Write(point.Count); data.Writer.Write(point.Body);
                data.Writer.Write(point.Rest.X); data.Writer.Write(point.Rest.Y); data.Writer.Write(point.Rest.Z); data.Writer.Write(point.Length);
            }
            uint bendStart = (uint)data.Writer.BaseStream.Position;
            foreach (GpuPoseBendLink link in bends)
            {
                rows.TryGetRow(link.Bend, out QueryRow constraint);
                data.Writer.Write((uint)constraint.Read<DeviceConstraintBinding<DeviceBendConstraint>>().Index);
            }
            Align(data.Writer);
            data.Offset = (uint)data.Writer.BaseStream.Position;
            data.Writer.Write(first); data.Writer.Write((uint)points.Length); data.Writer.Write(bendStart); data.Writer.Write((uint)bends.Length);
            data.Writer.Write(definition.Acceleration.X); data.Writer.Write(definition.Acceleration.Y); data.Writer.Write(definition.Acceleration.Z); data.Writer.Write(definition.Step);
            data.Writer.Write(definition.Damping); data.Writer.Write((uint)definition.Iterations); data.Writer.Write(0u); data.Writer.Write(0u);
        });
        return data.Offset;
    }

    private static void Align(BinaryWriter writer)
    { while (writer.BaseStream.Position % 16 != 0) writer.Write((byte)0); }
    private readonly record struct PointDefinition(uint Bone, uint Descendants, uint Count, uint Body, System.Numerics.Vector3 Rest, uint Length);
    private struct ChainDefinition(Entity entity, BinaryWriter writer, int boneStart, Func<int, (uint Offset, uint Count)> descendants)
    {
        internal readonly Entity Entity = entity;
        internal readonly BinaryWriter Writer = writer;
        internal readonly int BoneStart = boneStart;
        internal readonly Func<int, (uint Offset, uint Count)> Descendants = descendants;
        internal uint Offset;
    }
}
