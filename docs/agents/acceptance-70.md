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
| #143 | 零 ray 斜面比较的原版正向坡度容差实际 red→green；局部光半径为0时半平面外接收点全亮，半径 `.01` 时同点落入独立几何预期的半影范围，实际 D3D12 直接行1 passed、0 skipped。生产材质消费法线距离偏置、有效性/遮挡距离结果、方向/局部 SMRT、cube face 切换和深度历史外推；接收投影96B、页面需求240B、采样224B。原始输出在 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/ticket-143/`。 |
| #144 | Vulkan SW 用户 UV 覆盖与运行中材质修改的直接行为通过。 |
| #145 | 方向/局部 mip 数 `[3,3,3]→[1,1,3]`；粗页四点 `[0,1,1,1]→[0,0,0,0]`；模式条件 `[0,0]→[1,0]`。 |
| #146 | 生产 resident propagation 后直接采样：unfinished fine 使用已完成 coarse，实际 validity1 / known plane depth .4；1 passed、规则0。原始 TRX 在 `F:/SomeEngine.AgentWork/rg70-remaining/ticket-146/resident-lookup/`。 |
| #147 | 单页读取预算下，高投影细节需求页面先读取；同一用例实际 FIFO red→priority green，1 passed、0 skipped。 |
| #148 froxel | 实际 `hiz_init` mip0 → 8B packed froxel → 页请求，已知8×8单元内四个接收位置由中心请求覆盖 `[1,0,0,0]` 到原版视图 AABB/frustum 矩形覆盖 `[1,1,1,1]`，实际 red→green、1 passed、0 skipped、规则0。Features/Game.Build 构建0/0，默认内容77资产构建通过；128B参数、primary/water计数及 HQ 像素选择沿同一图区段和请求数据。原始结果在 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/ticket-148/froxel/`。 |
| #148 | 用户 receiver32B→实际 Mark 世界点覆盖 `[1,1,1,1]→[0,1,0,1]`，subsurface 保留背光，unlit/普通背光按声明筛选。用户额外 water-current、translucency-AAprevious-depth、hair 层的覆盖 `[0,0,0,0]→[1,1,1,0]`。两个直接行各1 passed/0 skipped、规则0，Features/D3D12.Tests Release build0/0；原始输出 `F:/SomeEngine.AgentWork/rg70-remaining/ticket-148/`。 |
| #149 | 跨旧网格边界的四页按 UE 窗口原点处理，实际工作数 6→4。 |
| #150 | 保守局部 mip 从1变2；完成帧年龄在光输入重排后仍先更新较旧光源，两个 CPU 行各 red→green。实际 prefilter X/Y/history kernel 的单像素遮挡从 visibility0 到独立期望 `.859375`，历史按原版最小步长推进到 `.86312157`，直接行1 passed/0 skipped、规则0。Features/D3D12.Tests Release build0/0；原始输出在 `F:/SomeEngine.AgentWork/rg70-remaining/ticket-150/`。 |
| #151 | `2^40+1/8` 原点的投影偏移由0到独立期望 `-1/2048`，实际 red→green。公开场景入口下，近 `.1875` 与远 `2^40+.1875` 原点保留源球体的 `1/8` 间距，D3D12 raw Half 位及 beauty 像素逐位一致，1 passed、0 skipped；Runtime/Direct3D12 测试项目构建、规则检查及 NativeAOT 编译能力检查通过。 |
| #152 | 用户材质 float3 使用 structured-buffer 布局，HDR 红/绿/蓝分量的实际渲染检查通过。 |

#143 的同一半影行进一步消费实际 CC0 RGBA8 蓝噪音纹理，标量、R2 偏移、帧切片和
局部射线步长更新全部进入生产采样分支；1 passed、0 skipped。面源的遮挡距离落在
独立平面几何预期 `.49–.51`，点源全亮且 SMRT 遮挡距离为0。默认内容75资产构建通过；
接上真实用户可见性材质后，包含其 receiver entry 的76资产构建通过。
数据为64³三维 void-and-cluster，原版输入为128²×64 spatiotemporal blue noise，差异、
许可和固定来源保存在 `assets/Noise/source.json`。硬阴影参考通过公开 `--vsm-ray-count 0`
选择两类 zero-ray 投射；可见性用户材质使用相同 shading normal、Native Trace 与预滤结果。

各次源码修改以受影响的公开行为、必要构建和测试规则检查记录结果。完整路径汇合后的 DefaultGame 三场景质量与性能结果在此补记，测量使用 `tools/Measure-Scenes.ps1` 的一次不超过60秒反馈，并保持分辨率、场景行为、渲染选项、JIT 与采样方式一致。

#147 的优先级预算公开行实际 FIFO red→priority green，1 passed；原始记录已随95285a3b正式归档。局部 alpha/cache 公开行跨 Native 10帧 moving 阶段固定观察：两侧 cube face 首帧各2项HW工作，进入缓存后各0；global-version对照更新A后无关B重新执行4项，actual red；局部数据范围源更新A后B保持0且完成页保留，色/深度整图与fresh一致，1 passed、0 skipped（21秒）。同一行先定位并修复 per-view counter 的 policy-bit数组越界；shader及CPU scope临时诊断已清理。原始日志/hash/TRX位于 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/ticket-147/local/`，收尾删除打印和集成接口接合复用该最小行为结果并完成必要构建。

#151 新增直接证明位于 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/ticket-151/`。
原版来源核对包含 Nanite 相对视图、clipmap int64 snap 与相对采样地址。
图像用例同时验证完整内容构建和 GPU 可写视图：默认项目按引擎 shader 声明生成缺项输入，
实际构建71资产；视图通过当帧图区段上传到同一设备缓冲，工作反馈与其后消费者共用此数据。

#148 第一人称完整帧公开行 `APlayersWorldRepresentationCastsIntoTheWorldThroughItsFirstPersonShadowFamily`：regular-only caller 的实际图像 red（1 failed，0 skipped，25秒，首次差异 byte16080）→Native FP family 合并 green（1 passed，0 skipped，27秒）。世界替身的完整颜色/深度与同位置普通 caster 一致，参考 caster 移开后阴影消失。固定 level10、4096分辨率、2048密度参考、零射线，以独立距离/密度计算选择同一光 frustum。此前 receiver 区段排序 RG4001、夹具 metrics 和空需求观察分别留作定位记录；af78c88a 的持久区段排序源片接合了主层和额外层。完整内容构建74资产通过，必要C#构建0警告/0错误；收尾保留同一行为参数和有效数据字节，接入 Native 调用方可见性及明确行为命名。原始日志、TRX、图像与hash位于 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/ticket-148/first-person/`。

#153 的当前 Sponza 构建先以旧 normalTS 注入得到 Slang E30015，再以共用法线采样函数得到96资产构建成功（10.99秒）。用户 receiver 与最终着色 fallback 使用同一 glTF NormalScale 解释；必要默认内容77资产及应用构建通过。#154 的点光精度用例按公开视图到光位置关联验证两光全部六面，实际1 passed、0 skipped。原始日志、TRX和生成元数据差异在 F:/SomeEngine.AgentWork/rg70-delivery-evidence/final/ticket-153/ 和 ticket-154/。

#155 的当前 typed304B输入经分配、清页、完成和接收者掩码采样，实际动态/静态各valid1、独立平面depth.25，1 passed、0 skipped、4秒。#156 将材质work/geometry/domain/bary求值共用，当前77资产shader闭包通过；完整FP/caster/receiver图像公开行1 passed、0 skipped、26秒。此完整帧同时完成#159蓝噪音typed资产加载及实际SRV帧绑定，原资产类型和空binding失败日志随green保留。全项目行为规则24项目、0 violations。原始记录在仓库外final/ticket-155和ticket-156。

#157 同1厘米光源半径在1厘米/100厘米距离下，用户receiver生产Mark覆盖实际[1,1]→[1,0]，1 passed、0 skipped、4秒；光向量与source radius共同换算为厘米。#158 的方向光froxel中心Z=-.1、包围体Z=[-.6,.4]，独立plane Z=.15覆盖实际[0,0,0,0]→[1,1,1,1]，1 passed、0 skipped、3秒；选择由Native距离、global/extra/per-VSM bias和绝对level最低0形成，再由AABB/frustum裁剪请求。当前77资产DXIL/SPIRV闭包构建成功；直接行规则0。原始red/green TRX与日志位于final/ticket-157、ticket-158，内容日志位于final/spec-fixes-content。

#160 当前独立平面 Nativehard 投射从遗留归一化常量偏置导致全亮1，修正为原版 slope/ray-start 两项后遮挡0，1 passed、0 skipped、1秒，77资产闭包成功。当前spheres完整源几何重跑保留miss241/4486、aggregate.8997581%、coverage99.518406%，继续该票的实际场景质量修复。#161 将resident祖先结果改为VSM持有的持久缓冲，状态重置时明确清零，关闭阴影的原UV深度gradient直接D3D12/HW行实际RG4001→1 passed、0 skipped（19秒）。#162 原透明caster与缺席caster比较当前Native7ray同48帧，直接D3D12/HW行实际RGBA16差异→1 passed、0 skipped（28秒）；全部原后端参数及图像断言保留。对应原始TRX与日志位于final/ticket-160、ticket-161、ticket-162。

#163 认证Native viewport*256+.5→floor整数亚像素及bounds half-subpixel−1规则接入共用SW核心，DILATE0/pixel-center/top-left保留。独立三角屏幕(.5006,0)/(.5006,2)/(2,0)由实际production像素输出验证中心(.5,.5)：旧coverage0→当前1，1 passed、0 skipped、1秒，当前77资产DXIL/SPIRV闭包成功、规则0。源码来源/blob/SHA和red/green TRX保存在final/ticket-160/raster-sources.json及final/ticket-163；场景160的实际品质因果继续沿当前数据核对。

#164 方向光由compact阶段形成depth-clip条件，768B Cluster视图尾部原padding承接DepthClipEnabled；80B target view的反馈低2bits保留，bit2承接同一条件。BVH/frustum、SW及HW coverage同向消费；HW普通VS/MS/tess在coverage变换前保存原clip.zw，以普通perspective插值并在PS相除写阴影深度。当前跨near-depth caster的公开完整图像行为实际red无阴影→HW/SW各green，共2 passed、0 skipped、21秒；local不同w且位于near plane前的实际SW像素输出控制为全空，1 passed、0 skipped、1秒。当前77资产DXIL/SPIRV闭包成功、必要C#与规则0。另保留最大源几何Spot参考当前agg.25087337%、miss2.1620214%、coverage100%、selfhit0的实际数据；相关原始记录在final/ticket-164。
