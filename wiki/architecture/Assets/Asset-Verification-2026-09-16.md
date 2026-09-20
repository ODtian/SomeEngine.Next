# Asset 重构：最终实施与验证记录

2026-09-16，BOQI-THE-DAEMON。工作树 `F:/SomeEngine.Next/worktrees/asset-rebuild`，分支 `codex/asset-rebuild`；SDK 10.0.112，Windows 10.0.26200 x64，RTX 3080。以下为当前对话直接使用本机工具取得的结果，不沿用历史报告中的测试总数。

## 最终状态

原清单 A00–A33 与 B00–B08 共 43 项完成。原有 Asset 家族、通用编码修正、Block / Importer 新结构以及默认项目均已接入；最后的 A31 整套回归、A32 旧路径和原 Wiki 核对、A33 原生 AOT 与真实应用验收已关闭。平台边界及文件失败保证没有为关闭任务扩大。

通过 App Server 的 `thread/read`、`includeTurns=true` 实际读取讨论 `01a09a21-1ed3-7cb0-af76-6dce44d40c78`，共 6 个 turn，停在组装 API 草案；由其引用找到实际执行 Thread `01a093db-e0fa-7842-aaff-4590c30cc5c2`，共 23 个 turn。执行记录最后仍在迁移收敛，但工作树已有更晚的实现和 40/43 项检查点。本次仅初始化 App Server 并读取上述记录，随后关闭；没有恢复执行、创建工作 turn 或委派本机 Codex。源码修改、验证与文档更新均由当前对话直接完成，既有未提交修改保留。

## 接续时发现并修复的问题

本轮再次按用户原提示词核对 App Server，讨论仍为 6 个 turn、执行对话仍为 23 个 turn；只读取并关闭，没有启动新的工作 turn。磁盘上已有较晚的 43 项验收记录，但复核没有直接采信勾选，而是在当前代码上重跑并补充遗漏的公开行为。

新增 `ProcessOnlyBlocksDoNotAddReferencesToSavedContent` 首次失败：仅进程内 Block 没有写入文件，其引用却进入保存返回的 `AssetEntry`。保存现在从实际写出的 Block 描述生成依赖；当前内存内容及独立分支保留该 Block，重开文件和未知 Block 重存的依赖则与保存结果一致。该变化不修改二进制协议或页管理。

新增 `EveryCloseWaitsForConversionToStopBeforeReleasingItsBlocks` 首次失败：第二个 `DisposeAsync` 在转换仍持有输出时提前完成。Context 现在共享一次关闭结果，拒绝新调用与实际清理完成不再混同；每个关闭等待者都等待转换停止及输出清理。两项修复后的 Block/Context 定向回归共 19 项通过，随后整套回归通过。资产原生 AOT 另实际验证进程内 Block 不进入文件或文件依赖，输出包含 `process-block=ok`。

来源通用复制原先允许跨目录直接复制 glTF 字节，可能把相对 `geometry.bin` 指向目标目录的另一份内容。新增公开回归先复现错误，再令 `CopySourceAsync` 在任何写入前拒绝这种请求。测试确认来源、人工配置、目录、原目录及目标目录的依赖文件全部不变，没有目的文件或临时残留。同目录复制仍创建新来源与元素身份，强类型资产复制和路径改写继续可用。

完整回归首次暴露 Shader 配置测试仍按祖先工程自动定位路径。`SlangShaderImporter.BuildAsync` 补齐显式 `projectRoot`，与已有编译和输入核验入口一致；缺省仍是定义目录。两种配置均验证身份保持、依赖路径、目标切换、保存重开与反射数据，不恢复祖先搜索。

额外独立进程设备移除测试暴露 D3D12 空提交可在设备已移除后返回成功。空提交复用已有完成围栏的移除检查，围栏读取集中在同一函数；没有给普通绘制提交增加轮询或更换设备架构。原测试的提交失败、设备丢失状态、后续创建拒绝及最终关闭全部通过。

## 最后一次构建与测试

| 验证 | 本次结果 | 范围 |
| --- | --- | --- |
| 完整解决方案 Release 构建 | 0 警告、0 错误，18.54 秒 | 含上述全部代码修复 |
| 完整解决方案测试 | 20 个程序集；2625 通过、0 失败、3 条件跳过 | 一次完整命令的结果，不合并不同代码阶段 |
| 独立呈现进程测试 | 17/17 | 按项目原有隔离要求单独运行 |
| 独立设备移除进程测试 | 1/1 | 原失败修复后通过 |
| 12 个正式工具逐项 Release 构建 | 全部 0 警告、0 错误 | 包含不在主解决方案内的工具 |
| 三套原生 AOT | 发布并执行均为 0 退出码 | Assets、Serialization、ECS |
| 引擎外独立项目 | 63 项资产；应用构建 0 警告、0 错误 | 初始 0 个二进制资产、无 Library |
| 真实 D3D12 场景 | 7 组，每组 30 帧，像素验证通过 | 默认项目及全部 6 种既有场景 |
| 真实 Texture 测量 | 9 组，每组预热后 31 样本 | 三种页尺寸与三种像素大小 |
| 通用编码、页与混合生命周期测量 | 执行完成，退出码 0 | 范围读取、分支、视图、复制与回收 |

整套测试中的资产相关主要数量为 Assets 207、外部消费者 79、Serialization 255、Render 85、Cluster 121、RenderGraph 65、D3D12 405、Vulkan 非呈现 43。额外 18 个独立进程用例不包含在 2625 中。三项跳过是两个需要显式配置输入的 ECS 模糊回放测试及一个无 Intel 硬件的测试；本次未新增跳过或缩小既有测试范围。

实际命令：

```powershell
dotnet build SomeEngine.slnx -c Release --nologo -m:4 -v quiet
dotnet test SomeEngine.slnx -c Release --no-build --no-restore --nologo -v minimal
dotnet test tests/SomeEngine.Graphics.Direct3D12.Tests/SomeEngine.Graphics.Direct3D12.Tests.csproj -c Release --no-build --no-restore --filter 'Isolation=PresentationProcess'
dotnet test tests/SomeEngine.Graphics.Direct3D12.Tests/SomeEngine.Graphics.Direct3D12.Tests.csproj -c Release --no-build --no-restore --filter 'Isolation=DeviceLossProcess'
```

原独立进程分组在主测试项目中本来就是分开执行的；不是为避开失败临时增加的筛选。设备移除失败修复后又重新执行整套方案与呈现分组。

## 静态扩展及原生 AOT

三项均通过各自正式工具项目 `dotnet publish ... -c Release -r win-x64` 生成原生可执行文件，并实际运行。资产导入验证输出如下：

```text
assets-import-nativeaot:ok generated-di=ok referenced-block=ok descriptions=ok settings=ok scopes=ok dependency-dedup=ok producer-conflict=ok same-mesh=ok persistence=ok process-block=ok catalog-replace=ok after-release=0
assets-nativeaot-smoke:ok schema=ECD6F00EF9C0FAD8 instances=2 independent-open=ok ready-after-close=ok update=ok extension=ok mesh-file=ok material-inheritance=ok world=ok curve-source=ok text-shapes=ok shader-file=ok
serialization-nativeaot-smoke:ok contractBytes=75 documentBytes=784 plainChunkBytes=6 brotliChunkBytes=16384 nativeElements=2
```

ECS 原生执行结果为 4 个实体、702 字节存档、校验值 -25362004713、异步作业拒绝行为为真、存档代次为 2。Shader 的代码事先由正式工具编译，资产 AOT 检查读取并使用这些代码，不宣称在原生资产检查中执行了 Shader 编译。

## 独立项目、来源修改和重新导入

使用原 `Install.ps1` 安装到引擎目录之外，安装后先确认二进制资产为 0、Library 不存在。该项目自己的 Game.Build 使用明确的 `--project` 与 `--engine`，从来源构建 63 项资产；应用从无关当前目录启动，读取自身 Manifest 和输出。

对项目中的 C# 与顶层 Slang 写入本地文本改动后构建，修改字节保持，63 个资产 ID 和直接引用保持。分别删除必要的 Slang 与 C#，构建均明确失败且不补回文件。恢复人工来源后移除 53 份 `.import.json` 及 `catalog.json`，再次构建全部 63 项资产；资产 ID、Shader 引用及直接依赖均与移除前一致。这是对项目源码不被覆盖和构建记录可丢弃的验证，不把追加文本当成材质语义变化的覆盖。

本轮在新的引擎外目录再次验证：安装时资产产物为 0、Library 不存在；项目自己的内容构建生成 63 项资产，应用构建 0 警告、0 错误。随后从无关当前目录执行默认项目和全部六种场景，各完成 30 帧及像素检查。下表中的候选、可见项、着色项、页数和细分数量在这次复跑中一致，图像摘要与部分光栅计数会随帧执行变化，不将它们写成逐次完全相同的保证。

源码保护及移除构建记录的检查也在该新项目重复执行成功；四次构建依次为成功、缺失 Slang 时拒绝、缺失 C# 时拒绝、移除记录后成功。正式 AssetCook 随后再次输出 `storage-open=ok`，相同 Shader ID 下 DXIL 与 SPIR-V 均有 2 个入口。

正式 AssetCook 又在该引擎外项目构建 `hello_triangle.shader`，通过存储打开并创建 Shader；ID 为 `40101228-9501-58ff-b81a-08767d408801`，配置 `d3d12-sm6.2`，DXIL 和 SPIR-V 各 2 个入口，`storage-open=ok`。

## 最终代码的真实多帧输出

RTX 3080、D3D12、1280×720、30 帧、同步帧、固定步长 0.016666667、隐藏窗口、关闭垂直同步、启用 `--verify-frame-output`。每一组都正常退出，像素验证通过，读取失败为 0。默认场景有 1024 个实例，稀疏移动场景有 128 个实例。

| 场景 | 末帧候选 | 可见项 | 着色项 | 驻留页/全部页 | 细分 patch |
| --- | ---: | ---: | ---: | --- | ---: |
| default | 6572 | 4148 | 49392 | 6/6 | 0 |
| phong | 670 | 332 | 429 | 6/6 | 20480 |
| factors | 176 | 89 | 1838 | 5/5 | 5872 |
| materials | 32 | 20 | 2317 | 4/4 | 1280 |
| wpo-alpha | 44 | 28 | 811 | 8/8 | 0 |
| displacement | 32 | 20 | 1777 | 4/4 | 16652 |
| sparse-motion | 1024 | 640 | 11608 | 3/6 | 0 |

稀疏移动的其余 3 页不在本次所需范围，未强制加载；`missing=3` 与 `failures=0` 区分报告。多材质场景有 4 个实际材质，顶点变形/透明裁剪场景同时出现软件与硬件可见项。结果不是仅清屏或进程存活。

第一次独立应用重建出现 MSBuild 子进程异常退出，未产生源码错误；改用单进程且关闭节点复用后构建完成。场景生成第一次用 `dotnet` 直接启动 DLL，未解析到下游原生编译库；库文件本身存在。正式工具的可执行入口在无关目录正常工作，最终七组均由该入口完成；没有改 PATH、复制系统库或增加当前目录猜测。复现应使用正式可执行入口或 `dotnet run`。

## 本次页与真实资产测量

正式 `SomeEngine.Assets.Benchmarks --samples 31` 完成全部九组。每组排除一次完整预热，查询每样本执行 10000 次。时间和分配来自真实 Texture 的打开、分支、保存、取得及使用对象创建，不以字节数组替代资产。

| 最大页 KiB | 像素字节 | 打开 p50 | 打开 p95 | 元数据分支 p50 | 取得像素 p50 | 首写 p50 | Block 查询 p50 |
| --- | ---: | --- | --- | --- | --- | --- | --- |
| 4 | 4096 | 971.60us | 2470.00us | 12.60us | 503.50us | 16.20us | 98.43ns |
| 4 | 2097152 | 868.90us | 1618.60us | 13.00us | 31196.80us | 34.40us | 141.84ns |
| 4 | 16777216 | 678.40us | 994.20us | 5.10us | 228073.10us | 32.10us | 68.52ns |
| 16 | 4096 | 537.40us | 1071.10us | 4.30us | 264.70us | 10.70us | 68.60ns |
| 16 | 2097152 | 584.00us | 1204.80us | 4.30us | 13687.30us | 27.00us | 68.81ns |
| 16 | 16777216 | 634.70us | 824.10us | 4.90us | 115118.50us | 42.10us | 68.95ns |
| 64 | 4096 | 541.90us | 636.30us | 4.30us | 268.60us | 2.50us | 68.01ns |
| 64 | 2097152 | 588.50us | 1224.20us | 4.50us | 9745.50us | 28.10us | 67.89ns |
| 64 | 16777216 | 656.30us | 899.20us | 4.70us | 84965.70us | 103.70us | 68.19ns |

所有组的打开解码量均为 96 字节、活动页容量为 768 字节；未取得像素的保存额外解码量为 0。重复 Block 查询的分配为 0。16 MiB 资产首次局部写分别复制 4096、16384、65536 字节，而不是整块像素。关闭资产和文件后，创建的 Texture 仍可直接读取保留的数据；所有对象释放并 Trim 后，活动、分配及底层保留全部归零。

通用页混合测试为 257 轮、8 MiB 来源；每组同时测 8 个读取者、128 次 256 KiB 范围取得。下表中的保留数据实际只请求 2816 字节，活动页与底层数组保留分别列出。

| 最大页 KiB | 混合轮 p50 | 混合轮 p99 | 复制/编辑字节比 | 保留活动页字节 | 保留底层字节 | 范围取得吞吐 |
| --- | --- | --- | ---: | ---: | ---: | --- |
| 4 | 288.60us | 2118.10us | 87.25 | 201728 | 2621440 | 631.40MiB/s |
| 16 | 33.50us | 669.70us | 341.10 | 705536 | 3407872 | 2438.69MiB/s |
| 64 | 45.00us | 598.20us | 1356.52 | 2720768 | 5242880 | 6468.17MiB/s |

沿用 256 B 最小页、64 KiB 最大页、256 KiB 分配块、每尺寸档保留 1 个空块的参数。小页减少写放大，大页适合批量取得；长存小视图拖住底层块的成本没有隐藏。操作系统文件缓存未受控，异步分配是进程级统计；这些值不是冷盘带宽、纯 API 延迟或所有负载的最优证明。

## 原 Wiki、清理与边界

最终架构位于原 [对象页](Asset-Objects.md)、[Importer / Context 页](Asset-Import-and-Storage.md)、[代码生成页](Asset-Code-Generation.md)、[文件页](Asset-File-IO.md) 和 [Shader 页](Asset-Shaders.md)。Asset 是身份、引用与生命周期单位；Block 自定强类型内容和取得方式；既有页管理负责大型数据；使用对象保留执行所需资源。SG 只产生 Microsoft DI 注册及编辑器能力描述，不生成完整流程或扩展业务关系。来源选择、单次覆盖、唯一生产者、Context 依赖缓存、保存时服务有效期及语义 Mesh 引用均按这条结构实现。

已核对当前使用路径，没有保留固定导入组装链、旧单 Slang 隐式导入、旧嵌套 Asset 业务字段及三目录文件回退。原通用编码、Slang 编译、ECS 和渲染算法保持；设备移除修复仅复用既有围栏检查。原家族范围与任务要求保留，没有以新最小样例缩小原计划。

Vulkan 仅计 43 项非呈现回归，其窗口呈现仍为原后端未实现边界；不将硬件 D3D12 或 WARP 独立测试写成 Vulkan 多帧成功。本次没有完成 WARP 的完整资产应用场景。新来源格式、其他管线表示与编辑器界面属于可接入扩展点，不列为已经实现的产品功能。多输出保存具有进程内失败回退，不承诺断电下跨独立文件整体原子性。

正式代码、公开测试、性能工具、模板和标准发布产物保留。一次性 App Server 读取文件、独立项目、场景副本、脚本及原始输出在证据提取后清理，清理结果记入执行记录。未创建提交、未合并、未推送，也未清理其他工作树或他人的临时文件。

最终检查已完成：当前 20 份主题及入口文档的 117 个本地引用无缺失，43 项任务全部勾选，`git diff --check` 通过。本轮删除本对话确认的 10 个临时目录、5825 个文件，包括本轮验证输出和前一阶段遗留输出；删除前核对没有已跟踪文件、链接目录或仍使用这些位置的进程。三套正式原生 AOT 发布文件仍在，源码、测试及工具保留。没有清理其他工作树、他人的文件或引擎根目录既有内容，也没有提交或推送。

---

## 以前阶段的记录（非最终状态）

以下保留以前执行阶段的原始陈述。其中的“本轮”、受阻、委派、未完成及测试数字不描述当前对话；最终状态及当前命令结果以上文为准。

## Asset 重构：当前实施与验证记录

更新：2026-09-16，本轮直接使用 BOQI-THE-DAEMON 本机工具。工作树 `F:/SomeEngine.Next/worktrees/asset-rebuild`，分支 `codex/asset-rebuild`。未提交、未推送，未改写主工作区。

### 当前结论与不能关闭的验收

A00–A30 与 B00–B08 的实现和针对性证据已收齐，共 40 项；A31、A32、A33 的最终关闭保留。代码已覆盖全部原资产家族及新的 Block / Importer 架构；原主题 Wiki 与旧入口文档已修订，但不能把项目宣告为 43/43 已验收。

最新完整解决方案测试命令在执行前被工具安全检查拦截，没有产生这一轮全量结果。未将相同测试命令拆分或改写以绕过拦截。较早的回归、当前构建、原生 AOT 和实际应用验证不能代替这项缺口。最后增加的显式库路径及畸形衍生记录检查已编译，但没有本次最新全量执行证明。

A31 等待最终整套回归；A32 的代码及文档清理已实施，仍待 A31 后确认；A33 的 AOT 和应用子项已完成，但完整回归条件未满足。

### Thread 恢复与停止点

本轮已通过 Codex App Server 的 `thread/read(includeTurns=true)` 读取用户指定讨论 `01a09a21-1ed3-7cb0-af76-6dce44d40c78`；讨论停在组装 API 草案。真实执行记录是 `01a093db-e0fa-7842-aaff-4590c30cc5c2`，此前已经继续实现，最后停在全量迁移收敛和新导入链 AOT 验证前。

本轮最初恢复原协调会话，随后两个 Codex 子任务报告额度耗尽。已停止该 App Server 执行，不购买额度、不重复启动；接下来的代码、构建和验证由当前对话直接使用本机工具完成。工作树中其他已经存在的修改保留。

### 本轮关键收敛

Asset 根只承载身份与 Block 描述，具体资产及消费者使用强类型 Block；材质生成参数不再借用旧 Asset 字段。真实 glTF 的两个材质复用同一贴图，只产生三个唯一贴图输出，而不是返回重复记录。

Context、Microsoft DI、静态设置读取、单 Block 生产者竞争、共享依赖调用和多 Block 持久化均进入正式公共测试及原生 AOT。来源人工 `.meta` 与衍生 `.import.json` 分离；旧设置袋、合并构建记录回读及由缓存恢复元素身份的路径已删除。样例来源 ID 和两个元素 ID 保留。

目录只保存 `catalog.json`，不写入或回读三份旧索引。元数据切换保持旧读者的完整文件与新读者的完整目录；37 项来源、目录、文件操作回归通过，100 次连续替换与并发读取又独立重复通过三次。Windows 的静态文件调用还在原生 AOT 中验证了 Unicode 路径与持有旧文件句柄的情况。

Shader 使用独立定义和强类型来源 Block；旧 Slang 单文件隐式导入及独立缓存流程已移除。实际传递依赖、编译器、目标和编码版本参与复用判断，真实输出内容及文档版本也必须匹配。缺失或损坏衍生记录可以重建，不改变人工身份。库路径与项目根显式提供，不回退到当前目录或祖先工程。

截断二进制头按数据错误拒绝，已补覆盖所有截断前缀的公共读取回归。Cluster Shader 测试中的原生对象改为明确逆序关闭，完整 121 项随后通过，未再出现原 COM 终结器崩溃。通用编码、页管理、Slang 算法和渲染算法没有为绕过失败重写。

### 构建与回归证据

| 验证 | 实际结果 | 范围说明 |
| --- | --- | --- |
| 最后一次完整解决方案构建 | 0 警告、0 错误，6.81 秒 | 最新代码，`build-final.log` |
| 12 个正式工具分别构建 | 全部 0 警告、0 错误 | 包括默认构建、AssetCook、场景工具、三套 AOT、性能及诊断工具 |
| Assets 全套 | 202/202 | 旧 Slang 流程及设置回退移除后的实际一轮；不是最后新增检查后的全量结果 |
| 外部消费者 | 79/79 | 静态扩展、强类型设置、DI、真实 glTF 和边界诊断 |
| Serialization | 255/255 | 包含新增截断文档回归 |
| Render | 85/85 | 项目夹具与 Block 消费者迁移后 |
| Cluster | 121/121 | 原生资源释放修复后 |
| Vulkan 非呈现回归 | 43/43 | 不包括窗口呈现 |
| 目录、导入和文件操作定向回归 | 37/37，另三次并发替换重复均通过 | 单目录文件、失败回退和旧读者保持 |
| 最新完整解决方案测试 | 未执行 | 工具在命令执行前拦截，不记为通过或测试失败 |

不合并不同阶段的数字制造一个“最新总通过数”。下方较早报告中的 2,619 项仅属于该报告的阶段，不能包含此后改动。

### 三套原生 AOT：已实际发布并执行

三项均使用 `dotnet publish <project> -c Release -r win-x64`，并运行生成的原生 exe，退出码均为 0。

```text
assets-import-nativeaot:ok generated-di=ok referenced-block=ok descriptions=ok settings=ok scopes=ok dependency-dedup=ok producer-conflict=ok same-mesh=ok persistence=ok catalog-replace=ok after-release=0
assets-nativeaot-smoke:ok schema=ECD6F00EF9C0FAD8 instances=2 independent-open=ok ready-after-close=ok update=ok extension=ok mesh-file=ok material-inheritance=ok world=ok curve-source=ok text-shapes=ok shader-file=ok
serialization-nativeaot-smoke:ok contractBytes=75 documentBytes=784 plainChunkBytes=6 brotliChunkBytes=16384 nativeElements=2
ECS: entityCount=4 durableSaveBytes=702 semanticChecksum=-25362004713 asyncJobRejected=true durableGeneration=2
```

这包括实际执行新文件替换调用，不只是编译或观察生成源码。Shader 编译由正式构建工具完成；原生资产检查消费预构建的 Shader 代码。

### 项目首次构建、用户修改与缓存删除

在引擎工作树的独立目录及引擎目录之外的 C 盘临时目录分别安装模板，首次均确认 `.asset` 数量为 0、Library 不存在。独立项目运行自己的 Game.Build，均从人工来源及显式引擎 Shader 库生成 63 项资产。外部目录中的 Game.App 也以 0 警告、0 错误构建，从无关当前目录启动仍读取自己的目录与产物。

公开文件行为验证实际执行了：修改项目 Standard 的 C# 和顶层 Slang 后重新构建，修改字节不被覆盖；分别删除必要的 Slang 和 C# 后构建明确失败，来源没有被补回；恢复来源后移除 53 份 `.import.json` 及目录，重新构建 63 项资产，全部资产 ID、Standard Shader ID 与目录中的直接引用保持。

```text
project-edits: C#=preserved Slang=preserved asset-ids=preserved material-shader-references=preserved
deleted-project-slang: build-rejected missing-source-not-restored
deleted-project-csharp: build-rejected missing-source-not-restored
rebuild-without-derived-data: records=53 assets=63 identities-and-references=preserved
independent-project-edit-check:ok
```

正式 AssetCook 在引擎目录外的独立项目构建 `hello_triangle.shader`，通过存储重开并创建 Shader：ID `40101228-9501-58ff-b81a-08767d408801`，配置 `d3d12-sm6.2`，DXIL 和 SPIR-V 各 2 个入口，`storage-open=ok`。

### 最新实际渲染结果

RTX 3080、硬件 D3D12、30 帧、同步帧、固定步长、隐藏窗口、关闭垂直同步，均启用 `--verify-frame-output`。以下每个应用均正常退出，像素检查通过，读取失败为 0。

| 场景 | 末帧候选 | 可见项 | 着色项 | 其他证据 |
| --- | ---: | ---: | ---: | --- |
| 独立默认项目 | 6572 | 4148 | 49392 | 1024 实例，6/6 页驻留 |
| phong | 670 | 332 | 429 | 20480 细分 patch |
| factors | 176 | 89 | 1838 | 4 实例，5872 细分 patch |
| materials | 32 | 20 | 2317 | 4 个实际材质 |
| wpo-alpha | 44 | 28 | 811 | 软件与硬件路径都有可见项 |
| displacement | 32 | 20 | 1777 | 16652 细分 patch |
| sparse-motion | 1024 | 640 | 11608 | 128 实例，当前选择 3/6 页，未读其余页，0 读取失败 |

引擎目录外的另一份全新项目也完成 30 帧，6572 候选、4148 可见、49392 着色项，6/6 页驻留且读取失败为 0。渲染输出不是零几何或仅清屏。

Vulkan 的现有实现和 `RequestingPresentationIsRejected` 回归明确不支持呈现。实际尝试在设备创建时被拒绝，未计入窗口多帧成功；不因 Asset 任务改写该后端。较早 WARP 尝试没有在本轮完成，已取消，不作为最新成功证据。

### 页参数与性能边界

沿用正式工具已记录的 31 样本真实 Texture 测量及分页压力测量：256 B 最小页、64 KiB 最大页、256 KiB 分配块、每尺寸档保留 1 个空块。4 KiB、16 KiB、64 KiB 页分别服务低写放大与批量吞吐的不同取舍，不增加另一套分配器。

16 MiB Texture 的单字节编辑分别复制 4096、16384、65536 字节，而不是整份负载；未取得负载的保存额外解码为 0，重复强类型查询分配为 0，关闭并 Trim 后回到 0。长存小视图会保留所在较大底层块，活动页容量与底层保留字节分别报告。测试没有控制操作系统文件缓存，不称为冷盘带宽或所有工作负载最优配置。完整测量表仍见本文件保留的阶段数据与执行记录。

### 清理与复现

正式源码、测试、工具、人工模板与本文证据保留。一次性 App Server 读取脚本、测试结果、场景复制目录、独立证明项目和原始日志在提取证据后清理；不删除原工作区、其他执行者文件或引擎根目录既有内容。最后的清理核对记入执行记录。

常用入口：`dotnet build SomeEngine.slnx -c Release`；默认检出模板使用 GenerateDefaultAssets 的 `--engine`；独立项目使用自己的 Game.Build 及显式 `--project`、`--engine`；场景使用 SomeEngine.RenderScenarios；三套 AOT 使用各自正式工具项目。最终完整测试仍需在工具允许执行后取得实际结果，本文不为它填写预期通过数。

---

### 较早工作树阶段报告

以下原记录已在本轮接管前由另一执行过程写入，保留其历史测量和上下文。它的全量数量、第一人称以及 App Server 访问情况不代表本轮：本轮读取 App Server 成功，而最新完整回归未执行。当前实施与验收结论以上面的记录为准。

## Asset 重构验收：2026-09-16

本次在 BOQI-THE-DAEMON 的 `F:\SomeEngine.Next\worktrees\asset-rebuild`、`codex/asset-rebuild` 分支执行。保留开始时已有的大量未提交改动，在现有实现上直接修正和验证；未向本机 Codex 或其他代理分配实施任务，未提交、推送或合并。

统一任务为 A00–A33、B00–B08 共 43 项。新导入架构已接入真实资产、项目模板、外部扩展和使用对象；任务状态见 [执行任务](Asset-Rebuild-Tasks.md)。本页记录实际验证范围，不把历史编译结果、初始化成本或未支持的平台能力写成通过。

### 上下文和本次收口

指定讨论线程 `01a09a21-1ed3-7cb0-af76-6dce44d40c78` 停在导入组装接口讨论；实际执行记录位于它引用的 `01a093db-e0fa-7842-aaff-4590c30cc5c2`。App Server 只读调用被工具层拦截，未取得其响应；上下文实际来自本地已有线程记录。执行记录最后报告 63 个资产及 15 项编译错误；接手时更新后的工作区已能构建，不能将这 15 项历史错误当作当前状态。

本次补齐解决方案遗漏的两个工具，移除 Asset 层旧字节接口、旧平铺文本格式和已无人使用的 glTF 便捷导入入口，迁移场景工具材质访问，修正弃用库调用以及测试中的阻塞保存和空值警告。截断文档检查等已经存在于工作区的新修正重新编译后纳入回归，没有声称由本轮重新实现。

第一次运行使用旧测试程序集和旧 Shader 产物时确有失败。随后从正式项目来源重建，并完整编译再执行。没有放宽 schema 校验、恢复旧格式回退或删除失败业务断言。两项零分配热路径测试补充未计时初始化，仍完整保留重复访问零分配的断言。

### 构建与全量测试

```powershell
dotnet build SomeEngine.slnx -c Release --nologo -m:4 -v quiet
dotnet test SomeEngine.slnx -c Release --no-build --no-restore --nologo -v minimal
```

解决方案构建：**0 警告、0 错误**。新增纳入统一构建的 GenerateDefaultAssets 与 RenderScenarios 均通过。

全量测试：**20 个测试程序集，2,619 通过，0 失败，3 项原有条件跳过，总计 2,622 项**。

| 测试范围 | 通过 | 跳过 |
| --- | ---: | ---: |
| Assets | 201 | 0 |
| Assets 外部消费 | 79 | 0 |
| Serialization | 255 | 0 |
| Resources | 16 | 0 |
| 应用与 World 集成 | 21 | 0 |
| Render | 85 | 0 |
| Cluster | 121 | 0 |
| RenderGraph | 65 | 0 |
| Graphics 公共层 | 22 | 0 |
| D3D12 | 405 | 1 |
| Vulkan | 43 | 0 |
| ECS | 534 | 0 |
| ECS 序列化 | 169 | 0 |
| ECS 生成器 | 39 | 0 |
| ECS 系统 | 287 | 0 |
| ECS 模糊测试 | 17 | 2 |
| Core | 58 | 0 |
| Diagnostics | 20 | 0 |
| Job | 177 | 0 |
| Job.Dots | 5 | 0 |

两个 ECS 条件测试需要指定的回放输入或活动设置；Intel 专用测试因本机没有该硬件跳过。没有为本次重构新增跳过项。

### 两套原生 AOT

```powershell
dotnet publish tools/SomeEngine.Assets.AotSmoke/SomeEngine.Assets.AotSmoke.csproj -c Release -r win-x64
& tools/SomeEngine.Assets.AotSmoke/bin/Release/net10.0/win-x64/publish/SomeEngine.Assets.AotSmoke.exe

dotnet publish tools/SomeEngine.Serialization.AotSmoke/SomeEngine.Serialization.AotSmoke.csproj -c Release -r win-x64
& tools/SomeEngine.Serialization.AotSmoke/bin/Release/net10.0/win-x64/publish/SomeEngine.Serialization.AotSmoke.exe
```

两套均实际发布并执行成功，不只是没有反射调用的静态判断。

资产 AOT 覆盖生成的 Microsoft DI、引用程序集中的 Block、编辑器能力描述、强类型设置、服务作用域、Context 依赖去重、生产者冲突、同 Mesh 多 Block、持久化、独立打开、关闭资产后继续使用、更新、材质继承、World、Curve 文本及 Shader 文件。结束后活跃页为零。

通用序列化 AOT 覆盖静态字段、可选值、联合值、字典、字符串与数值往返，程序成功退出。

### 来源构建与实际多帧应用

工作区模板通过正式入口生成 **63 个项目资产**：

```powershell
dotnet run --project tools/GenerateDefaultAssets/SomeEngine.GenerateDefaultAssets.csproj -c Release --no-build --no-restore -- --engine F:/SomeEngine.Next/worktrees/asset-rebuild
```

模板应用分别使用 D3D12 WARP 与 RTX 3080，执行 `--frames 30 --synchronous-frames --verify-frame-output`，硬件运行另加 `--backend d3d12 --no-vsync`。两次均完成 30 帧，并通过像素输出校验。

随后安装到引擎目录外的新临时目录。安装完成即检查不存在 `.asset` 产物或 Library 缓存，再执行：

```powershell
# $engine 指向本工作树；$project 是不存在的独立项目目录。
& "$engine/templates/DefaultProject/Install.ps1" -Destination $project -EnginePath $engine
dotnet run --project "$project/Game.Build/Game.Build.csproj" -c Release -- --project $project --engine $engine
dotnet build "$project/Game.App/Game.App.csproj" -c Release
& "$project/Game.App/bin/Release/net10.0/Game.App.exe" --backend d3d12 --frames 30 --synchronous-frames --verify-frame-output --no-vsync
```

独立项目从自己的 C#、Shader 定义和模型来源生成 63 个资产。其应用构建也是 0 警告、0 错误；读取自身输出的 Manifest，不使用引擎默认产物回退。

| 实际运行 | 帧数 | 像素校验 | 几何页 | 读取失败 |
| --- | ---: | --- | --- | ---: |
| 工作区模板、WARP | 30 | 通过；406,872 个不同于背景的像素 | 5/6 按需驻留，未读取页不影响本帧 | 0 |
| 工作区模板、RTX 3080 | 30 | 通过；440,858 个不同于背景的像素 | 6/6 | 0 |
| 独立安装项目、RTX 3080 | 30 | 通过；436,123 个不同于背景的像素 | 6/6 | 0 |

独立项目场景含 1,024 个实例，输出 1280×720、至少 256 种颜色。第 30 帧有 6,572 个候选、4,148 个可见项、49,842 个着色项；渲染图 134 个声明 pass、113 个存活 pass，使用既有三队列流程。这些是本次输入的观察值，不作为跨设备或帧间完全相同的图像要求。

Vulkan 应用尝试在设备创建时因呈现能力被拒绝，没有进入资产加载。现有 `VulkanPresentationTests.RequestingPresentationIsRejected` 明确要求该行为；未为资产重构新增窗口呈现实现。**43 项 Vulkan 后端回归通过，不计为 Vulkan 窗口或多帧应用通过。**

### 页和实际资产测量

```powershell
dotnet run --project tools/SomeEngine.Assets.Benchmarks/SomeEngine.Assets.Benchmarks.csproj -c Release --no-build --no-restore -- --samples 31
dotnet run --project tools/SomeEngine.Serialization.Benchmarks/SomeEngine.Serialization.Benchmarks.csproj -c Release --no-build --no-restore
```

真实纹理负载为 4 KiB、2 MiB、16 MiB，比较 4、16、64 KiB 页上限。每组 31 次采样；操作系统文件缓存未控制，不能解释为冷盘速度，测量期间也不是隔离性能实验。基准覆盖公开 API 的成本，不使用裸指针循环代表资产访问。

| 页上限 | 16 MiB 像素取得 p50 / p95 | 16 MiB 负载首写 p50 | 首写复制上限 | 10,000 次 Block 查询分配 |
| --- | --- | --- | --- | ---: |
| 4 KiB | 234.34 / 244.76 ms | 33.7 μs | 4 KiB | 0 B |
| 16 KiB | 116.54 / 121.34 ms | 43.6 μs | 16 KiB | 0 B |
| 64 KiB | 87.54 / 142.64 ms | 102.8 μs | 64 KiB | 0 B |

三组 16 MiB 资产的 Block 查询中位值约 68–70 ns。打开只解码 96 B 的 Block 元数据，活跃页 768 B；仅改元数据保存没有额外解码像素。资产分支的分配约 1,240 B，不随像素负载线性增长。使用对象关闭后活跃页全部归零。

通用页基准中，128 B 至 16 MiB 缓冲的 Duplicate 固定分配 104 B。局部首写复制一页及必要索引路径，不复制完整页表。已初始化的 PageList 元素遍历及编码视图字段重复读取均为零分配；较大前置字段影响视图首次解析，不使每次后置字段读取重新扫描。

混合生命周期以 8 MiB 来源执行 257 轮，保留长期小范围并做短命分支、局部写入和批量操作：

| 页上限 | 每轮 p50 / p95 / p99 | 已请求长期范围 | 实际保留底层空间 |
| --- | --- | ---: | ---: |
| 4 KiB | 281.9 / 1,807.1 / 2,195.8 μs | 2,816 B | 2,621,440 B |
| 16 KiB | 35.4 / 627.3 / 689.6 μs | 2,816 B | 3,407,872 B |
| 64 KiB | 89.2 / 524.1 / 577.1 μs | 2,816 B | 5,242,880 B |

8 个并发工作者、128 次 256 KiB 读取、保留并异步让出后释放的周转测试，4/16/64 KiB 分别约 598/2,269/6,148 MiB/s，所有结束后页占用为零。单个 1 B 范围仍可保留 256 KiB 底层数组，不能用小范围宣称没有保留放大。

首版保留现有默认值：**最小页 256 B、最大页 64 KiB、底层分配 256 KiB，每尺寸档位最多保留一组空分配**。依据是大负载和异步周转成本，不是因为 GPU 页也使用相近大小。经常局部编辑的调用方可配置 16 KiB 等更合适上限；64 KiB 并非每个负载的最优值。小字段实际仍采用较小档位。本次没有引入新的原生页后端。

### 原任务与公开验收对应

| 任务 | 已验证的交付与入口 |
| --- | --- |
| A00–A04 | 完整家族清单、独立资产及 Resources 依赖方向、语义引用、静态登记；外部 Curve 与资产消费测试 |
| B00、B06、B08 | 通用生成独立、明确字段顺序、公共根描述；生成器行为、DocumentOpenTests、未知类型描述 |
| B01–B05 | 共同认证、预算推进、压缩来源、标准缓冲、可定位输出；DocumentSourceTests、DocumentOutputTests、StandardBufferTests、调度与包回归 |
| B07 | IndexedViewTests 与正式字段访问测量；首次解析与重复访问成本分开 |
| A05–A10 | 分级页、活跃保留、结构共享、写入边界与公共缓冲；PageBuffer、PageAccess、PageSource、PageStreaming、TypedPage 和故障测试 |
| A11 | 本页的真实资产、混合生命周期、长期范围和异步周转测量及首版参数 |
| A12–A16 | Block 文本与二进制、冷数据保存、直接生成、创建与显式更新；AssetBlock、AssetBlockSourceText、AssetSource、Curve 和资源行为测试 |
| A17–A21 | 单一目录、来源移动复制、选定版本、配置、输出身份、失败回退和重新导入；AssetFileOperationsTests、AssetImportTests、AssetProject 和存储回归 |
| A22–A27 | Shader 与组合资源、Texture、Mesh、Material、World、应用配置；相应领域测试和两个 AOT 程序 |
| A28–A29 | 同一 Game world 提取、全部工具进入构建、63 项默认资产、独立项目从零构建及实际 30 帧 |
| A30–A31 | 外部 Block/Importer、生成式 DI、使用边界检查、原生 AOT；20 个程序集全量回归 |
| A32 | 移除旧 Asset 数据接口、平铺文本格式和失效 glTF 入口；原主题 Wiki 与 docs 示例同步，无新旧双轨 |
| A33 | 本页构建、2,619 项通过、两套原生 AOT、独立构建、多帧输出及明确平台边界 |

D01–D10 原审查问题仍可在 [序列化审查](Asset-Serialization-Review.md) 查到当时证据；上述验证分别覆盖认证遗漏、预算等待、可写访问与重新共享互斥、压缩来源、标准缓冲、输出能力、生成职责、重复扫描、字段顺序和未知根描述。原审查记录不是另一套待办清单。

### 保留边界

没有声称任意第三方可变对象都自动 COW，没有保证一个 Asset 引用让所有数据驻留，也没有为多个独立输出承诺断电时的整体事务。对象承诺的是当前就绪内容，未来流送仍需显式取得。编辑器消费能力描述的接口已具备，不等于本次另行交付完整编辑器界面；新增来源格式仍由相应扩展实现。

临时运行目录、探针和原始日志在提取上述结果后清理；保留正式工具、测试、项目来源以及本 Wiki 验收记录。
