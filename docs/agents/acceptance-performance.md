# 三场景性能恢复

规格：[#190](https://github.com/ODtian/SomeEngine.Next/issues/190)。统一反馈票为 [#81](https://github.com/ODtian/SomeEngine.Next/issues/81)，其前置实现为 #191、#192、#193。

目标为 P95：1024 运动小球 ≤1ms、Sponza ≤2ms、Amazon Bistro ≤3ms。

当前已测源码 `51693b01ad9d2c55d50a4891d427f3788df890b2`，三场景均 Complete/Measured，非 trace 原始采样 42.2431456 秒，含启动整轮 57.26 秒。

| 场景 | P95（ms） | P50（ms） |
| --- | ---: | ---: |
| 1024 运动小球 | 4.2405 | 3.4667 |
| Sponza | 6.0294 | 5.5119 |
| Amazon Bistro | 7.5178 | 6.6603 |

报告使用 presented-frame elapsed 与 CPU 阶段计时。框架 GPU query 的 trace 定位另行记录。

配置为 1280×720、RTX 3080、.NET 10.0.12、TieredCompilation=false、TAA/async compute/mesh shader 开启、原几何与运动。本轮采用 contact-hardening PCSS，每 family 最多 4 次遮挡查询与 8 次过滤；SMRT ray count 为 0，最大半径 8 texels，方向分辨率 bias 为 0，真实页池 2048 页。

必要构建后使用 `tools/Measure-Scenes.ps1` 执行一次 ≤60 秒的完整三场景反馈，内容构建另计。原始结果和身份为 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/final/vsm-selected-189/`，任务重组归档为同目录下的 `issue-reorganization-20261007/`。

对比本轮基点 600dd84f，P95 从 5.0181 / 8.9845 / 10.4675ms 降至 4.2405 / 6.0294 / 7.5178ms。目标继续由 #190 验收。
