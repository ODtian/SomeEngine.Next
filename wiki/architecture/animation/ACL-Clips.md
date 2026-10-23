# ACL Clips

`AclClipCompressor` converts a ready `AnimationClip` into `AclClipData` using [ACL 2.1.0](https://github.com/nfrechette/acl/releases/tag/v2.1.0) and [RTM 2.2.0](https://github.com/nfrechette/rtm/releases/tag/v2.2.0). The native adapter invokes the upstream compressor and decompressor directly. Compression uses constant-track handling, segment range reduction, variable quaternion/vector quantization and the matrix-based transform error metric.

## Compression inputs

| Input | Meaning |
| --- | --- |
| `sampleRate` | Requested uniform source samples per second |
| `precision` | ACL model-space positional error threshold, in the skeleton's distance units |
| `shellDistance` | Distance from each bone origin at which rotation and scale error are measured |

The source clip is sampled into complete local poses, including the skeleton's reference values. The number of intervals is rounded up and the resulting sample rate includes both clip endpoints. `sampleRate` controls the source-curve resampling; `precision` controls compression of those samples. The matrix error metric follows the skeleton's own parent chain and includes nonuniform scale. Each track retains its own bone index in the decoded output.

This is a public API usage example:

```csharp
AclClipData data = AclClipCompressor.Compress(source, sampleRate: 60,
    precision: 0.001f, shellDistance: 1);
var clip = new AnimationClip(source.Skeleton, data.Duration,
    ClipSampler.Create<AclClipData, AclClipSampler>(data), data.Events, data.RootMotionBone);
clip.SampleLocal(0.5f, destination);
```

## Import and persistence

Select `AclClipImporter.Id` in the glTF source metadata and supply `AclClipSettings`. The selected skeleton importer provides the source node correspondence, and `GltfClipReader` reads the source curves before ACL compression.

| Setting | Default | Meaning |
| --- | --- | --- |
| `SkinIndex` | `0` | Source skin whose skeleton supplies the clip bone indices |
| `RootMotionNode` | `-1` | Source node selected for root motion, or `-1` for an unset selection |
| `SampleRate` | `60` | Requested uniform source samples per second |
| `Precision` | `0.001` | ACL model-space positional error threshold |
| `ShellDistance` | `1` | Bone-relative measurement distance for rotation and scale error |

`AclClipBlock` stores the skeleton asset reference, duration, events, root-motion bone and compressed bytes. Block duplication shares its immutable `AclClipData`; opening an asset reconstructs aligned ready data. `CreateClip` returns the common ready clip and retains that data through an ordinary reference.

This is a public API usage example:

```csharp
AnimationClip clip;
using (AnimationClipAsset asset = await AnimationClipAsset.OpenAsync(path))
    clip = (await asset.GetBlockAsync<AclClipBlock>()).CreateClip(skeleton);

clip.SampleLocal(0.5f, destination);
```

## Ready data and sampling

`AclClipData` retains the compressed bytes in aligned, pinned managed storage and exposes duration, sample rate, bone count, events and root-motion selection. Clip objects and samplers hold ordinary references to this data. The data object's lifetime therefore follows its consumers.

`AclClipSampler` implements the same `IClipSampler<AclClipData>` boundary used by source curves and consumer codecs. Scalar sampling decodes a complete row. Batch sampling passes the requested times, bone indices and contiguous local rows to one native call; the native decoder initializes one context, seeks each time, and decodes either all tracks or the requested subset. Each call uses its own context and writes the caller's output storage.

`ClipSampleBatch` continues to provide caller-order output ranges and model-space ancestor composition. The ready clip also works with `ClipLeaf` and the shared animation graph.

## Native build and distribution

The Pose project builds `SomeEngineAcl.dll` with CMake and the Windows x64 C++ toolchain under its `obj/native` directory. The adapter uses C++17, the static C runtime and the ordinary x64 SIMD baseline. Managed calls use source-generated `LibraryImport` entry points.

The native DLL and both MIT notices are transitive output and publish content. A project referencing the Pose library receives these files with its managed output, including NativeAOT publishing. The upstream headers and notices are kept in `external/acl` and `external/rtm` with pinned-version READMEs.

The public behavior tests compare decoded full poses and caller-selected batch results against source curves, then import, save, reopen and play a compressed clip after closing its asset. A separate consumer codec exercises source-metadata selection through the same public sampling entry.

Sources: [compressor](../../../src/SomeEngine.Animation.Pose/AclClipCompressor.cs), [ready data and sampler](../../../src/SomeEngine.Animation.Pose/AclClipData.cs), [Block](../../../src/SomeEngine.Animation.Pose/AclClipBlock.cs), [Importer](../../../src/SomeEngine.Animation.Pose.Importers/AclClipImporter.cs), [native adapter](../../../src/SomeEngine.Animation.Pose/Native/Acl.cpp), [Pose project](../../../src/SomeEngine.Animation.Pose/SomeEngine.Animation.Pose.csproj), [public behavior tests](../../../tests/SomeEngine.Animation.Pose.Tests/AclClipBehaviorTests.cs).

[[architecture/animation/Clip-Assets|Clip Assets and Sampling]] · [[architecture/animation/Pose-Graphs|Pose Graphs]] · #102
