# Project Graph

`SomeEngine.slnx` is the current build/test project graph. Wiki does not copy a second exhaustive project list that can silently drift.

Architecture pages define dependency direction and responsibilities; the solution file defines which projects currently participate in the default build/test graph.

Renderer projects currently include Graphics, RenderGraph, Render, MaterialBindless, Cluster, Cluster Pipeline and Runtime alongside their tests/backends; their architectural responsibilities are defined in [[architecture/Rendering]].
