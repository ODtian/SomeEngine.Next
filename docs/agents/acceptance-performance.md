# 三场景性能恢复

规格：[#190](https://github.com/ODtian/SomeEngine.Next/issues/190)。统一反馈票为 [#81](https://github.com/ODtian/SomeEngine.Next/issues/81)，本轮交付为 #189/#201。

目标为 P95：1024 运动小球 ≤1ms、Sponza ≤2ms、Amazon Bistro ≤3ms。

当前 #190 继续实现，#189 的 RT GT 继续验收。下表属于 `e3a97e66` 的旧提交记录；随后 RT GT 定位到该提交的 Sponza 七层几何只遍历六层，主体接收面覆盖为 18.55%。最新集成 `98f70878` 已恢复完整遍历，修复阴影深度精度并持久化材质资源连接。后续统一性能反馈采用修复后的完整几何与正确阴影结果。

当次源码 `e3a97e66c1c78dea5f4136f6d12393a1e52207ad`，三场景均 Complete/Measured，非 trace 原始采样 26.2796009 秒，含启动整轮 34.21 秒。

| 场景 | P95（ms） | P50（ms） |
| --- | ---: | ---: |
| 1024 运动小球 | 5.3710 | 4.6609 |
| Sponza | 3.4827 | 2.0072 |
| Amazon Bistro | 6.3762 | 5.5435 |

报告使用框架 presented-frame elapsed 与 CPU 阶段计时。三景 queued/active loads、backpressure 和 dropped faults 均为 0；驻留页分别为 7/7、103/103、2586/2996，Bistro 按当前可见需求完成流送。

配置为 1280×720、RTX 3080（driver 32.0.16.1074）、.NET 10.0.12、TieredCompilation=false、固定 1/60、3 秒预热及 2 秒采样、TAA/async compute/mesh shader 开启，采用原几何与运动。软采样每 family 最多 4 次遮挡查询与 8 次过滤，最大半径 8 texels；SMRT 搁置，三景 distant prefilter 关闭，几何预算 256MiB。

使用 `tools/Measure-Scenes.ps1` 完成当次 ≤60 秒的三景反馈，内容构建另计。原始 `results.json`、`identity.json`、应用与 2402 项内容文件身份、Apps/Library 保留于 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/final/cluster-189/ticket-201/`。

前次源码 `51693b01ad9d2c55d50a4891d427f3788df890b2` 的 P95 4.2405 / 6.0294 / 7.5178ms 作为历史基准。#190 继续按 1/2/3ms 目标推进。

首轮反馈在 Bistro 初始化阶段达到脚本时限，其日志独立保留。随后单 Bistro 框架 trace 将热点定位到流送 fixup 的碎片状态区间：StateUpdateConstruction 13.829s、ResourceHazards 1.995s。合并 cluster header 访问及 240B 层级节点上传区间后完成上表反馈；诊断 trace 位于 `ticket-201/diagnostic/`，性能数据取上表非 trace 结果。
