# NativeAOT pose consumer example

This independent consumer defines its importer, serialized clip block, ready codec, pose leaf and state-machine condition in this assembly. The two-bone motion, one inherited parent, two panel instances and two influences per vertex are example data for the public extension APIs.

`PoseConsumerAssets.cs` registers the generated importer capability with DI, imports `motion.json` through `ImportContext`, and stores `ConsumerPoseBlock` in an ordinary `AnimationClipAsset`. `PoseConsumerChecks.cs` saves and reopens the assets, obtains the ready clip, closes the asset files, then samples and plays that clip.

`PoseConsumerGraph.cs` installs a typed per-instance buffer and reads the common graph clock through `AnimationGraph.WithTimeAccess` / `ReadTime(in row, ...)`. Its concrete system samples the clip, adds the example local-bone translation, writes `LocalPose` / `PoseResult`, and calls `Submit(in row, ...)`. The ordinary blend node and pose composition produce the public `ModelPose`. Two graph instances share definitions and the same live parent instance; playback speed, condition history and result storage belong to each character. Two qualifying input samples select an ordinary ACL clip leaf. The ACL sampler uses the same imported motion.

Prediction explicitly selects a blended custom-codec / ACL source path and supplies independently sampled future parent poses. `PosePredictionBatch` returns future model-space bones, path times, calculation time and caller conditions. The leaf's extra local translation is part of the example algorithm; the selected prediction path describes the underlying source motion.

`PoseConsumerApplication.cs` runs the same graph through `ApplicationScene`, attaches `MeshSkin` to the two scene mesh entities, and reads final public poses and completed Cluster metrics. The published executable verifies its NativeAOT mode, captures a visible rendered frame and saves its BMP.

## Rebuild the content and publish

Run from the repository root on Windows with the .NET 10 SDK and Visual Studio x64 C++ build tools. Build the common DefaultGame assets once, then prepare the example with the public asset pipeline. Queue submissions return immediately; consume each task's `result.json` before using its outputs.

```powershell
$queue = Join-Path $PWD 'tools/Invoke-Benchmark.ps1'
$project = 'tools/SomeEngine.Animation.Checks/SomeEngine.Animation.Checks.csproj'
$native = Join-Path $PWD 'tools/SomeEngine.Animation.Checks/bin/Release/net10.0/win-x64/native-publish'
$content = Join-Path $native 'ConsumerContent'
$env:DOTNET_PROCESSOR_COUNT = '4'

& $queue --cpu --session pose-example dotnet build $project -c Release '-m:1' '-nodeReuse:false' '-p:UseSharedCompilation=false' --disable-build-servers
& $queue --session pose-example dotnet run --project samples/DefaultGame/DefaultGame.csproj -c Release '-p:SomeEngineDevelopment=true' '-p:PublishAot=false' -- sample
& $queue --session pose-example dotnet tools/SomeEngine.Animation.Checks/bin/Release/net10.0/SomeEngine.Animation.Checks.dll --pose-consumer-prepare samples/DefaultGame assets/Shaders $content
& $queue --cpu --session pose-example dotnet publish $project -c Release -r win-x64 --self-contained true '-p:PublishAot=true' '-m:1' '-nodeReuse:false' '-p:UseSharedCompilation=false' --disable-build-servers -o $native
& $queue --session pose-example (Join-Path $native 'SomeEngine.Animation.Checks.exe') --pose-consumer $content (Join-Path $native 'pose-consumer.bmp')
```

The consumer preparation command copies the common assets and manifest, writes its checked-in `standard.shader` / `standard.slang`, composes the public Cluster and skin instance property layout, calls `AssetProject.BuildAsync`, converts a raw panel with `ClusterBuilder.ProcessRaw`, and saves an ordinary scene with `SceneSerialization.CreateRegistry`. The resulting directory can be deployed alongside the published executable. Published content includes `motion.json`, the shader sources, the ACL native library and dependency licenses. The project also publishes DXC / DXIL beside the Slang native libraries, which the built-in GPU pose shader compilation uses at skin-feature startup.

For a compatible previously compiled example shader, append `--reuse-shader` to preparation and supply that content root as the first argument. This reuses the same source/encoding specialization while still creating the example mesh, scene and settings through public assets. The normal command above rebuilds the checked-in shader definition in another checkout.

`--pose-consumer-cpu <directory>` runs the importer, ready clip, World playback and prediction portion with either the managed or published consumer. It uses the same example source identities and can continue using that directory on repeated runs.

## Example vertex bytes

The material encoding is `someengine.cluster.positions.uint16.v1+someengine.tests.skin.float.v1`, matching the existing shared two-influence example fixture. The geometry block holds its ordinary quantized positions. Each cluster's attribute bytes start with the standard surface bytes, followed by one 16-byte row per cluster-local vertex: `uint joint0`, `uint joint1`, `float weight0`, `float weight1`, all little endian. The final 8 bytes contain the surface byte count and a zero previous-frame selector.

`standard.slang` reads this tail, obtains `LoadSkinBinding(instanceID)`, applies the two `LoadSkinTransform` matrices, and returns the complete standard vertex with its deformed position. `standard.shader` preserves the source list and the static/skin specializations used by the public Cluster pipeline. `PoseConsumerContent` generates the matching instance layout with `RenderInstancePropertyLayout.Compose` and `ClusterShaderSources.BuildInstanceLayout`. The example skin binding maps its two source joints to model bones 0 and 2, with a translated inverse bind matrix for the second joint. Rendering receives these data through the ordinary Game World extraction and Rendering World skin feature.
