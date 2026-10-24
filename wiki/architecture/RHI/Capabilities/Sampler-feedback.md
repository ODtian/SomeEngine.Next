# RHI-CAP-003 — Sampler feedback 

`SamplerFeedback` reports tier, granularity, supported sampled/feedback formats and native alignment.
A `SamplerFeedbackTexture` is created for one sampled Texture and one feedback type. Its UAV names the
exact sampled and feedback resources required by the native descriptor.

Clear and resolve are explicit command operations. They do not decode feedback, change sparse
mappings, add barriers or submit hidden work. The command capture retains the sampled Texture,
feedback Texture, views and resolve destination through Queue completion.
^rhi-cap-003

