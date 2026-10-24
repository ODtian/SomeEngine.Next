# Skeleton Inheritance

## Definition and indices

A `Skeleton` references one optional `Parent` definition. Each definition stores its own bones in `Parents`, `ReferencePose` and `NameHashes`; `BoneCount` is the length of those own arrays. `Parents` uses own-bone indices and `-1` for an own root. `InheritedParents` has one entry per own bone: an attached root names a model bone in the parent definition, and other entries are `-1`.

The model index space places all parent bones first, followed by the definition's own bones. `InheritedBoneCount` is the parent model count and `TotalBoneCount` includes both parts. `SkinBinding.JointBones` uses these composite model indices. Clip sampling continues to use own-bone indices and `BoneCount` local rows.

The following is a definition example:

```csharp
var clothing = new Skeleton(body, parents: [-1], referencePose: [clothRoot],
    nameHashes: [clothRootName], inheritedParents: [bodyChest]);
using SkeletonInstance garment = poses.CreateInstance(clothing, character);
SkinBindingInstance skin = clothingSkin.Bind(garment);
Matrix4x4 chestG = skin.GetModelTransform(chestJoint);
Matrix4x4 clothSkinMatrix = skin.GetSkinTransform(clothJoint);
```

Here `bodyChest` is a model bone index in `body`. The garment's own root G is its local transform multiplied by the chest G. Bone matrices use the chain's common model space; skin matrices multiply the mesh's inverse bind matrix by that G.

## Assembly and lifecycle

`PoseStorage.CreateInstance(skeleton)` recursively creates private parent instances from their reference poses. The returned `ParentInstance` exposes the base instance for animation. `CreateInstance(skeleton, parent)` uses the supplied matching parent instance. Every instance writes its own continuous `Range`; inherited bones point directly to the producing instances' G.

`Attach(parent, time)` selects an existing matching parent, refreshes the result mappings for the instance and its attached descendants, evaluates their own FK at `time`, then releases the previous private parent chain. `Detach(time)` creates a private parent chain using the current parents' local poses, calculation times and sample times, and reconnects the descendants at the supplied time. The resulting clothing pose continues from the attached pose.

`Dispose()` releases the instance's own range and its private parent chain. Existing supplied parents retain their own lifetime. Callers release attached children before the supplied parent. `IsDisposed` exposes the completed release state. Assembly, reconnection and release run after work using those instances has completed.

## Reading shared results

Assembly resolves bone indices to the instance section producing each matrix. Reconnection updates that same source table. Retained `ModelPose`, `BonePose` and `SkinBindingInstance` reads therefore follow the current source, including reconnecting an intermediate instance in the chain. Updating a parent immediately changes inherited reads; the child's next FK update propagates its own local transforms from that direct parent G. Common animation composition follows those bound parent links before evaluating a child and refreshes the child's own FK when its parent changes, including a child holding its last local result.

`ModelPose.GetSection` exposes each producing instance's identity, continuous matrix range, version, calculation `Time` and local input `SampleTime`. `GetBone` and `SkinBindingInstance.GetJointPose` expose the producing instance and its local bone index. Rendering can share a parent upload across several attached garments. Full metadata and span use are described in [Skeleton Instances](Skeleton-Instances.md).

Public behavior evidence: [SkeletonInheritanceBehaviorTests](../../../tests/SomeEngine.Animation.Pose.Tests/SkeletonInheritanceBehaviorTests.cs).

## Relations

[[architecture/animation/Skeleton-Instances|Skeleton Instances]] · [[architecture/animation/Clip-Assets|Clip Assets]] · #301
