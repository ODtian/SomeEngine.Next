# Skeleton Assets

## Data and public API

`SomeEngine.Animation.Pose` supplies `Skeleton`, a synchronous use object containing parent-ordered bones. `SkeletonAsset` supplies the identity and typed asset reference; `SkeletonBlock` stores the skeleton through the existing asset API. Both `Skeleton` and its Block expose:

| Member | Data |
| --- | --- |
| `BoneCount` | Number of bones, up to 32768 |
| `Parents` | `ReadOnlySpan<short>`; each parent precedes its child, with `-1` for roots |
| `ReferencePose` | `ReadOnlySpan<BoneTransform>`; local translation, rotation and scale for each bone |
| `NameHashes` | `ReadOnlySpan<uint>`; one case-sensitive UTF-8 FNV-1a hash per bone; empty names use zero |
| `JointBones` | `ReadOnlySpan<short>`; bone index for each source joint, in the source skin's joint order |

`BoneTransform` is a struct with `Vector3 Translation`, `Quaternion Rotation` and `Vector3 Scale` fields. `BoneNames.Hash(ReadOnlySpan<char>)` provides the same name hash for callers. Hashes are lookup data; source names can repeat or collide.

The `Skeleton` constructor accepts spans for the four arrays and copies them. `SkeletonBlock` holds the ready `Skeleton`; copies and asset branches share its immutable data. Development builds report inconsistent array lengths, parent order and joint indices through Core `DevelopmentCheck`. Reading each frame uses the spans directly.

After `GetBlockAsync<SkeletonBlock>()` finishes, `CreateSkeleton()` returns the ready use object. A caller retains that object through an ordinary reference, including after the Asset and Block close. Instances share this object directly. Program-created skeletons use the same constructor data and public reads.

Public API usage example:

```csharp
Skeleton skeleton;
using (SkeletonAsset asset = await SkeletonAsset.OpenAsync(path))
    skeleton = (await asset.GetBlockAsync<SkeletonBlock>()).CreateSkeleton();

ReadOnlySpan<BoneTransform> referencePose = skeleton.ReferencePose;
```

Source: [Skeleton](../../../src/SomeEngine.Animation.Pose/Skeleton.cs), [SkeletonBlock](../../../src/SomeEngine.Animation.Pose/SkeletonBlock.cs).

## glTF import

`SomeEngine.Animation.Pose.Importers.GltfSkeletonImporter` implements `IAssetImporter<SharpGLTF.Schema2.Skin, GltfSkeletonSettings>`, has a parameterless constructor, and declares `SkeletonBlock` as its output. The project includes the importer assembly in its `[AssetCapabilities]` discovery and selects the importer in the source `.meta` (`GltfSkeletonImporter.Id`). `GltfSourceImporter` supplies each skin's `SkeletonAsset` identity and saving path.

The importer visits joints in `skin.joints` order, adding each unvisited ancestor chain from root to child. It retains the joints and all their ancestors, including nodes between joints and above `skin.skeleton`. Each bone keeps its source local transform. This preserves the model-space transform when parents are propagated, and lets future clip import address intermediate nodes directly. Ancestor transforms participate in skinning under the [glTF joint hierarchy rules](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#joint-hierarchy). Other branches remain scene data.

The bone count therefore includes the retained ancestor nodes. `JointBones.Length` equals `skin.joints.Length`. `JointBones[joint]` identifies the reordered bone associated with that original joint. `SkinBindingImporter` obtains the Block with `context.GetBlockAsync<SkeletonBlock>(output.Target, token)` and combines this mapping with the source inverse bind matrices in their original joint order. The neutral skeleton group includes both producers. Binding data and its public behavior evidence are described in [Block Division](../Assets/Block-Division.md).

## Persistence and verification

The Block uses the asset system's static typed binary codec. Metadata stores a bone count, each bone's parent/TRS/name hash, then the source joint mapping. `SkeletonAsset.SaveAsync`, `OpenAsync`, `GetBlockAsync<SkeletonBlock>`, `Duplicate` and `CreateCopy` preserve all four arrays. Collection allocation limits remain in the serialization layer.

Public behavior tests in `tests/SomeEngine.Animation.Pose.Tests` import a GLB with reordered joints, matrix and TRS transforms, intermediate ancestors, default transforms, and Unicode names. They check parent propagation, asset persistence and copying, use-object reads after asset closure, equivalent directly created data, empty and maximum-index skeletons, reimport identity and edited pose, a project-selected replacement skeleton importer, and a user importer consuming the joint mapping through `ImportContext`.

## Relations

[[architecture/animation/Pose-Animation|Pose Animation]] · [[architecture/Assets/Asset-Objects|Asset Objects]] · [[architecture/Assets/Importer-Discovery|Importer Discovery]] · `docs/Pose-Animation-Spec.md` · #95 · #117 · #121
