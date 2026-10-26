# 百万体活动场景创建数据

本页维护当前百万体目录、创建参数和公开首帧入口。
源码入口为 [PhysicsScaleScenes](../../../samples/DefaultGame/PhysicsScenes/PhysicsScaleScenes.cs)、
[创建数据](../../../samples/DefaultGame/PhysicsScenes/PhysicsMillionActivityScene.cs)
及 [公开步行为](../../../tests/SomeEngine.Runtime.Tests/MillionPhysicsActivityTests.cs)。

`PhysicsScaleScenes.All` 的 `million-activity` 条目选择 100 × 500 列、每列 20 层的盒体。
创建入口将索引解析成原 Body 实体，参数与当前状态沿共同组件及其设备表示持有。

| 创建项 | 数值 |
| --- | --- |
| 动态盒体数 | 1,000,000 |
| 单体尺寸、质量 | 0.5 × 0.5 × 0.5 m，1 kg |
| 列中心间距 | X、Y 均为 0.75 m |
| 层中心间距 | 0.55 m |
| 最底层中心高度 | 0.3 m |
| 静态底板 | 77 × 377 × 1 m，中心 Z=-0.5 m |
| 固定步、重力 | 1/60 s，世界 Z 方向 -10 m/s² |

第 180 步向中部列的顶层主体输入 X 方向 2 m/s 的速度增量。
共同创建入口按原质量将它记录为 `BodyImpulse`，原时段输入阶段作用当前设备速度。
第 360 步将底板位置设为 `(0.025, 0, -0.5)`，沿原组件输入和共享参考端进入活动阶段。

公开首帧入口调用 `PhysicsScenes.CreateDevice`，显式给出网格、接触与读取范围。
该入口选择 16 轮 AVBD、每体 2 轮、0.02 m 接触距离和每步 1000/60 ms 预算声明；
网格容量为 1,000,001 个主体、8,000,008 个单元、16,000,017 个桶、1,000,000 个 pair，
接触容量为 4,000,000 个点。位姿与速度读取主体索引 1、500,001 和 1,000,000。

全局活动体数、接触点数和最大连通体数通过同一设备归约交回，
选定主体的位姿与速度继续写回原实体。阶段和完成消费见[统一场景平台](Physics-Scenes.md)。
