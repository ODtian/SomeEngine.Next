# #70 验收记录

交付：[PR #136](https://github.com/ODtian/SomeEngine.Next/pull/136)。实现起点为 `be6be444f639d9e786914d4b3661f8b8e9441bd0`，对应目标分支 `codex/someui-base-7b03495`。

UE 原版统一采用 Epic UE5.8.3 `396c9f059903aed5fec78ecd3d437a40c6415368`。GitHub 提交说明为 `5.8.3 release`；原版文件的 GitBlob、SHA-256、字节数和实际调用位置随各票记录。本项目修复前的源码用于对应行为的 before/after。

## 原始输出

逐像素 CSV、回读 JSON、TRX、图片、trace 和构建日志统一存放于仓库外验收归档。源码变更保留公开行为用例、域文档和结果摘要。`artifacts/`、`TestResults/` 等生成目录沿根级 `.gitignore` 排除。

截至 `95285a3b9209d1ad85988690ab06d749967a4594` 的完整原始输出：

- 本地归档：`F:/SomeEngine.AgentWork/rg70-delivery-evidence/acceptance-95285a3b.zip`
- 大小：18,406,485 bytes。
- SHA-256：`B416DA58CDB4E2F4FAF9835CF2FA18BE624B78BC3F71C3D5D10882A7B98C0F8C`。
- 原始目录结构与文件名保留，包含各票实际命令、TRX 和必要图像。

## 当前直接行为结果

| 任务 | 生产行为与实际结果 |
| --- | --- |
| #137 | 阴影视图选择几何 LOD；固定阴影投影时修改主相机，缓存帧候选从 12 降为 0，深度和图像保持预期。 |
| #138 | 厘米转换、逐光 bias、移动状态、正交、局部 nominal mip、mip 模式及 DOF 分别使用直接行为用例，实际 red→green。 |
| #139 | 分层 Z 范围、缓存 guard 和正交 target origin 分别实际 red→green。 |
| #140 | 两页池 A→B→A→空闲32帧→C 保留最近访问 A，B 再请求恢复与 fresh-state 一致。 |
| #141 | 动态页更新时复用静态深度；实际 allocator dirty0→dirty1，同时保留静态 generation7。 |
| #142 | 全局页压力、逐视图 HW/SW 工作、时间预算和光实体身份沿对应生产数据反馈；直接用例通过。 |
| #144 | Vulkan SW 用户 UV 覆盖与运行中材质修改的直接行为通过。 |
| #145 | 方向/局部 mip 数 `[3,3,3]→[1,1,3]`；粗页四点 `[0,1,1,1]→[0,0,0,0]`；模式条件 `[0,0]→[1,0]`。 |
| #146 | 生产 resident propagation 后直接采样：unfinished fine 使用已完成 coarse，实际 validity1 / known plane depth .4；1 passed、规则0。原始 TRX 在 `F:/SomeEngine.AgentWork/rg70-remaining/ticket-146/resident-lookup/`。 |
| #147 | 单页读取预算下，高投影细节需求页面先读取；同一用例实际 FIFO red→priority green，1 passed、0 skipped。 |
| #149 | 跨旧网格边界的四页按 UE 窗口原点处理，实际工作数 6→4。 |
| #151 | 远原点的亚单位投影偏移实际 red→green；Runtime 构建和 NativeAOT 编译能力检查通过，图像接合行为继续。 |
| #152 | 用户材质 float3 使用 structured-buffer 布局，HDR 红/绿/蓝分量的实际渲染检查通过。 |

各次源码修改以受影响的公开行为、必要构建和测试规则检查记录结果。完整路径汇合后的 DefaultGame 三场景质量与性能结果在此补记，测量使用 `tools/Measure-Scenes.ps1` 的一次不超过60秒反馈，并保持分辨率、场景行为、渲染选项、JIT 与采样方式一致。
