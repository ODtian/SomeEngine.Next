# Native hardware window origin (#149)

Hardware windows now begin at the cluster page rectangle's minimum, matching NaniteClusterCulling.usf:889–940 at UnrealEngine 396c9f059903aed5fec78ecd3d437a40c6415368. The existing page mapping, receiver coverage, crop and work-capacity checks continue to select each emitted window.

The single public rendering case places a receiver and an off-camera caster at light-space page bounds x=5..8, y=1. Each plane requires one four-page fine window and one coarsest-page window. The previous globally aligned origin split each fine rectangle into 5..7 and 8..8.

| Same case | Executed | Passed | Failed | Skipped | Required hardware windows |
| --- | ---: | ---: | ---: | ---: | ---: |
| Grid origin | 1 | 0 | 1 | 0 | 6 |
| Cluster origin | 1 | 1 | 0 | 0 | 4 |

The green frame reports four accepted windows and complete work. Its scene, raster option, resolution, lights and geometry match the red frame. The necessary Release build completed with zero warnings/errors; shader content construction generated 68 assets. Both test calls ran the repository behavior rules with zero violations.

Command: `dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release --no-build --filter FullyQualifiedName~FourPagesAcrossTheGridBoundaryUseOneHardwareWindow`. The minimal direct results are retained as `red.trx` and `green.trx`. The integrated three-scene check measures the resulting renderer performance.
