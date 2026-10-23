# Skeleton Instances

## Data and public API

`PoseStorage` holds continuous arrays of local `BoneTransform` values and model-space `Matrix4x4` values. A world can retain one storage and create its skeleton instances through `CreateInstance(Skeleton)`. The optional constructor capacity reserves space in bones.

`SkeletonInstance` directly retains the shared immutable `Skeleton`, its `PoseRange(Start, Count)`, and its own evaluation version and time. Creation copies the reference pose and evaluates the first model pose. Local bone indices follow the definition's own `Skeleton.Parents` and start at zero. Model bone indices place all inherited bones first, followed by this definition's own bones.

| Member | Data or behavior |
| --- | --- |
| `Skeleton` | Shared topology, reference pose and bone correspondence |
| `Range` | Instance's own section in the storage's arrays |
| `LocalPose` | Read-only local transforms for this definition's own bones |
| `Update(localPose, time)` | Copies one transform per bone, evaluates FK, and uses the supplied time for both calculation and local sampling |
| `Update(localPose, time, sampleTime)` | Evaluates FK at `time` while retaining the local input's distinct source `sampleTime` |
| `ModelPose` | Indexed inherited and own `Transforms`; `Version` and `Time` describe this instance's own evaluation, and `SampleTime` describes its local input |
| `Dispose()` | Returns this instance's section to the storage for reuse |

`BoneTransform.ToMatrix()` forms local matrices with `Scale * Rotation * Translation` under the `System.Numerics` row-vector convention. FK visits own bones in parent order: a model root G equals its local matrix; each child G equals its local matrix multiplied by the direct parent's G. An inherited parent reads its producing instance's G. All sections use the skeleton chain's common model space. SkinBinding supplies the mesh's inverse bind matrices separately.

`ModelPose.Transforms` supports `Length`, indexed reads, `CopyTo(Span<Matrix4x4>)` and `ToArray()`. `GetBone(index)` returns a `BonePose` with the resolved producing `Instance`, its own `BoneIndex`, matrix, version, calculation `Time` and source `SampleTime`. `SectionCount` and `GetSection(index)` expose sections from base to derived. Each `ModelPoseSection` provides its producing `Instance`, composite `BoneStart`, storage `Range`, continuous `Transforms`, and that section's `Version`, `Time` and `SampleTime`. Instance identity distinguishes equal ranges belonging to separate storages.

The following is a public API usage example:

```csharp
var poses = new PoseStorage(boneCapacity: skeleton.BoneCount * 1000);
using SkeletonInstance character = poses.CreateInstance(skeleton);
BoneTransform[] local = skeleton.ReferencePose.ToArray();
local[0].Translation = new Vector3(2, 0, 0);
character.Update(local, time: 1.25);
ModelPose pose = character.ModelPose;
Matrix4x4 root = pose.Transforms[0];
```

## Evaluation and lifetime

Direct consumers write an instance through `Update`; it evaluates the bound parent chain before its own FK. The common animation composition stage first copies changed local-pose results into their instances, then composes the connected instances through those same parent links. Each instance evaluates when its local input or the parent's result version changes. Independent instances keep separate result sections and versions while sharing their Skeleton definition.

The composition system's calculation clock starts at zero and advances by `ImmediateSystemContext.DeltaTime` on every update. `Time` records that clock when the section is evaluated. `SampleTime` retains the selected local result's source time, which can follow the clip's speed, looping or graph stage. A parent-driven recalculation advances the child's calculation time while preserving the held local input's sample time. `PoseResult.Time` remains the node's source time.

Continuous array spans apply to the current read phase. Callers obtain them after pose work completes and consume them before the next update, instance creation or release. `ModelPose` and `BonePose` retain the resolved instance sources and obtain current values on each read. Installation and release happen after work using the storage has completed. Standalone reference-pose instances begin with version one and time zero; an attached instance begins at its parent's current time.

## Bone targets

`BoneTarget` supplies a bone index, target `Position` and `Orientation`, a `BoneTargetSpace`, and separate `PositionWeight` and `OrientationWeight` values. `Local` coordinates are relative to the bone's parent, `Model` coordinates use the skeleton's common model space, and `World` coordinates use world space. A consuming solver applies the corresponding coordinate conversion and weights.

Source: [PoseStorage](../../../src/SomeEngine.Animation.Pose/PoseStorage.cs), [SkeletonInstance](../../../src/SomeEngine.Animation.Pose/SkeletonInstance.cs), [BoneTarget](../../../src/SomeEngine.Animation.Pose/BoneTarget.cs).

Single-parent definitions, standalone creation, attachment and resolved skin bindings are described in [Skeleton Inheritance](Skeleton-Inheritance.md).

## Relations

[[architecture/animation/Skeleton-Assets|Skeleton Assets]] · [[architecture/animation/Pose-Animation|Pose Animation]] · #97
