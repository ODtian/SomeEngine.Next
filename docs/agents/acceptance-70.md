# #70 验收记录

交付：[PR #136](https://github.com/ODtian/SomeEngine.Next/pull/136)。目标分支为 `codex/someui-base-7b03495`，实现起点 `be6be444f639d9e786914d4b3661f8b8e9441bd0`。

当前状态：[#172](https://github.com/ODtian/SomeEngine.Next/issues/172) 的恢复目标为 P95：1024 运动小球 ≤1 ms、Sponza ≤2 ms、Bistro ≤3 ms。当前 `f02641a4` 三场景完整测量的 P95 为 4.8936 / 8.8709 / 10.4760 ms；PR 为 draft，性能验收继续。#180–#187 接入用户早期深度、动态深度裁剪、static/merged HiZ 与 Native receiver-mask 阶段；动态遮挡移动恢复的单条行为通过。已有行为结果复用，后续执行必要构建和三场景短测。

原版依据为 Epic UE5.8.3 `396c9f059903aed5fec78ecd3d437a40c6415368`，提交说明 `5.8.3 release`。各票记录实际源码调用、GitBlob、SHA-256 和字节数。最终源码为 `31533a97ec6bcc17177694a8d468fdd86f654cd5`；#167/#168 接合缓存行为的 Native 参照状态，#169 明确最高细节源几何参照的适用范围，#170/#171 完成反向深度精度与 directional mip0 行为。

## 实现

- RenderGraph 持有区段、帧上传、回读与 GPU 完成边界；局部高频工作沿当帧子图形成。Game world → Rendering world 保持同一提取通路，用户 shader 解释材质与顶点数据。
- VSM 按 Native 距离和 bias 选择 clipmap，覆盖厘米换算、逐光移动状态、阴影视图几何 LOD、局部 mip、正交/DOF、分层 Z 范围、缓存 guard 和 int64 世界原点。
- 页管理覆盖顺序 LRU、静态/动态深度、resident ancestor 回退、粗页与 receiver mask、窗口划分、逐视图工作/时间预算反馈、流送优先级和局部失效。
- 用户接收者、第一人称 family、froxel/water/translucency/hair、法线与源半径、方向/局部 SMRT、cube face、远光与预滤进入同一生产通路。SW 使用 Native 整数亚像素规则，方向光覆盖与原始深度按 Native 条件分别求值。
- Shader 持有与代码一同构建的入口资源使用数据；安装阶段消费这份数据。Shader 构建、Cluster 与两 RHI 共用 descriptor 地址算法，ParameterBlock child 按层级处理。
- 投射直接读取 atlas 反向深度；跨 clipmap 保留 Native 先减后除顺序与浮点有效位。公开 forward-depth 读取沿原接口转换，directional coarse 请求覆盖 mip0。

当前布局为 Cluster view 768B、VSM view 304B、target view 80B；几何 metadata7、ShaderBlock schema11、Importer31。内容同步顺序为 DefaultCook → Game.App build → ImportedSceneBuild。

蓝噪声资源采用 CC0 64³ void-and-cluster 数据。UE 输入为 128²×64 spatiotemporal 数据；固定来源、维度与许可记录见 [assets/Noise/source.json](../../assets/Noise/source.json)。生产地址、R2 偏移、帧切片与局部射线步长沿对应 Native 规则消费该输入。

## 当前验证

| 范围 | 实际结果 |
| --- | --- |
| 集成源码与内容 | Release 构建 0 warnings / 0 errors；默认 77、Sponza 96、Bistro 2046 资产及 1532 实例构建完成。 |
| 当前支持行为组 | 27 条完成：复用已通过 14 条，原失败 13 条在同条件下完成；GPU 读回与完整颜色/深度用于断言。 |
| 入口数据与显式资源 | 公开 Shader 重载、D3D12 u3/space2 写入 17、Vulkan set2/binding3 写入 17、完整 UV 安装行为通过。 |
| Native 光栅 | SW 亚像素覆盖 0→1；方向光跨 near 的 HW/SW 两行通过；局部光不同 w 的裁剪控制通过。 |
| 缓存参照 | 双方 hard0；接收者与 caster 行为保留首次更新观测，独立参照达到相同 stationary-light LOD 状态。 |
| 深度与 coarse | 三条实际投射精度路径 1→0 通过；两条 directional mip0 用例通过，共享采样路径 40 条已通过结果复用。 |
| 审查 | 固定 `be6…96bf` Standards/Spec 审查已闭环；后续修复片独立窄审查新增问题为 0。 |

Native adaptive 在同帧会因材质箱 wave 成员变化产生 7↔1 射线数，实际投射读回与图像差值已在 #162/#167 记录。#168 的变化通知、GPU 标量和细节页更新数据贯通；独立参照首次 moving bias0 与更新场景 stationary bias−2 的差异由 view11/view9 读回确认。测试以相同参照状态观察缓存结果，生产 Directional/Local 7 rays、Adaptive1 保留。

#160 已按同 Frame63 的实际 shadow target cluster、窗口、实例、页面和接收者数据完成 241 个小球差异像素归因：143 个纹素中心覆盖、96 个实际 LOD6/7 caster 轮廓、1 个接收者/法线偏置、1 个 Native 8 位亚像素边界。完整允许的 LOD6/7 几何与实际选中请求逐点结果相同。当前小球的 234 个遗漏像素均属于这批已归因像素。

#169 的最高细节 GLB RT 报告继续保留全部像素、图像、原始 2% 门槛与结果。当前小球 aggregate 1.59375%、miss 5.216%；Bistro aggregate 4.87030%、miss 5.4476%。原 2% RT 门槛结果为失败；该报告度量最高细节源几何误差。Native 行为验收采用实际阴影视图 LOD、纹素中心和覆盖规则，结合公开行为结果。两场景有效覆盖分别 99.5184% / 99.999493%，self-hit 均为 0；上述逐像素归因范围为小球。

## 性能

使用 `tools/Measure-Scenes.ps1`。最终 `31533a97` 的默认 7 rays、非 trace 结果为 Complete，三个场景均 Measured，含启动整轮 41.59 秒；报告计时 31.01 秒。

| 场景 | 有效采样帧 | 平均帧时间 | P50 | P95 | 首帧 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1024 moving spheres | 326 | 6.16 ms | 6.13 ms | 6.68 ms | 380.06 ms |
| Sponza | 176 | 11.44 ms | 11.43 ms | 11.81 ms | 4826.01 ms |
| Amazon Bistro | 118 | 17.09 ms | 17.01 ms | 18.43 ms | 4372.15 ms |

本轮同配置 `8efbb6d4` → `31533a97`：Sponza 首帧 7821.42 → 4826.01 ms（下降约 38.3%）；Bistro 首帧 16729.16 → 4372.15 ms（下降约 73.9%），从收敛超时采样 0 帧到完整采样 118 帧。`2c72fc17` 的整轮 32.75 秒与首帧 1163.13 / 4285.50 ms 保留在对应正式记录。

两侧为 RTX 3080、.NET 10.0.12、TieredCompilation=false、1280×720、TAA、async compute、mesh shader、256 MiB 几何内存、固定 1/60 步长；小球运动、导入场景静止。采样保留 32 ready / 128 rendered / 3 秒预热 / 2 秒测量。数值为 presented-frame elapsed 与 CPU stage 时间。trace 仅用于定位，另存正式数据。

## 正式数据

当前命令、身份、TRX、RGBA16/beauty 图像、逐像素 JSON/CSV、trace 和报告位于 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/final/`。最终性能结果在 `default-seven-performance-31533/`；#160 归因在 `ticket-160/actual-geometry-diagnosis.md` 与 `actual-geometry/actual-241-triangle-analysis.json`，原始回读以校验后的 ZIP 保存。逐票细节与审查结论同目录记录。源码保留公开行为、域文档和简洁摘要。

`95285a3b9209d1ad85988690ab06d749967a4594` 的既有输出归档：`F:/SomeEngine.AgentWork/rg70-delivery-evidence/acceptance-95285a3b.zip`，18,406,485 bytes，SHA-256 `B416DA58CDB4E2F4FAF9835CF2FA18BE624B78BC3F71C3D5D10882A7B98C0F8C`。生成目录沿根级 `.gitignore` 维护；原始 2,804,288 行生成输出保存于外部归档。

各源码修改以受影响的公开行为、必要构建及行为规则检查闭环；正式失败与后续通过数据共同记录。
