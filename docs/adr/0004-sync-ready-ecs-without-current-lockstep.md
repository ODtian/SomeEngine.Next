---
status: accepted
---

# Keep ECS synchronization-ready without making lockstep a current requirement

SomeEngine does not currently require network synchronization or cross-platform bitwise lockstep, but the ECS must preserve stable logical identities, canonical serialization, replayable structural journals, deterministic structural ordering, and explicit fixed-partition execution hooks so a future synchronization or rollback layer can be added without replacing resource ownership, queries, relations, or hierarchy. Scheduler timing and arbitrary floating-point jobs remain outside the default deterministic contract; a future sync layer adds input ordering, state hashing/snapshots, deterministic math, and fixed execution policies above the same ECS model.
