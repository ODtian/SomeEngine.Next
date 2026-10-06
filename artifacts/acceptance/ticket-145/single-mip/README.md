# Directional clipmaps use one mip (#145)

Fixed native UE5.8.3 ref396c9f059903aed5fec78ecd3d437a40c6415368: VirtualShadowMapArray.cpp4958–4965 creates directional TargetMipCount1; VirtualShadowMapPerPageDispatch.ush61–76/100–112 limits directional page iteration to mip0. PageMarking.usf829–860 requests four origin pages at mip0 for coarse clipmap views. The tracked source manifest is represented by the #70 native audit records outside repository.

One public PrepareViews case uses two directional clipmaps and a512/128 local Spot map. Independent expected mip counts are [1,1,3]. Actual unchanged production6c212b9b red was [3,3,3],1failed; identical fixture after fix was [1,1,3],1passed,0skips. Both default rule checks reported0violations. Necessary source build succeeded7.03s,0warnings/errors. Actual TRX and red executable identity are included.

Producer view count1 governs page decoding, sampling, invalidation and quality. Compact output filters each view's valid mip range before emitting raster work; maximum-sized backing buffers retain the existing global capacity stride. Allocation uses the same view decoder. Depth completion, page clear and HiZ dispatch only the allocator's actual dirty physical pages. Extra last-directional higher-mip marking and its fractional scrolling invalidation branch are removed. Local full mip chains retain their existing domain.

Coarse-origin coordinates and configurable native coarse modes continue in the next #145 slice. Broader shader/backend/default-scene execution belongs to the #70 unified final collection.
