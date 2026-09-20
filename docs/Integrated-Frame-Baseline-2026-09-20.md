# render、asset、wiki 合并后的帧基线与首轮优化

2026-09-20。工作目录为 `F:\SomeEngine.Next`，分支为 `main`。本次先合并三方内容、构建项目资产并验证画面，再采集新基线，最后实施两项有测量依据的优化。合并前报告中的 2.13 ms 不代表这个版本。

## 版本与资产路径

合并基线提交为 `6bcd1712f4249bc1f4aea55297ad99ef9ae5ce23`。来源包括：

| 来源 | 保留最新未提交内容的检查点或分支 |
|---|---|
| 原主工作区，排除未完成性能实验 | `codex/main-integration-source`，`c4f82a5c` |
| render 工作树 | `codex/render-integration-source`，`ecb8a959` |
| asset 工作树 | `codex/asset-integration-source`，`04e50ff4` |
| wiki 分支 | `codex/wiki-atomic-architecture`，`19da2ae9` |

原主工作区完整内容另存于 `codex/frame-draft-before-integration` 和合并前 stash；原 render、asset 工作树保留。未完成性能草稿没有混入测量基线。SlangShaderSharp 子仓库采用资产工作树检查点 `cbb72f38`。

当前路径是项目的显式 `.shader` 源文件，经 AssetProject 构建为 ShaderAsset / ShaderBlock，再创建 Resources 下的使用对象。具体材质在 DefaultProject 的 Game / Game.Assets 中注册。运行入口是 `Game.App`，`SomeEngine.Runtime` 是库。没有增加 `ShaderImportMetadata`。

```powershell
dotnet run --project tools/GenerateDefaultAssets/SomeEngine.GenerateDefaultAssets.csproj -c Release -- --engine F:/SomeEngine.Next
dotnet build templates/DefaultProject/Game.App/Game.App.csproj -c Release
```

## 低开销整帧测量

硬件为 Xeon E5-2698B v3 / RTX 3080。Release、D3D12、1024 实例、1280×720、异步 compute、TAA 和 VSM 保持开启；隐藏窗口，关闭 VSync 和调试 UI。每次运行 2048 帧，丢弃前 512 帧，统计 1536 帧。计时关闭 GPU validation 和像素读回，仅记录帧边界。trace 均完整，无事件丢失或 scope 错误。

同步模式包含当前帧场景更新、提取、Rendering world 应用、录制、提交、GPU 完成等待和 Present。流水模式反映帧间隔，不能与同步整帧混为一谈。动态材质回放每帧移动 1024 个网格和 3 盏灯，并修改 1 个材质。

| 版本 / 场景 | 平均 ms | p50 ms | p95 ms |
|---|---:|---:|---:|
| 合并基线，静态流水 | 4.4475 | 4.2069 | 5.5541 |
| 合并基线，静态同步第 1 次 | 5.7573 | 5.7637 | 6.7331 |
| 合并基线，静态同步第 2 次 | 5.3372 | 5.2483 | 6.0810 |
| 合并基线，动态与材质变化，同步 | 18.0678 | 17.9251 | 19.7395 |
| 仅批量应用变换，动态与材质变化 | 14.3197 | 14.1990 | 15.3667 |
| 两项优化后，静态同步 | 4.5362 | 4.4007 | 5.5056 |
| 两项优化后，动态与材质变化 | 13.0703 | 12.9948 | 13.9906 |

静态同步以两次基线均值 5.5473 ms 对比，耗时降低约 18.2%，约 1.22 倍速度；动态材质场景耗时降低约 27.7%，约 1.38 倍速度。这是本场景的首轮测量结果，不是所有场景的收益保证。尚未达到 1 ms。

复现参数：

```text
--frames 2048 --hidden-window --no-vsync --no-debug-ui
--synchronous-frames --fixed-step 0.016666667
--trace <新的文件路径> --trace-frames-only
```

静态添加 `--static-scene`；动态材质添加 `--scene-playback materials`。流水对照去掉 `--synchronous-frames`，使用静态场景。

## 合并基线中的 RG、RHI、原生命令开销

单独的详细静态同步 trace 运行 1024 帧，排除前 256 帧。其平均整帧为 5.8832 ms；下表扣除了嵌套调用，均为每帧独占区间耗时。**它不是与 OS 调度事件相交后的实际 CPU 运行时间**，不能把 worker 时间相加当作帧延迟。

| 类别 | 主线程 ms/帧 | worker 合计 ms/帧 |
|---|---:|---:|
| RG，含原有 rg-probe 调度区间 | 0.4952 | 0.0320 |
| RHI 封装 | 0.0911 | 0.1057 |
| 原生 API | 0.7274 | 0.1285 |
| render 系统 | 0.2716 | 0 |
| 显式等待区间 | 4.0764 | 0 |

主线程原生 API 中，Present 约 0.3589 ms，ExecuteCommandLists 约 0.2473 ms。CpuFenceWait 约 4.0457 ms；等待区间不能被称为 CPU 忙碌时间。

详细动态材质 trace 平均 17.9151 ms，其中提取约 1.9813 ms、应用提取结果约 5.0630 ms、CpuFenceWait 约 8.4640 ms。变换逐实体应用和 GPU 工作都是需要处理的部分。

## 合并基线的 CPU / GPU 占比

临时探针在每个录制 batch 两端写入 GPU timestamp，在原有帧资源复用边界读回，通过队列时钟校准转到 CPU QPC。主线程运行时间由同一线程的 GetThreadTimes 累计差计算；不包含其他 CPU 线程，也没有细分就绪和阻塞。

探针先通过 24 帧 GPU validation。正式运行关闭 validation，2048 帧，统计中间连续 1526 个帧周期。GPU 查询会使相关命令无法走现有命令复用路径，并增加查询与读回成本；因此以下结果属于**带探针运行**，不作为低开销优化收益或优化后占比。

| 指标 | ms/帧 | 占同一观测窗口 |
|---|---:|---:|
| 帧周期 | 6.4851 | 100% |
| 主渲染线程实际 CPU 运行 | 1.8942 | 29.21% |
| GPU Graphics 命令区间 | 3.8270 | 59.01% |
| GPU Compute 命令区间 | 0.0214 | 0.33% |
| GPU 队列命令区间并集 | 3.8484 | 59.34% |

CPU 和 GPU 相互重叠，不能相加。GPU 命令区间表示时间戳包围的执行区间，不是芯片各运算单元的硬件利用率。该窗口没有 copy 队列工作。

另一次逐 pass 探针的静态样本中，主要 GPU 区间为：软件光栅约 1.2947 ms、整张 VSM 接收掩码清理约 1.0212 ms、硬件可见性光栅约 0.2714 ms、续接层级遍历 level 1 约 0.2000 ms、材质着色约 0.1991 ms。逐 pass 时间戳本身也有扰动，用于定位，不代替整帧对照。

## 已修改的行为

1. 当提取应用需要事务时，没有用户变换替换回调的变换列使用现有批量查询更新。仍处于同一个事务内；有用户回调时保留逐实体调用，保留回调与回滚语义。
2. VSM 接收掩码复用现有请求索引和间接调度参数，完成光栅读取后仅清理本轮请求过的页。首次初始化仍清理完整状态；没有新建每帧图或第二份页请求状态。读取索引明确声明为计数限制的有效前缀，写入掩码声明为局部覆盖。
3. 删除原 render 工作树带入的命令复用控制台诊断输出。

GPU 校验与像素读回单独执行。32 帧动态材质回放在修改前后均得到像素哈希 `C3C93BE2E450FFD6`，可见簇 4148、着色项 49540；静态 64 帧也通过输出和 GPU validation。

新增 GPU 行为测试覆盖零请求、一个请求、跨线程组的 65 个请求；验证未请求页保持原值。提取测试覆盖公共提取/应用行为及异常回滚。

主工作区 Debug 全解决方案和 Release Game.App 均构建通过，零警告、零错误。最终串行回归中的 Assets 216 项、ECS.Systems 288 项、ECS 540 项、Job 181 项、Render.Cluster 12 项、Render 80 项、RenderGraph 98 项通过。D3D12 整组先通过 282 项、跳过 1 项，另 1 项位移测试仍使用旧 shader 绑定数量；补齐其未使用的 SRV/CBV 空绑定后，该项单独复测通过，原有位移误差与边界断言未放宽。

## 尚未完成的架构与性能目标

这里没有宣称完成全部持久图优化或达到 1 ms。已合并的图存储和执行器具有持久结构与局部更新能力；验证输出的 `analysisReused=0` 只说明未完全跳过分析，不能据此认定每帧完整重建。

仍需逐项处理的重点是：动态提取和材质结果应用中的重复遍历及分配、资源输入切换导致的分析失效范围，以及软件光栅的 GPU 工作量。合并接入中的资源版本捕获也仍需要生命周期审计：Mesh/Texture 当前保留历史捕获直到源使用对象释放，Shader 引用的版本隔离尚不完整。这些不能被称为已满足单一持有者和完整提取隔离的最终设计。

当前证据不足以给出“全部修完必然小于 1 ms”的承诺。应继续保持画面和场景一致，对 CPU 提取、GPU 光栅分别做局部修改与测量；不以降低质量或隐藏等待满足数字。

按项目约定，临时探针、脚本、trace、测试运行日志和临时整合工作树在提炼本报告后删除，报告保留方法与聚合结果。
