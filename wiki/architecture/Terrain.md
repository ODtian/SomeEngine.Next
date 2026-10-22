# 地形

本专题导航维护已实现的地形数据职责和公开接口用法。术语见[地形术语](../../GLOSSARY.md#地形)，对象入口为 [Terrain](../../src/SomeEngine.Resources/Terrain/Terrain.cs)。

| 专题 | 内容 |
| --- | --- |
| [CPU 字段](terrain/Terrain-Fields.md) | 高度场与体素场的创建、坐标、CPU 编辑、固定版本读取、共享与复制 |
| [放置与 CPU 分块](terrain/Terrain-Scene.md) | 高度表面、固定 LOD 空间块、共享放置、局部构建与应用呈现 |

字段编辑使用现有 [Job 依赖与资源访问](ECS/Readiness-Admission.md)。领域阅读入口见[领域规则](../../docs/agents/domain.md)。
