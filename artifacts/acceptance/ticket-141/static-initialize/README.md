# #141 static depth initialization

Pinned native 396c9f059903aed5fec78ecd3d437a40c6415368: VirtualShadowMapPhysicalPageManagement.usf 997–1008 initialize dynamic depth from reusable static depth, while static invalidation clears both. The production clear kernel calls the shared initializer; the feature owns a separate static atlas role with frame boundary tracking and uses main/post merge passes before the corresponding HiZ generation.

Public shader behavior `DynamicDepthStartsFromReusableStaticDepth`: .75 retained static depth produces .75 dynamic initialization, and static invalidation produces zero. Red records the absent source entry. Green records one passed row. Features Release build: zero warnings and errors.

This source slice establishes atlas initialization/merge resources. Following source slices fill caster classification, page dirty flags, static/dynamic raster destinations and receiver-mask lifecycle.
