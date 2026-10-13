# Hard-shadow behavior and source-geometry quality

## Implementation acceptance

Run the public DirectionalDepthRenderingTests and TargetRasterVisibilityTests on
both hardware and software raster paths. The normal engine LOD selection and
shadow sampling stay enabled. The fixtures use known geometry and public material
outputs; they do not replace the renderer or force a scene LOD.

The native-depth checks require a planar receiver to stay lit without a caster,
an interior receiver to be shadowed by a nearby caster, and that same receiver to
become lit when the caster is removed or its public forward-depth output is moved
behind the receiver. Exterior probes must remain lit. Transparent and patterned
alpha coverage checks compare the same public receivers against their removed
caster controls. These controls reject missing occlusion, false self-shadow,
incorrect depth convention, and ignored user visibility/alpha behavior without
requiring shadow-map edges to equal camera-origin ray-traced edges.

## Source-geometry accuracy gate

DefaultGameRayShadowTests retains its original-GLB ray reference and every raw
comparison metric. That reference traces camera-pixel-center rays against original
source geometry, then launches shadow rays from the source hit with geometric
normal offset. The engine samples a shadow texel-center depth generated from its
normally selected LOD and applies its existing receiver bias. These are different
queries. The retained four actual-caster observations establish intentional LOD
and texel-center footprint differences for those four samples only. They do not
classify every image disagreement as correct.

All current scene/mode cases require source-geometry mismatch and mean visibility
error at or below 2%, alongside the existing receiver-coverage and self-hit checks.
DefaultGameRayShadowTests asserts those limits directly;
MeetsSourceGeometryAccuracyThresholds in comparison.json records the same limits.
The selected-geometry/texel-center distinction does not waive any source receiver
or relax the accuracy gate.

## Historical Bistro profile (inactive)

The profile and its CPU policy tests below remain as historical diagnostic evidence.
They are not called by the current renderer acceptance test and do not replace its 2% limits.

The checked-in Bistro/default profile records three historical captures.
The two historical attribution captures have 36,556 mismatches out of 795,215
receivers. A fresh capture of the final original-queue implementation has 36,557:
only one additional missed shadow, at reference-boundary pixel (1073, 291). The
raw image differs at five pixels; the eligible population and every interior
error count are unchanged. The mapped-normal diagnostic flag also changes at
that boundary pixel. This bounds the observed difference but does not establish
a floating-point or triangle-ordering root cause.

The historical profile envelope is 30,963 missed shadows, 5,592 false shadows, two missing
rendered receivers and 36,557 total mismatches, with the recorded mean-error
maximum. Interior and boundary populations and error channels are also checked
independently: interior remains limited to 1,701 mismatches (1,620 missed and 81
false), while boundary is limited to 34,856 (29,343 missed and 5,511 false). Moving
the one accepted boundary difference into the interior fails even if global
totals do not change. No boundary mask is enlarged and no pixel is excluded.

This historical profile is not a current acceptance baseline or proof that every
differing pixel is correct. The three
observations do not establish a universal run-to-run noise distribution. Each
channel uses its observed maximum without percentage padding; an improvement
in one channel or region cannot pay for a regression in another.

The profile is never updated by a test. The source geometry hash, fixed frame
configuration, mesh asset IDs and transform bits, resolution, backend, camera,
lights, shadow/temporal options, and
machine profile must match before it is used. A changed configuration needs a
separately reviewed baseline. All current scene/mode cases, including Bistro/default,
retain their original 2% mismatch and mean-error accuracy limits, in addition to
their coverage/self-hit checks. They do not use this Bistro-specific numeric bound.
Any future acceptance change requires explicit review; CPU controls alone do not authorize it.

CPU policy tests accept all three recorded normal observations and reject the retained
37,758-mismatch earlier result, changed eligible population, each independently
worsened global or regional channel, non-finite error, crossed missed/false
regressions, an unchanged-global-total interior regression, and extreme
all-lit/all-shadow/missing-output counterexamples. Renderer behavior is verified
by the public scene/material controls above, rather than production fault hooks.

The baseline JSON records provenance. Validation artifacts distinguish the earlier
attribution captures from the final original-queue depth/transparent regression.
A source-quality metric passing its regression profile is not proof of exact
selected-triangle or texel-sample identity across every pixel.
