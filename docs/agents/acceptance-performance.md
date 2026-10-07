# 三场景性能恢复

规格：[#190](https://github.com/ODtian/SomeEngine.Next/issues/190)。统一反馈票为 [#81](https://github.com/ODtian/SomeEngine.Next/issues/81)，其前置实现为 #191、#192、#193。

目标为 P95：1024 运动小球 ≤1ms、Sponza ≤2ms、Amazon Bistro ≤3ms。

当前已测源码 `600dd84f02dd7ce17957f3354faa34313ac8c298`，三场景均 Complete/Measured，非 trace 原始采样 26.7738692 秒，含启动整轮 35.84 秒。

| 场景 | P95（ms） | P50（ms） |
| --- | ---: | ---: |
| 1024 运动小球 | 5.0181 | 4.0747 |
| Sponza | 8.9845 | 8.3055 |
| Amazon Bistro | 10.4675 | 9.5657 |

报告使用 presented-frame elapsed 与 CPU 阶段计时。框架 GPU query 的 trace 定位另行记录。

配置为 1280×720、RTX 3080、.NET 10.0.12、TieredCompilation=false、TAA/async compute/mesh shader 开启、原几何与运动。该记录采用旧 SMRT 7 rays/8 samples/adaptive 1；下一轮记录用户确认的新算法与实际参数。

必要构建后使用 `tools/Measure-Scenes.ps1` 执行一次 ≤60 秒的完整三场景反馈，内容构建另计。原始结果和身份为 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/final/ticket-188/primary-depth-600dd84f/`，任务重组归档为同目录下的 `issue-reorganization-20261007/`。
