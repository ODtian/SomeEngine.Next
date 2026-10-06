# Native per-level depth range — ticket 139 first slice

Source base 63bfdbf66d53746308b701ed249a38a05360d176. Windows, SDK 10.0.112, Release, public VirtualShadowMap.PrepareViews and light projection output. One behavior: DirectionalLevelsCoverTheirNativeDepthRange.

Independent source example: native level 6 radius is 2^(6+1) centimeters. Explicit .01 world units per centimeter gives a radius of 1.28 meters. Native ZRangeScale 1000 gives a Z half-range of 1280 meters. With a light directed down Y and origin zero, world Y 1200 sits 80 meters from the light-facing edge of the 2560-meter span, so forward normalized depth is 1/32. The level 7 projection scales the same example to Y 2400.

Red returned -.70017004 for the first world point, outside the old fixed depth projection. Green returned the independently specified interval around 1/32 for both source world examples. One case executed in each TRX; green took 719 ms and the rule checks reported zero violations. Device/testhost ended after green.

Each directional level now forms its own depth radius and includes actual light-space Z in its center. DirectionalDepthRangeScale replaces the former fixed directional distance/near/far configuration. This slice supplies the projection basis for the following adopted center/radius guardband and orthographic target-origin pieces.

Native authority: EpicGames/UnrealEngine 396c9f059903aed5fec78ecd3d437a40c6415368, VirtualShadowMapClipmap.cpp:84–89,175–182,352–458. The source remains in the shared external reference directory and its blob/SHA/bytes are in the established native VSM manifest.
