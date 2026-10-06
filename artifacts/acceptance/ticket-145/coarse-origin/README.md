# Directional coarse coverage follows the actual origin (#145)

UE5.8.3 fixed ref396c9f059903aed5fec78ecd3d437a40c6415368, VirtualShadowMapPageMarking.usf829–860: project ClipmapWorldOriginOffset through the snapped shadow projection, multiply UV by base page dimensions, then surround pageAddress-.5 with floor/ceil. All four requests use mip0 and their receiver masks follow those same addresses.

Independent example: native absolute level6 radius128cm has projection half-width256cm. Camera51.2cm remains in snapping center0 and has NDC(.2,.2), UV(.6,.4). Eight pages per row give pageAddress(4.8,3.2): surrounding pages x4/5,y2/3. Four authored receiver positions occupy those four pages; a known occluder depth requires visibility[0,0,0,0].

One public RHI/user-shader case executes coarse marking, actual allocation, depth completion and sampling of the known plane. Actual fixed-center source at3e9b4e26 gave[0,1,1,1],1failed; same test body/DLL with origin projection gave[0,0,0,0],1passed,0skips. Default rule checks0. Fixture CPU build26.23s/0warnings/errors; both device runs compile the actual Slang stages, including the changed marker. Red/green fixture and DLL hashes match; only the marker shader hash changes.

The existing208B ClipmapWorldOrigin is the only origin data: perspective camera and orthographic camera target production already supply it. Marker reads that origin through the same LightViewProjection used by sampling, cache and geometry. Existing count fixtures now supply an authored identity projection; the new regression asserts only public shadow visibility.
