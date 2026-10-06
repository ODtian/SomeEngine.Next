# Independent shadow geometry — ticket 137 first slice

Code base: 6e32800210cfec765f6995f3940530b59e2e63ec. Platform: Windows, SDK 10.0.112, Release, D3D12 hardware, forced hardware raster. Public entry: FrameStageScene's normal extraction/RenderWorld/persistent graph path and output copy/readback.

Source IcoSphere(5): 20480 triangles, 10242 vertices, 6 source pages. A plane receives a shadow from a caster outside the primary camera. Both independently installed scenes use the same fixed raster projection, light and source geometry. The main camera's public GeometryProjection ranges from .001 to 10000 world-width orthographic projections. Six feedback frames permit geometry refinement. Removing the caster supplies an image witness that the scene contains its shadow.

Red: old compiled compact shader selects primaryViews[0]. One case failed through the whole color image at byte 11496. red-images retains full actual/expected/difference PNG and raw color bytes.

Green: compact uses its own light view. The same one case passed through whole beauty and depth equality. The rule check returned 0 violations. GPU processes ended after the result.

Command: dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release --no-build --filter FullyQualifiedName~MainCameraGeometryProjectionPreservesOffCameraCasterShadows

The compact .shader.asset was rebuilt by the existing Game.Build content entry between red and green. Its red SHA256 was 6e8affddc9c03895c5af1c4ad410ef28a6f2d55646c49daa3108508d4474aabb; green SHA256 is 264af7f7577e3cfdbbabece2c436148d6116b951c5aa0bac6c930ae29af57953. The tracked compact .import.json records source inputs and content identity. TRX contains source quantities and actual completed-frame metrics/feedback.

Fixed native reference: EpicGames/UnrealEngine 396c9f059903aed5fec78ecd3d437a40c6415368, NaniteShared.cpp:195–202 and VirtualShadowMapArray.cpp:4958–4965,5008–5016. Native views derive geometry scale from their own shadow projection and viewport. The source-native error/HW scaling is the next coherent slice; global geometry/material revision protection remains in the cache chain until tickets 147/141 consume local caster inputs.
