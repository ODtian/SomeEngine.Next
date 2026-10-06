# Shadow projection error — ticket 137

Source base: 9675bfea449627d30d42dfb70d4f711c04d70682. Windows, SDK 10.0.112, Release, public D3D12 WARP shader compilation/commands/buffer readback. One behavior case: ShadowSphereErrorUsesItsLightProjectionDepthAndAngle.

Independent hand example: 90-degree projection, 512×512 virtual pixels, near 0.1, sphere centered at depth 10 with radius 2, geometry error 1/64. Native ViewToPixels is 256; the minimum sphere edge projection distance is 8×sqrt(96)/10 = 7.838367. Four divided by that distance is 0.51031036 pixels.

Red returned 0.626223087 pixels through the old full XY/W derivative bound. Green passed the independent interval 0.510309–0.510312. Both TRX records contain the actual executed case; rule checks returned zero violations. GPU processes ended after the case.

Command: dotnet test tests/SomeEngine.Graphics.Direct3D12.Tests/SomeEngine.Graphics.Direct3D12.Tests.csproj -c Release --no-build --filter FullyQualifiedName~ShadowSphereErrorUsesItsLightProjectionDepthAndAngle

The C#/shader view layout remains 496 bytes. GeometryLodScale replaces an unused word; NearPlane replaces the retired cross-view geometry selector. Source seed/traversal/cluster consume their own view. Target sphere/cluster error follows the native minimum instance scale; sphere bounds retain maximum scale. Primary views retain their existing projection-bound calculation, with an explicit positive geometry scale.

Content entry rebuilt 68 DefaultProject assets with the final source. Shader import metadata records source/compiler inputs and content identities. The standalone scalar proof uses the ordinary shader library and RHI interfaces. The previous slice contains whole-image off-camera caster and independent-installation proof.

Native authority: EpicGames/UnrealEngine 396c9f059903aed5fec78ecd3d437a40c6415368; NaniteShared.cpp:195–202, NaniteCullingCommon.ush:112–159 and NaniteClusterCulling.usf:255–308. native-source.json records the added CullingCommon source blob/SHA/bytes. Native shadow default parameters, longest source-triangle edges and finite domain dicing are the following implementation slice.
