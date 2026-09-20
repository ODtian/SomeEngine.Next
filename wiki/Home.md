# SomeEngine Wiki

根索引（MOC）。每条笔记聚焦一个概念，通过 `[[wikilink]]` 互联。

## 领域

- [[DRY]] · [[SRP]] · [[Structural-Optimization]] — 设计原则
- [[Engine-Trace]] · [[Profiler-Bridge-Only]] · [[Tracy-Toolchain]] · [[ECS-Architecture]] · [[Product-Boundary]] · [[Render-Boundaries]] · [[Render-Graph]] · [[Asset-Files]] — 架构决策
- [[Engine-Goals]] — 项目目标
- [[visco-semen-liquid-domain]] — 独立原型领域记录

## 资产架构图谱

从 [[Asset-Files]] 进入完整导航。本次统一完成全部现有资产接入和已讨论的序列化不足修正，未涉及的业务架构保持：

- [序列化能力审查](architecture/Assets/Asset-Serialization-Review.md)：现有能力、运行证据、成熟实现对比与十项不足。
- [冻结概念与原则](architecture/Assets/Asset-Rebuild-Principles.md)：21 组原则，明确职责、数据、保证和修改边界。
- [统一执行任务](architecture/Assets/Asset-Rebuild-Tasks.md)：按依赖完成 43 个任务，同时验收全部资产、序列化修正与有效能力回归。

原主题页仅需更新与已讨论变化冲突的内容；未涉及架构继续有效。当前完成的是源码审查与文档范围修正，对应实现任务尚未执行。
