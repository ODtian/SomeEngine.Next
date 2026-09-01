# Ray Tracing Bridge

Native ray tracing 是 geometry/material 能力的 consumer 或独立 feature，不因消费 Cluster 数据而成为 Cluster core。

其 acceleration structure、shader table 与 dispatch 仍使用普通 RHI/RenderGraph 能力。