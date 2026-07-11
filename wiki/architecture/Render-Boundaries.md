# Render Boundaries

Render migration is split by responsibility rather than by legacy directory layout.

## Render Domain

`SomeEngine.Render` is the backend-free render domain. It may contain render-facing ECS components, CPU-side render-world state, material and asset semantics, temporal settings, and renderer-independent data contracts.

`MaterialPass`, `PassEntry`, and `PassVersion` are accepted material/asset semantics when they stay detached from RenderGraph execution and GPU resources.

It must not depend on legacy RHI, D3D12/Direct3D, RenderGraph, Editor renderer integration, windowing, ImGui, GPU handles, command buffers/encoders, descriptor/root-signature/resource-binding concepts, pipeline state, pass scheduling, pipeline caches, or present/swapchain code. A product `Pipelines` folder is not part of the first-round Render domain.

## Cluster Renderer

`SomeEngine.Render.Cluster` is a backend-free Cluster renderer domain/model. It may contain cluster options, debug modes, BVH/page models, page streams, upload payload descriptions, slot layouts, material planning, and shader identity sets.

Cluster execution is not part of the current product boundary. RenderGraph passes, GPU resources, command buffers/encoders, descriptor/root-signature/resource-binding concepts, RHI/D3D12 bindings, debug readback, and pipeline runtime code are future Render execution work against accepted Next RHI and RenderGraph boundaries.

A Cluster product `Pipelines` folder is likewise not part of the first-round Cluster domain/model.

## Legacy code

Legacy Render execution and legacy RHI are reference material only. They must not be copied into the accepted product graph to satisfy compilation. The target Render Graph execution boundary is defined independently in [[Render-Graph]]; importing checkpoint code does not make that code an accepted implementation of the boundary.

参见 [[Product-Boundary]]、[[Harness-Definition]]。
