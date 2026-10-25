using SomeEngine.Animation.Pose;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Render.Features.Skinning;

public sealed partial class SkinRenderExtractor
{
    private static void ReadSkeleton(SkeletonInstance skeleton, ref JobResourceAccessBuilder resources)
    {
        for (SkeletonInstance? section = skeleton; section is not null; section = section.ParentInstance)
            resources.Read(section);
    }

    private readonly struct MeshSkinReads : IJobComponentResources<MeshSkin>
    {
        public static long GetVersion(in MeshSkin component) =>
            unchecked((long)component.Joints.SkeletonInstance.BindingVersion);
        public static void Collect(in MeshSkin component, ref JobResourceAccessBuilder resources) =>
            ReadSkeleton(component.Joints.SkeletonInstance, ref resources);
    }

    private readonly struct PoseTargetReads : IJobComponentResources<PoseTarget>
    {
        public static long GetVersion(in PoseTarget component) => unchecked((long)component.Skeleton.BindingVersion);
        public static void Collect(in PoseTarget component, ref JobResourceAccessBuilder resources) =>
            ReadSkeleton(component.Skeleton, ref resources);
    }

    private readonly struct GpuPoseTargetReads : IJobComponentResources<GpuPoseTarget>
    {
        public static long GetVersion(in GpuPoseTarget component) => unchecked((long)component.Skeleton.BindingVersion);
        public static void Collect(in GpuPoseTarget component, ref JobResourceAccessBuilder resources)
        {
            ReadSkeleton(component.Skeleton, ref resources);
            if (component.Controls is { } controls)
                resources.Read(controls.AsMemory());
            if (!component.Readbacks.IsEmpty)
                resources.Read(component.Readbacks);
        }
    }
}
