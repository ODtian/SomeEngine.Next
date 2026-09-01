# RHI-CAP-001 — Discovery, absence and errors 

Optional functionality is represented by typed `DeviceCapability` objects returned from
`TryGetCapability<TCapability>`. Absence is an ordinary `false` result. Operations that require an
absent capability throw `NotSupportedException`; they do not emulate a different feature silently.

`DeviceFeatures` appears only in `DeviceDesc.RequiredFeatures` and `OptionalFeatures`. It requests
Device creation behavior and is not a post-creation capability database.

The current typed capability set includes Presentation, PipelineCreationSupport, SparseResources,
SamplerFeedback, Residency, RayTracing, MeshShaders, VariableRateShading, WorkGraphs,
IndirectCommands, CalibratedTimestamps, LinkedAdapters, ExternalResources and ExternalTimelines.
^rhi-cap-001

