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

## Batch and subset sampling

`ClipSampleSelection` binds a ready clip and copies the requested bone indices in caller order. Its single-argument constructor selects every bone. Indices follow the clip skeleton's own `BoneCount`, `Parents` and `ReferencePose` arrays.

For model sampling of a derived skeleton, the selection supplies `parentPose`, such as `new ClipSampleSelection(clothingClip, [clothBone], parentPose: character.ModelPose)`. Binding resolves each required inherited root to that parent result's producing section. Local sampling continues to use own-bone rows; model sampling multiplies an attached root's sampled local matrix by the selected parent's current G. Different selections can use the same clip with different parent results. The caller chooses parent results corresponding to the requested sample times.

`ClipSampleBatch` binds a fixed sequence of these selections. Reusing a selection supports several times or characters using the same clip. Each call accepts one time per selection and writes the corresponding output sections consecutively. `OutputCount` gives the required element count; `GetOutputRange(sample)` returns that selection's `PoseRange` relative to the supplied span.

| Entry | Output element | Required local bones | Completion |
| --- | --- | --- | --- |
| `SampleLocal(times, destination)` | `BoneTransform` in selection order | Selected bones | Returns completed output |
| `SampleModel(times, destination)` | `Matrix4x4` in selection order | Selected bones and their ancestors | Returns completed output |
| `ScheduleLocal(times, destination, dependency)` | `BoneTransform` in selection order | Selected bones | Returns a `JobHandle` |
| `ScheduleModel(times, destination, dependency)` | `Matrix4x4` in selection order | Selected bones and their ancestors | Returns a `JobHandle` |

Binding groups selections by ready clip, which also fixes their concrete sampling function and data. It expands model-space ancestors once in parent order and allocates reusable time, local-pose and model-pose arrays. Each call fills these arrays, invokes the concrete sampler per clip group, and copies the selected results into the caller's span. Playback instances continue to hold their own current pose, version and time. Each concurrent caller retains its own batch workspace.

The scheduled entries accept array-backed `ReadOnlyMemory<float>` and output `Memory<T>`, including slices. Binding divides each clip group into concrete sampling tiles, with `samplesPerJob` defaulting to 32. Each tile owns distinct time/local/model rows and output sections. Scheduling declares reads of the input memory and ready clips, a write of the batch workspace, writes of the output memory range, and reads of the selected parent sections. The selected parent instances' binding versions refresh those section reads after attachment or detachment, including an intermediate instance in the inheritance chain. The caller can pass an input producer's dependency and complete the returned handle before reading or releasing these memories. Repeated scheduled calls on one workspace follow its declared write dependency; synchronous access uses the caller's completion boundary.

The following is a public API usage example:

```csharp
var feet = new ClipSampleSelection(clip, [12, 18]);
var batch = new ClipSampleBatch([feet, feet, new ClipSampleSelection(otherClip)]);
var local = new BoneTransform[batch.OutputCount];
var model = new Matrix4x4[batch.OutputCount];
float[] times = [0.25f, 0.75f, 1.0f];
batch.SampleLocal(times, local);
batch.SampleModel(times, model);
PoseRange secondTime = batch.GetOutputRange(1);
ReadOnlySpan<Matrix4x4> futureFeet = model.AsSpan(secondTime.Start, secondTime.Count);
```

This is the corresponding asynchronous usage example with the same ready definitions and output arrays:

```csharp
JobHandle localReady = batch.ScheduleLocal(times, local, inputReady);
JobHandle modelReady = batch.ScheduleModel(times, model, localReady);
modelReady.Complete();
```

`ClipBatchSamplingTests` compares selected local and model results with complete public sampling across several clips and times, while reading each character's current pose through `SkeletonInstance`. The independent sampling measurement uses the same selections, times and output ranges for batch and individual calls:

```powershell
$env:DOTNET_TieredCompilation = '0'
tools/Invoke-Benchmark.ps1 dotnet run --project tools/SomeEngine.Animation.Checks -c Release '--property:PublishAot=false' -- --pose-sampling-benchmark
```

The command reports reference-pose framework cost and raw-curve sampling cost, with local/model timing and steady managed allocation. Measurement output is written to the queue's standard output.

## Consumer codecs

An encoding extension supplies its own Importer and `AnimationClipBlock` subclass. The Block exposes the common metadata and a `ClipSampler`. Its `CreateClip` entry produces the same ready use object as the raw implementation.

For a generated Block codec, mark computed metadata overrides with `[BinaryIgnore]` and declare the encoding's stored properties with `[BinaryFields]`. `ClipEvent` supplies a public static value codec for event collections in those properties.

`ClipSampler.Create<TData, TSampler>(data)` binds a managed function pointer to the concrete `IClipSampler<TData>.SampleLocal` implementation. The ready data is a normal class reference. The common sampling entry initializes the local pose before the codec writes animated values. The raw implementation loops over its typed channels; a consumer implementation controls its own encoding and decoding loop.

`IClipSampler<TData>.SampleLocalBatch<TSampler>` supplies the corresponding batch extension. Its default implementation calls the concrete scalar implementation for each row. An encoding can supply its own loop, as `RawClipSampler` does by visiting requested channels across all sample times. The `ClipSamples` view supplies clamped `Times`, sorted requested `Bones`, and `LocalPose(sample)` rows indexed by the skeleton's own bone indices. `LocalPoses` exposes the contiguous rows with `BoneCount` as their stride for concrete batch decoders. Rows start with reference values, so codecs write their animated properties directly.

The [ACL implementation](ACL-Clips.md) supplies `AclClipImporter`, `AclClipBlock`, an upstream compression adapter and a concrete batch decoder through these same entries. Its import settings hold the sampling rate, precision and shell distance; its ready data remains usable after the asset closes.

The [uniform quantized implementation](GPU-Clips.md) supplies `QuantizedClipImporter`, `QuantizedClipBlock` and `QuantizedClipData`. It resamples complete TRS at uniform times and exposes the encoded data to both the common CPU sampler and the GPU clip leaf.

Sources: [AnimationClip](../../../src/SomeEngine.Animation.Pose/AnimationClip.cs), [ClipSampler](../../../src/SomeEngine.Animation.Pose/ClipSampler.cs), [ClipSampleBatch](../../../src/SomeEngine.Animation.Pose/ClipSampleBatch.cs), [ClipSampleSelection](../../../src/SomeEngine.Animation.Pose/ClipSampleSelection.cs), [ClipSamples](../../../src/SomeEngine.Animation.Pose/ClipSamples.cs), [AnimationClipBlock](../../../src/SomeEngine.Animation.Pose/AnimationClipBlock.cs), [RawClipBlock](../../../src/SomeEngine.Animation.Pose/RawClipBlock.cs), [RawClipChannel](../../../src/SomeEngine.Animation.Pose/RawClipChannel.cs), [RawClipImporter](../../../src/SomeEngine.Animation.Pose.Importers/RawClipImporter.cs), [GltfClipReader](../../../src/SomeEngine.Animation.Pose.Importers/GltfClipReader.cs).

Public behavior entry: `tests/SomeEngine.Animation.Pose.Tests/RawClipBehaviorTests.cs` imports and reopens clips, reads source interpolation and metadata, and selects a consumer codec through source metadata before using the common sampler.

## Relations

[[architecture/animation/Skeleton-Assets|Skeleton Assets]] · [[architecture/animation/Pose-Animation|Pose Animation]] · [[architecture/Assets/Asset-Objects|Asset Objects]] · #96
