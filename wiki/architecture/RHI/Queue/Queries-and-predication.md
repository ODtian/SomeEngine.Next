# RHI-QUE-009 — Queries and predication 

Query pools are typed by query kind and Queue family. Begin/end queries, timestamps, resolves and
Pipeline/stream-output statistics use explicit slots and destination Buffer ranges. Resolve checks
query count, destination stride, usage and bounds before recording.

Predication names a Buffer, offset and operation. Clearing predication passes a null Buffer. Query and
predication operations do not add barriers or waits; the caller supplies synchronization through the
ordinary barrier and Queue mechanisms.
^rhi-que-009

