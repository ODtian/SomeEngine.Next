# Render Graph

> Status: authoritative target design; implementation is not conforming until
> [[Render-Graph-Conformance#Acceptance evidence]] passes.

This page is the Render Graph map of content. This page and exactly the contract notes listed below
form the sole Render Graph design contract. Notes outside this set may reference the contract but may
not restate, specialize, or override it. Source code, tests, review comments, chat transcripts,
benchmarks, and diagnostic artifacts are conformance evidence rather than parallel design authority.

The contract defines the end state. It contains neither migration phases nor a history of review
findings. Every review and every later design change cites the stable RG identifier whose definition
it challenges.

## Navigational summary

The following statements are navigation, not duplicate definitions. Their linked clauses own the
exact meaning:

- every owned word has one meaning and every owned type must prove an independent semantic/lifetime
  invariant; forwarding and aggregation wrappers are forbidden:
  [[Render-Graph-Vocabulary-and-Type-Admission]];
- every frame authors and compiles a new graph, and no executable topology survives the invocation:
  [[Render-Graph-Frame-Invocation#RG-CORE-001 — One frame invocation, no topology cache]];
- user callbacks issue graphics commands directly through stack-only pass command scopes; Render
  Graph has no command IR:
  [[Render-Graph-Frame-Invocation#RG-CORE-002 — No Render Graph command IR]];
- users declare complete resource use and never author barriers; shipping correctness derives all
  synchronization from the current graph:
  [[Render-Graph-Frame-Invocation#RG-CORE-003 — Automatic synchronization]] and
  [[Render-Graph-Frame-Authoring#RG-AUTH-007 — Exact pass effects and raster state]];
- transient, persistent, history, descriptor, upload, readback, and external-resource storage reuse
  comes only from concrete RHI placement and lifetime facts:
  [[Render-Graph-Physical-Resources#RG-RES-001 — RHI cache boundary]];
- admission, partial Submit acceptance, resource publication, and swapchain ownership have explicit
  Status transitions and resource-State commit rules:
  [[Render-Graph-Submission-and-Presentation]];
- both Render Graph CPU measurements must satisfy the certification profile's symbolic `1X%` limit without
  subtracting native-call savings:
  [[Render-Graph-Diagnostics-Limits-and-Performance#RG-EVID-003 — CPU overhead contract]].

## Contract notes

| Contract note | Sole responsibility |
|---|---|
| [[Render-Graph-Vocabulary-and-Type-Admission]] | Canonical vocabulary, forbidden synonyms, suffix meanings, public type whitelist, and anti-wrapper rule |
| [[Render-Graph-Frame-Invocation]] | Frame lifetime, no topology cache, no command IR, automatic synchronization authority, Device domain, and backend propagation |
| [[Render-Graph-Frame-Authoring]] | Stack builder, callback ABI, identities, exact declarations, pass command capabilities, shader closure, and pass memory epoch |
| [[Render-Graph-Resource-Dependencies]] | Content lineage, Buffer/Texture dependency construction, observable roots, and culling |
| [[Render-Graph-Queue-Synchronization]] | Queue assignment, Queue-local pass ordinals, submission formation, barriers, ownership transfer, and synchronization quality |
| [[Render-Graph-Physical-Resources]] | Directly retained RHI objects, placement/aliasing, persistent/history resources, descriptors, transfers, residency, and external resources |
| [[Render-Graph-Submission-and-Presentation]] | Admission, submission acceptance, physical-state commit, publication, presentation, completion, and parallel recording |
| [[Render-Graph-Advanced-Operations]] | Sparse resources, ray tracing, feedback, variable-rate shading, Work Graphs, queries, indirect work, and raster merging |
| [[Render-Graph-Diagnostics-Limits-and-Performance]] | Direct diagnostic delivery, hard limits, bounded algorithms, and CPU certification |
| [[Render-Graph-RHI-Requirements]] | Portable RHI additions required by the Render Graph contract |
| [[Render-Graph-Conformance]] | Assembly boundary, executable acceptance evidence, and invariant checklist |

## Authority and naming

RHI facts are linked instead of copied. Render Graph inherits these upstream contracts directly:

- automation follows information ownership:
  [[RHI/Core-Contract#RHI-CORE-003 — RHI and Render Graph automation boundary]];
- S#/Slang remains the sole shader authority:
  [[RHI/Core-Contract#RHI-CORE-004 — Slang/S# is the sole shader authority]];
- successful calls, expected branches, failures, and exceptions keep the RHI transport shapes:
  [[RHI/Core-Contract#RHI-CORE-005 — Error transport]];
- stable hot paths keep the RHI allocation boundary:
  [[RHI/Core-Contract#RHI-CORE-006 — Allocation boundary]];
- every SomeEngine-owned symbol obeys the RHI controlled vocabulary:
  [[RHI/Core-Contract#RHI-CORE-007 — Controlled vocabulary]];
- public disposal, physical retirement, diagnostics, and concurrency keep the RHI lifetime contract:
  [[RHI/Lifetime-Concurrency-and-Diagnostics]]; and
- native acceptance, command lifetime, Queue completion, and acquired-image rights remain owned by
  [[RHI/Queue-and-Commands]] and [[RHI/Presentation]].

The exact Render Graph-specific word choices, forbidden synonyms, and type-admission decisions live
only in [[Render-Graph-Vocabulary-and-Type-Admission]]. That note narrows the RHI vocabulary for this
domain; it does not replace, copy, or compete with the upstream contracts.

[[Render-Boundaries]] owns only assembly relationships and points back here. It contains no second
Render Graph API or behavior contract. The RHI notes own explicit direct-RHI semantics; the notes
listed above own graph-wide automation.

## Reading rule

Every `RG-*` identifier is globally unique in this contract and has exactly one definition. A link
to an identifier is a dependency on that definition, not permission to paraphrase it as a second
rule. If a summary conflicts with an identified clause, the identified clause wins and the summary
is a documentation defect. Replacing a clause requires updating its definition and every affected
wikilink in the same change.
