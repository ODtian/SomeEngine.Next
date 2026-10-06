# Main-camera geometry and cached shadow output — ticket 137

This extends the existing public off-camera caster behavior with one main GeometryProjection edit on the same installed world after its six geometry feedback frames. The actual fixed shadow geometry and full beauty/depth are compared with the independent installation. Public completed target candidate work is expected to stay zero when shadow inputs stay fixed.

Red: full images matched, but the main geometry proxy advanced all shadow depth generations and caused 12 actual target candidate tests after the edit. The existing case failed against the independently specified zero work.

Green: the two main RenderView proxy states and their comparison/writeback were removed. The same public case passed full beauty/depth equality and reported zero target candidate tests. One case executed, 14 seconds, zero rule violations; the device/testhost ended after execution. The source contains the complete preceding own-view/native error/1-.5-32-2/source-edge/finite-dicing chain.

Each prepared shadow view's actual parameters maintain its own depth invalidation. Directional comparison retains XY world-page scrolling while checking the actual depth translation M43. Geometry, material and instance revision protections continue. The existing bounded/unbounded external vertex/domain/opacity invalidation APIs report real inputs. Per-level Z guardband follows in ticket 139; localized material/streaming changes follow in ticket 147.

Command: dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release --filter FullyQualifiedName~MainCameraGeometryProjectionPreservesOffCameraCasterShadows

The content entry built 68 assets and the Cluster test project built with zero warnings/errors before red. Both TRX records contain the actual case and source/geometry quantities. The preceding primary-geometry proof retains complete actual/expected/difference images; this scalar work failure occurred after complete image equality passed.
