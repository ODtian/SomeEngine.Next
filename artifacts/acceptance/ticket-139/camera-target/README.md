# Orthographic view target — ticket 139

One public behavior: OrthographicCameraTargetCentersItsClipmapOrigin. A one-meter orthographic view supplies its actual target (10,0,0) through ordinary RenderView data. Native level 6 has radius 1.28 meters; target-centered snapping places its center at 10.24. The .24-meter offset over a 2.56-meter projection half-width gives absolute NDC .09375.

Red returned 3.90625 from the old camera-centered projection. Green passed the independent .09375 interval and confirmed the public ClipmapWorldOrigin data equals the supplied target. Each TRX contains one actual executed case; green took 701 ms, and the rule checks returned zero violations. Device/testhost ended after the case. The Runtime project then built with zero warnings/errors.

CameraTarget follows the existing camera/view data route. RuntimeCamera owns position, direction and target distance, deriving the actual target point. RuntimeViewFrame and its initial/staged application calls pass that point to the RenderWorld view through named input. Orthographic clipmap XY/Z and distance origin use that same point; perspective/default-target views retain their camera-derived origin. User camera/collision systems can supply their actual target through the same view input.

Native source switches are expressed by OrthographicProjectCameraTarget=true, OrthographicEstimateClipmapLevels=true and OrthographicClipmapLodBias=0. The native view-target condition and first-level controls are checked in VirtualShadowMapClipmap.cpp:258–317. The shader distance origin follows its actual WorldOrigin, checked at cpp:548–563 and ProjectionCommon's distance helper.

This closes the source projection/guardband/target chain from ticket 139, using the existing unique shadow-view data holder and graph completion boundary. Typed VSM view remains 208 bytes; its source identity/build and guardband evidence are in the preceding slice. Authority is fixed native commit 396c9f059903aed5fec78ecd3d437a40c6415368 and the established blob/SHA/bytes manifest.
