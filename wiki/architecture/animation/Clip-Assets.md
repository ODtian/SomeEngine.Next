# Animation Clip Assets and Sampling

## Ready clips

`AnimationClipAsset` supplies asset identity and a typed Block directory. A concrete `AnimationClipBlock` supplies encoded data and the shared `CreateClip(Skeleton)` entry. The returned `AnimationClip` retains its ready `Skeleton`, metadata and sampler data through ordinary references. It remains usable after the source Asset and Block close.

| Member | Caller-visible data or behavior |
| --- | --- |
| `Skeleton` | The ready skeleton used for bone indices and reference values |
| `Duration` | Clip duration in seconds |
| `RootMotionBone` | The bone whose retained motion supplies the movement consumer; `-1` leaves root-motion selection unset |
| `Events` | Timed `ClipEvent` values with a shared-timeline `ushort` event ID |
| `SampleLocal(time, destination)` | Writes a complete local pose into the caller's bone buffer |

Sampling initializes the destination from the skeleton's reference pose and applies animated properties. Time is measured from the clip start. Each source channel holds its first or last value outside its key interval. Sampling preserves the root channel's local transform; playback and movement consumers decide how to consume its delta. Shared timeline playback owns clocks, looping and event counts.

Public API usage example:

```csharp
AnimationClip clip;
using (AnimationClipAsset asset = await AnimationClipAsset.OpenAsync(path))
    clip = (await asset.GetBlockAsync<RawClipBlock>()).CreateClip(skeleton);

var pose = new BoneTransform[skeleton.BoneCount];
clip.SampleLocal(0.5f, pose);
```

## Source curves and import

`RawClipImporter` produces `RawClipBlock` from a glTF animation. Select it in the source `.meta`; `RawClipSettings.SkinIndex` chooses the source skin and `RootMotionNode` chooses a source node for root motion. The selected skeleton importer supplies the skeleton and source joint correspondence. `GltfClipReader.Read` follows source node identities through that correspondence and the bone parents, so reordered joints and retained ancestors receive their own channels.

`GltfClipReader` is a public ordinary library function for other clip importers. It reads accessors through the existing `GltfAccessorReader`, preserving expanded source values. `RawClipData` retains duration, events, the root bone and immutable `RawClipChannel` objects. Channels retain key times, bone property and interpolation. Cubic channels retain each key's incoming tangent, value and outgoing tangent.

| Source interpolation | Translation and scale | Rotation |
| --- | --- | --- |
| `STEP` | Previous key value | Previous key value |
| `LINEAR` | Linear interpolation | Short-path spherical interpolation |
| `CUBICSPLINE` | Cubic Hermite interpolation with tangents scaled by key interval | The same component interpolation, followed by normalization |

At a key time, sampling returns that key's value. These rules follow [glTF animation interpolation](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#appendix-c-animation-sampler-interpolation-modes).

The source metadata field `animation.extras.someengine_events` contains objects with `time` in seconds and an integer `id`. Import orders these markers by time. `RootMotionNode` uses the source node index; the ready clip retains the resolved bone index. Source curves, marker values, duration, root bone and skeleton asset reference all survive `RawClipBlock` persistence.

## Consumer codecs

An encoding extension supplies its own Importer and `AnimationClipBlock` subclass. The Block exposes the common metadata and a `ClipSampler`. Its `CreateClip` entry produces the same ready use object as the raw implementation.

For a generated Block codec, mark computed metadata overrides with `[BinaryIgnore]` and declare the encoding's stored properties with `[BinaryFields]`. `ClipEvent` supplies a public static value codec for event collections in those properties.

`ClipSampler.Create<TData, TSampler>(data)` binds a managed function pointer to the concrete `IClipSampler<TData>.SampleLocal` implementation. The ready data is a normal class reference. The common sampling entry initializes the local pose before the codec writes animated values. The raw implementation loops over its typed channels; a consumer implementation controls its own encoding and decoding loop.

Sources: [AnimationClip](../../../src/SomeEngine.Animation.Pose/AnimationClip.cs), [ClipSampler](../../../src/SomeEngine.Animation.Pose/ClipSampler.cs), [AnimationClipBlock](../../../src/SomeEngine.Animation.Pose/AnimationClipBlock.cs), [RawClipBlock](../../../src/SomeEngine.Animation.Pose/RawClipBlock.cs), [RawClipChannel](../../../src/SomeEngine.Animation.Pose/RawClipChannel.cs), [RawClipImporter](../../../src/SomeEngine.Animation.Pose.Importers/RawClipImporter.cs), [GltfClipReader](../../../src/SomeEngine.Animation.Pose.Importers/GltfClipReader.cs).

Public behavior entry: `tests/SomeEngine.Animation.Pose.Tests/RawClipBehaviorTests.cs` imports and reopens clips, reads source interpolation and metadata, and selects a consumer codec through source metadata before using the common sampler.

## Relations

[[architecture/animation/Skeleton-Assets|Skeleton Assets]] · [[architecture/animation/Pose-Animation|Pose Animation]] · [[architecture/Assets/Asset-Objects|Asset Objects]] · #96
