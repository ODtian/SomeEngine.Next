# RHI-DSC-003 — Descriptor object retention 

A table stores public binding identity in CPU staging. Command recording resolves the current native
descriptor data and captures every resource, view, Sampler, counter resource and descriptor-generation
lease required by the command.

Disposing a referenced view prevents future use, but it cannot invalidate already accepted commands.
Disposing a DescriptorTable ends future writes/index use and releases table storage after captured
native generations retire. Correct physical retention is unconditional and is not selected per
Device or per submission.
^rhi-dsc-003

