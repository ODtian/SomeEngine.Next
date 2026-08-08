---
status: accepted
---

# Admit resource ownership only after semantic dependencies are ready

SomeEngine admits a job's resource accesses only after its explicit semantic dependencies have completed, so an unready job pins required lifetimes without reserving a place in the resource frontier. Fixed access sets admit atomically; an ECS query uses a constrained preparation protocol that holds sorted World sequencers, registers topology guards, resolves stable ranges, then atomically seals the remaining set before publishing the owner as ready, with rollback at every pre-seal failure. Resource hazards wait only for the predecessor's work release and do not propagate failure; explicit dependencies wait for full scope completion and require predecessor success by default, with an explicit after-completion policy for sequencing recovery/checkpoint work. Schedule-time reservation was rejected because dynamic child scopes can turn it into deterministic wait cycles, while treating submission order as freshness would conflate mutual exclusion with happens-before semantics.
