# Skeleton Instances

## Data and public API

`PoseStorage` holds continuous arrays of local `BoneTransform` values and model-space `Matrix4x4` values. A world can retain one storage and create its skeleton instances through `CreateInstance(Skeleton)`. The optional constructor capacity reserves space in bones.

`SkeletonInstance` directly retains the shared immutable `Skeleton`, its `PoseRange(Start, Count)`, and its own evaluation version and time. Creation copies the reference pose and evaluates the first model pose at time zero. Bone indices within an instance follow `Skeleton.Parents` and start at zero.

| Member | Data or behavior |
| --- | --- |
| `Skeleton` | Shared topology, reference pose and bone correspondence |
| `Range` | Instance section in the storage's arrays |
| `LocalPose` | Read-only local transforms for this instance |
| `Update(localPose, time)` | Copies one transform per bone, evaluates FK, and publishes the supplied calculation time with the next version |
| `ModelPose` | Read-only `Transforms`, `Version` and `Time` from the most recent evaluation |
| `Dispose()` | Returns this instance's section to the storage for reuse |

`BoneTransform.ToMatrix()` forms local matrices with `Scale * Rotation * Translation` under the `System.Numerics` row-vector convention. FK visits bones in parent order: root G equals its local matrix; each child G equals its local matrix multiplied by the parent's G. G uses the skeleton's model space. SkinBinding supplies the mesh's inverse bind matrices separately.

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

The selected pose evaluation stage writes an instance through `Update`; consumers read the resulting G, version and calculation time. Updates copy and evaluate existing arrays. Independent instances keep separate result sections and versions while sharing their Skeleton definition.

Array views apply to the current read phase. Callers obtain them after pose work completes and consume them before the next update, instance creation or release. Installation and release happen after work using the storage has completed. Recreated instances start from their Skeleton's reference pose, version one and time zero.

## Bone targets

`BoneTarget` supplies a bone index, target `Position` and `Orientation`, a `BoneTargetSpace`, and separate `PositionWeight` and `OrientationWeight` values. `Local` coordinates are relative to the bone's parent, `Model` coordinates use the skeleton's common model space, and `World` coordinates use world space. A consuming solver applies the corresponding coordinate conversion and weights.

Source: [PoseStorage](../../../src/SomeEngine.Animation.Pose/PoseStorage.cs), [SkeletonInstance](../../../src/SomeEngine.Animation.Pose/SkeletonInstance.cs), [BoneTarget](../../../src/SomeEngine.Animation.Pose/BoneTarget.cs).

## Relations

[[architecture/animation/Skeleton-Assets|Skeleton Assets]] · [[architecture/animation/Pose-Animation|Pose Animation]] · #97
