# RHI-QUE-008 — Memory and aliasing barriers 

Memory and aliasing barriers are explicit commands and are never inferred from copies, rendering,
dispatch or resource creation. The backend validates Queue legality and exact resource provenance,
then lowers the public operation either to Enhanced Barriers or the legacy D3D12 path.

A Queue release/acquire pair is matched by the caller's synchronization plan. The RHI does not create
a hidden completion or wait to repair a missing handoff. Validation diagnoses mismatched split or
Queue-transfer facts before forwarding valid calls.
^rhi-que-008

