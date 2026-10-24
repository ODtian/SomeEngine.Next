# RHI-QUE-006 — Exact state-suppression policy 

Only setters whose native effect is fully represented by the current command-state shadow may be
suppressed. Equality is exact for the public value domain, including bitwise floating-point policy
where required.

Draw, dispatch, copy, clear, barrier, query, event, bundle, indirect, DXR, Work Graph and presentation
operations are never suppressed as future-state setters. Pipeline switches invalidate only the state
that the native API invalidates. Descriptor heap or root-signature changes invalidate the affected
native binding shadow without inventing a public compatibility value.

All state arrays and capture storage are prepared before native encoding. At stable high-water
capacity, repeated state-setting and command recording perform no managed allocation, reflection walk
or string lookup.
^rhi-que-006
