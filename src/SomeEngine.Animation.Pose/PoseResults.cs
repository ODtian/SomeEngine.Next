using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation.Pose;

/// <summary>A local-pose result number in a shared graph definition.</summary>
public readonly record struct PoseResultId(int Value);

/// <summary>The instance buffer and bone section bound to a graph result at installation.</summary>
public readonly record struct PoseResultReference(Entity Entity, int Index, PoseRange Range);

/// <summary>One bone transform in an instance's continuous node-result buffer.</summary>
[BufferCapacity(1)]
public struct LocalPose : IBufferElement
{
    public BoneTransform Transform;

    public static Span<BoneTransform> Transforms(Span<LocalPose> values) =>
        MemoryMarshal.Cast<LocalPose, BoneTransform>(values);

    public static ReadOnlySpan<BoneTransform> Transforms(ReadOnlySpan<LocalPose> values) =>
        MemoryMarshal.Cast<LocalPose, BoneTransform>(values);
}

/// <summary>The bone section, sample time, cumulative cycle phase and graph weight produced by one node result.</summary>
[BufferCapacity(4)]
public struct PoseResult : IBufferElement
{
    public PoseRange Range;
    public double Time;
    public double Phase;
    public float Weight;
    public uint Starts;
    public ulong Version;
    public RootMotionDelta RootMotion;
}

/// <summary>A skeleton instance and the resolved result selected for its final local pose.</summary>
public struct PoseTarget : IComponent
{
    public SkeletonInstance Skeleton;
    public PoseResultReference Output;
    public ulong Version;
}

public static partial class PoseAnimation
{
    /// <summary>Creates an instance data entity with one continuous bone section per graph result.</summary>
    public static Entity CreateTarget(World world, SkeletonInstance skeleton, int resultCount = 1,
        PoseResultId output = default)
    {
        Entity target = world.CreateEntity();
        world.AddBuffer<LocalPose>(target);
        world.AddBuffer<PoseResult>(target);
        var initial = (Skeleton: skeleton.Skeleton, Count: resultCount);
        world.ExecuteBufferWrite<LocalPose, (Skeleton Skeleton, int Count)>(target, ref initial,
            static (values, ref initial) =>
            {
                values.EnsureCapacity(initial.Skeleton.BoneCount * initial.Count);
                for (int result = 0; result < initial.Count; result++)
                    foreach (BoneTransform bone in initial.Skeleton.ReferencePose)
                        values.Add(new LocalPose { Transform = bone });
            });
        world.ExecuteBufferWrite<PoseResult, (Skeleton Skeleton, int Count)>(target, ref initial,
            static (results, ref initial) =>
            {
                results.EnsureCapacity(initial.Count);
                for (int result = 0; result < initial.Count; result++)
                    results.Add(new PoseResult
                    {
                        Range = new PoseRange(result * initial.Skeleton.BoneCount, initial.Skeleton.BoneCount),
                        Weight = 1,
                        RootMotion = RootMotionDelta.Identity,
                    });
            });
        world.Add(target, new PoseTarget { Skeleton = skeleton, Output = Resolve(world, target, output) });
        CpuBoneDemands.For(world).Changed = true;
        return target;
    }

    /// <summary>Resolves a shared result number once, while installing a concrete leaf.</summary>
    public static PoseResultReference Resolve(World world, Entity target, PoseResultId result)
    {
        var binding = (Index: result.Value, Range: default(PoseRange));
        world.ExecuteBufferRead<PoseResult, (int Index, PoseRange Range)>(target, ref binding,
            static (results, ref binding) => binding.Range = results[binding.Index].Range);
        return new(target, result.Value, binding.Range);
    }

    /// <summary>Reads a produced result's timing, weight, motion and version.</summary>
    public static PoseResult ReadResult(World world, in PoseResultReference result)
    {
        var read = (Index: result.Index, Result: default(PoseResult));
        world.ExecuteBufferRead<PoseResult, (int Index, PoseResult Result)>(result.Entity, ref read,
            static (results, ref read) => read.Result = results[read.Index]);
        return read.Result;
    }
}
