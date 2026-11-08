# Frame CPU Overlap

## Definition
GameWorld 下一轮 CPU 工作可以与已经交付给渲染侧的上一轮 CPU/GPU 工作重叠。

## Invariants
- render prepare 完成后 renderer 不再依赖该轮 GameWorld 的活跃访问。
- Game N+1 不需要等待 Render N GPU 完成。
- 需要设备反馈的决策只能作用到未来 frame。

## 应用帧的完成链

`RuntimeApplication` 的帧协调线程推进本轮更新步骤。`GameFrameSystems` 按现有阶段顺序调用
系统，阶段返回 `SystemGroup.CompleteAsync()`，其结果覆盖该组已经提交的 roots 及其子工作。
协调线程在 Game N+1 首个计算尚待完成时保留更新枚举器与当前完成信号，继续既有帧资源等待；
下一轮 Game 读取边界消费信号后继续推进。生命周期回调、结构修改与提取交接均在此线程执行。

实际计算仍由系统声明的 WorldJobs / Job 入口提交。roots 由各 `JobLifetime` 持有，
`CompleteAsync` 使用原 root 观察与归还路径聚集失败；等待结束后系统组继续接受更新。
`RuntimeWait.WaitForCompletion` 把尚待完成的 ValueTask 交给既有外部 fence 入口，等待期间处理窗口消息。

游戏阶段完成后执行提取；提取范围计算与汇合结束后才交付 Rendering world 更新。
Render prepare 与 frame 系统在各自能力范围有效期间等待其实际 roots，随后释放写入范围、
提交 prepare 或完成图录制。关闭路径先推进最后一轮游戏步骤至完成，再释放两个 World 及渲染资源。
Game N+1 的计算继续与已提交的 Render N GPU 工作重叠。

源码入口为 `src/SomeEngine.Runtime/RuntimeApplication.cs`、`GameFrameSystems.cs`、
`RuntimeWait.cs` 与 `src/SomeEngine.Render/Systems/RenderSystemGroups.cs`。

## Relations
[[Extraction]] · [[Delayed-Feedback]]
