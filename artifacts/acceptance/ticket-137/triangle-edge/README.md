# Source triangle edge — ticket 137

One public asset behavior: ThreeFourFiveTriangleCarriesItsLongestSourceEdge. The source triangle has positions (0,0,0), (3,0,0), (0,4,0); its independently known longest edge is five. ClusterBuilder.ProcessRaw and the public mesh/page output supplied zero before the producer and five after it. CPU red failed one case and green passed the same case in 350 ms. The default rule check returned zero violations.

Importer owns this geometric value. It measures source edges in double precision and rounds the stored half value upward. PackedGroup's high 16 bits carry that half; its low 16 bits continue carrying the signed reduction group. PackedCluster retains 96 bytes. The source-build importer version recooks content after this producer change, and the generated glTF recipe/shader identities accompany this commit.

Shadow HW/SW uses the decoded source maximum edge, maximum instance scale, finite bounds expansion and its own sphere projection scale. It consumes the per-view 32-pixel hardware threshold. Current raster bounds enforce the 64-pixel SW work capability. Older source pages with zero edge metadata take the hardware path. The loader reads the new data interpretation through its existing mesh ingestion; vertex bytes remain opaque and are interpreted by the user shader.

The existing content build regenerated 68 assets after the source, C#/shader data and consumer changes. The existing whole-image off-camera case remains the integrated behavior seam; this CPU case proves the new edge producer against independent literal geometry.

Native authority: EpicGames/UnrealEngine 396c9f059903aed5fec78ecd3d437a40c6415368, NaniteClusterCulling.usf:255–308 and NaniteShared.cpp:195–202. Native HW selection uses actual triangle EdgeLength and its own view LODScaleHW. Both main and post stages now call the same edge consumer.
