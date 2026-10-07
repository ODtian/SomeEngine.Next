# #70 RenderGraph 与帧资源验收

规格：[RenderGraph 区段与帧资源单一持有者](https://github.com/ODtian/SomeEngine.Next/issues/70)。实现票为 #71–#80，集成分支为 `codex/render-graph-regions-70`，交付为 PR #136。

本规格维护区段树、当帧区段、帧阶段骨架、帧上传和回读、实例存储与渲染数据读写锁。公开行为结果采用已保存的缓冲、纹理、回读、统计和状态检查；后续按实际源码修改做最小必要验证。

VSM 由 [#189](https://github.com/ODtian/SomeEngine.Next/issues/189) 独立推进，见 [VSM 验收](acceptance-vsm.md)。渲染性能由 [#190](https://github.com/ODtian/SomeEngine.Next/issues/190) 与 [#81](https://github.com/ODtian/SomeEngine.Next/issues/81) 独立验收，见 [性能记录](acceptance-performance.md)。

原验收记录、派生票正文、讨论和依赖保存于 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/final/issue-reorganization-20261007/`。
