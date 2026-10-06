# Adopted clipmap depth range — ticket 139

One public projection/state-transition behavior: CameraDepthMovementUsesCachedRangeUntilTheNativeGuardbandIsCrossed. The normal graph submission and completion callback commit each prepared projection. The source level 6 radius is 1.28 meters and its native depth radius is 1280 meters.

The native retained-depth condition is abs(currentZ-cachedZ)+levelRadius <= .9*cachedRadius. Camera heights 0, 1100, 1200 therefore map the fixed world origin to forward normalized depths .5, .5, .96875. At 1100 the projection still uses its adopted center. At 1200 the new center yields (1280+1200)/2560. Expectations are independent source examples, observed through the public projection output and explicit graph completion.

Red returned .9296875 after moving to 1100, revealing the old per-frame depth recentering. The same case passed after adopted center/radius and guardband were connected, with actual preserved and refreshed projection results. Red and green each executed one case; green took one second and rule checks reported zero violations. The device/testhost ended after green.

The unique frame/committed VirtualShadowView data holder now carries DepthCenterZ and DepthRadius. View layout is 208 bytes/52 words, preserving all preceding 192-byte fields. The current light-space view and a projection delta express the adopted cached center. Direction/radius/level and actual shadow parameters determine reuse. Existing successful submission/content completion, discard and recovery advance the same data holder.

The source C#/Slang/raw fixtures and imported shader identities were updated together. The existing content entry regenerated 68 assets. Native authority: 396c9f059903aed5fec78ecd3d437a40c6415368, VirtualShadowMapClipmap.cpp:352–458 and VirtualShadowMapCacheManager.cpp:258–315. The established native source manifest retains blob/SHA/bytes.
