# RHI-QUE-002 — Completion value domain 

`QueueCompletion` is the exact pair of a borrowed Queue identity and a nonzero monotonically assigned
value from that Queue. Values from different Queues are not interchangeable even when their integers
match.

`IsComplete` and `WaitCpu` observe that Queue's private completion fence. `CollectCompleted` releases
retired submissions, descriptor generations, presentation generations and capability payloads whose
completion has passed. Completion collection never changes public command results.
^rhi-que-002

