# Rendering Architecture

本页只负责导航。渲染架构按可独立演进的概念拆分；每条设计事实只有一个定义页，其他页面只链接，不重复定义。

## Render Graph
- [[architecture/render-graph/Retained-Graph|Retained Graph]]
- [[architecture/render-graph/Structural-Changes|Structural Changes]]
- [[architecture/render-graph/Frame-Data|Frame Data]]
- [[architecture/render-graph/Graph-Identity|Graph Identity]]
- [[architecture/render-graph/Graph-Versioning|Graph Versioning]]
- [[architecture/render-graph/Resource-Access|Resource Access]]
- [[architecture/render-graph/Shader-Resource-Closure|Shader Resource Closure]]
- [[architecture/render-graph/Range-Completions|Range Completions]]
- [[architecture/render-graph/Incremental-Reconciliation|Incremental Reconciliation]]
- [[architecture/render-graph/Incremental-Hazards|Incremental Hazards]]
- [[architecture/render-graph/Incremental-Scheduling|Incremental Scheduling]]
- [[architecture/render-graph/Transient-Placement|Transient Placement]]
- [[architecture/render-graph/Explicit-Compaction|Explicit Compaction]]
- [[architecture/render-graph/Extension-Points|Extension Points]]

## Assets and device realization
- [[architecture/assets/Asset-Object-Identity|Asset Object Identity]]
- [[architecture/assets/Hot-Reload|Hot Reload]]
- [[architecture/assets/Asset-Update-Boundary|Asset Update Boundary]]
- [[architecture/assets/Texture|Texture]]
- [[architecture/assets/Bootstrap-Streaming|Bootstrap Streaming]]
- [[architecture/assets/Texture-Bootstrap|Texture Bootstrap]]
- [[architecture/assets/Texture-Device-Realization|Texture Device Realization]]
- [[architecture/assets/Texture-View-Cache|Texture View Cache]]
- [[architecture/assets/Missing-Resource|Missing Resource]]
- [[architecture/rendering/RenderDevice|RenderDevice]]

## Shader and material
- [[architecture/shader/Slang-Authority|Slang Authority]]
- [[architecture/shader/Shader-Build|Shader Build]]
- [[architecture/shader/Shader-Composition|Shader Composition]]
- [[architecture/shader/Shader-Block-Residency-Refactor|Shader Block and Residency]]
- [[architecture/shader/Shader-Resource-Effects|Shader Resource Effects]]
- [[architecture/shader/User-Stage-Interfaces|User Stage Interfaces]]
- [[architecture/shader/Conservative-Bounds|Conservative Bounds]]
- [[architecture/shader/Geometry-Invocation-Requirements|Geometry Invocation Requirements]]
- [[architecture/material/Material|Material]]
- [[architecture/material/Material-Slots|Material Slots]]
- [[architecture/material/Stage-Compatibility|Stage Compatibility]]
- [[architecture/material/MaterialBindless|MaterialBindless]]
- [[architecture/material/Bindless-Resource-Recipe|Bindless Resource Recipe]]
- [[architecture/material/Bindless-Descriptor-Lifetime|Bindless Descriptor Lifetime]]

## Cluster geometry
- [[architecture/cluster/Cluster-Scope|Cluster Scope]]
- [[architecture/cluster/Cluster-Retained-Graph|Cluster Retained Graph Usage]]
- [[architecture/cluster/LOD-DAG|LOD DAG]]
- [[architecture/cluster/Spatial-BVH|Spatial BVH]]
- [[architecture/cluster/Material-Ranges|Material Ranges]]
- [[architecture/cluster/Opaque-Vertex-Data|Opaque Vertex Data]]
- [[architecture/cluster/Vertex-Encoding|Vertex Encoding]]
- [[architecture/cluster/Cluster-Deform-Compatibility|Cluster Deform Compatibility]]
- [[architecture/cluster/Deform-Cache|Deform Cache]]
- [[architecture/cluster/Deform-Cache-Allocation|Deform Cache Allocation]]
- [[architecture/cluster/Software-Raster|Software Raster]]
- [[architecture/cluster/Vertex-Reuse|Vertex Reuse]]
- [[architecture/cluster/Hardware-Raster|Hardware Raster]]
- [[architecture/cluster/Raster-Execution|Raster Execution]]
- [[architecture/cluster/Tessellation|Tessellation]]
- [[architecture/cluster/Tessellation-Reconstruction|Tessellation Reconstruction]]
- [[architecture/cluster/Worklists|Worklists]]
- [[architecture/cluster/Overflow|Overflow]]

## Cluster streaming
- [[architecture/cluster-streaming/Page-Identity|Page Identity]]
- [[architecture/cluster-streaming/Resident-Page-Address|Resident Page Address]]
- [[architecture/cluster-streaming/Page-Heap|Page Heap]]
- [[architecture/cluster-streaming/Page-Install|Page Install]]
- [[architecture/cluster-streaming/Page-Eviction|Page Eviction]]
- [[architecture/cluster-streaming/BVH-Leaf-Patching|BVH Leaf Patching]]
- [[architecture/cluster-streaming/Resident-Ranges|Resident Ranges]]
- [[architecture/cluster-streaming/Page-Faults|Page Faults]]
- [[architecture/cluster-streaming/Residency-Usage|Residency Usage]]
- [[architecture/cluster-streaming/Streaming-Failure|Streaming Failure]]
- [[architecture/cluster-streaming/Streaming-Shutdown|Streaming Shutdown]]

## World and features
- [[architecture/rendering/GameWorld-and-RenderWorld|GameWorld and RenderWorld]]
- [[architecture/rendering/Extraction|Extraction]]
- [[architecture/rendering/Frame-CPU-Overlap|Frame CPU Overlap]]
- [[architecture/rendering/Delayed-Feedback|Delayed Feedback]]
- [[architecture/rendering/Multi-View|Multi-View]]
- [[architecture/features/Virtual-Shadow-Maps|Virtual Shadow Maps]]
- [[architecture/features/Lighting|Lighting]]
- [[architecture/features/Motion|Motion]]
- [[architecture/features/Ray-Tracing-Bridge|Ray Tracing Bridge]]
- [[architecture/features/Debug-Rendering|Debug Rendering]]
- [[architecture/features/Translucency|Translucency]]
- [[architecture/features/Streaming-Feedback|Streaming Feedback]]

## Performance and checking
- [[architecture/validation/Validation|Validation]]
- [[architecture/validation/Development-Checks|Development Checks]]
- [[architecture/validation/External-Input-Checks|External Input Checks]]
- [[architecture/validation/Memory-Safety-Guards|Memory Safety Guards]]
- [[architecture/validation/Execution-Failures|Execution Failures]]
- [[design/Structural-Optimization|Structural Optimization]]
- [[design/Type-Admission|Type Admission]]
- [[design/Naming|Naming]]




