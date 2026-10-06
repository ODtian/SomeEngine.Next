# Native shadow parameters — ticket 137

One public behavior: ShadowViewsCarryNativeGeometryAndDicingParameters. Public VirtualShadowMap.PrepareViews supplies geometry scale .5, maximum pixels per edge 1, HW edge threshold 32 and dicing rate 2. The old output returned zero geometry scale; the same case passed after production writes. Red and green TRX each executed one case. Green duration was 728 ms; rule checks returned zero violations. The device ended after the case.

Authority is the fixed native commit 396c9f059903aed5fec78ecd3d437a40c6415368. NaniteCullRaster.cpp:133–151 defaults are 1/32/2. ShadowSceneRenderer.cpp:48–52,142–145,1167–1174 defaults shadow geometry bias to 1 and passes 2^(-bias) times the streaming quality scale. Quality initially equals one, giving .5 and an effective two-pixel geometric error.

The VSM data holder writes these four values into each directional/local typed view. Size is 192 bytes; the existing world unit, light position and range offsets are preserved. Compact copies the actual parameters into the generic Cluster view. Source/raw buffer fixtures use 48 words and 192-byte stride. The Cluster view retains 496 bytes; surface edge pixels use a previous padding word. Primary surfaces use their existing group configuration.

Finite user domains supply their own world positions and surface error. Shadow edge factors follow the active native GetTessFactors rule: three-dimensional view-space edge length divided by the minimum forward depth, multiplied by the light pixel/geometry scale. User surface-error bounds remain conservative for domain evaluation. NaniteTessellation.ush:62–100 supplies the source rule.

The existing Game.Build entry rebuilt 68 assets with the source and interface changes. Imported shader identities are committed. Default settings and the actual parameter producer are proven by this case; the existing whole-image and broader rendering acceptance continues on the integrated pipeline.

Streaming quality adaptation uses true unique visible-page demand divided by the streaming page pool, as checked in NaniteStreamingManager.cpp:478–515,2753–2760. That producer is coordinated with ticket 147. Native source blob/SHA/bytes are retained in the accompanying JSON files.
