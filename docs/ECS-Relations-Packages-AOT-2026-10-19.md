# ECS 关系包、生成元数据与 NativeAOT 证据

验证日期：2026-10-19。实现分支：`codex/ecs-relations-242`，基于集成 `093097a0`。单一工作树为 `F:\.codex\worktrees\ecs-relations-worker-3\SomeEngine.Next`，下文的相对目录均位于该树。环境为 SDK 10.0.113、Runtime 10.0.13、Windows win-x64、Visual Studio 2022 VC.Tools。

## 实际包与重现入口

实际使用方位于 `tools/SomeEngine.ECS.Relations.AotSmoke`。项目使用六个直接 PackageReference；`obj/project.assets.json` 中八个 SomeEngine 依赖均为 `type: package`、版本 `0.1.0-relations.231`。SourceGen 包内 analyzer 位于 `analyzers/dotnet/cs/SomeEngine.ECS.SourceGen.dll`，使用方由该 DLL 生成业务入口与类型登记。

| 实际包 | 版本 | 最终 pack 队列编号 |
| --- | --- | --- |
| SomeEngine.Diagnostics | 0.1.0-relations.231 | `80b01d319ac04862be469a136a1c599a` |
| SomeEngine.Job.Contracts | 0.1.0-relations.231 | `73638f4b7cc745dc938b27ba7a19fac9` |
| SomeEngine.Job | 0.1.0-relations.231 | `99c67fc8ccf34a6b9a9a2043ed1bcd7a` |
| SomeEngine.ECS.SourceGen | 0.1.0-relations.231 | `d927bc7f992640e1a3f645d280bec839` |
| SomeEngine.ECS | 0.1.0-relations.231 | `36271a5f246f4cb8a66527201df04281` |
| SomeEngine.ECS.Relations | 0.1.0-relations.231 | `9e9f861fce97494793b399a4b5601d2f` |
| SomeEngine.ECS.Systems | 0.1.0-relations.231 | `ed89a2585f4b47ad8cae6788c43836c0` |
| SomeEngine.Physics | 0.1.0-relations.231 | `ae588b3a26a24088a885bdf975ea4898` |

复现入口：

```powershell
& tools/SomeEngine.ECS.Relations.AotSmoke/Build-Consumer.ps1
```

脚本按 FIFO 提交已确定的八个 pack，依次读取结果目录。全部 build、pack、restore、JIT、publish 和原生执行通过绝对 `F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1` 提交到公共队列。结果目录为 `artifacts/benchmark-queue/<编号>`，其中 `result.json` 保存实际命令、工作目录、时间和退出码，`stdout.log` / `stderr.log` 保存原始输出。

| 数据 | 同树目录 | 后续刷新方式 |
| --- | --- | --- |
| 本地 feed | `artifacts/ecs-relations/feed` | 同版本重新 pack 八个包 |
| 使用方包 cache | `artifacts/ecs-relations/cache` | 脚本替换其中八个包的当前版本后 restore |
| 使用方 Release | `tools/SomeEngine.ECS.Relations.AotSmoke/bin/Release/net10.0/win-x64` | 脚本重建同一目录 |
| 使用方生成源码 | `tools/SomeEngine.ECS.Relations.AotSmoke/obj/Release/net10.0/win-x64/generated` | 脚本清理该目录后重建 |
| NativeAOT obj | `tools/SomeEngine.ECS.Relations.AotSmoke/obj/Release/net10.0/win-x64/native` | publish 更新同一目录 |
| 原生发布 | `tools/SomeEngine.ECS.Relations.AotSmoke/bin/Release/net10.0/win-x64/native-publish` | publish 更新同一目录 |

## 实际执行结果

| 阶段 | 公共队列编号 | 结果 |
| --- | --- | --- |
| 最终库 Release build | `1522485b40804287b66c034d9215d79d` | exit 0，0 警告、0 错误 |
| 实际包 restore | `7d74745053c74dada8268f26cdb4bbb7` | exit 0 |
| 使用方 Release rebuild | `ac9883dd22764a8cb5253881e13cc8bd` | exit 0，0 警告、0 错误 |
| 实际包 JIT 运行 | `c59aab643eda45ec97dace955a6eac28` | exit 0，完整 PASS |
| NativeAOT publish | `01134f2ac0d24949ab4abbb1a0154aa0` | exit 0，生成原生代码并链接 exe |
| 原生 exe 运行 | `9279be3898c24802a3386f52443da040` | exit 0，完整 PASS |

JIT 与原生 exe 的实际输出一致：

```text
numbers: 8 arities, Item8, typed/generated reads; edges: identities, roles, maintenance
spatial: key+bitmap+components; parallel/serial=5; updated=3; consumed counts=2,2,0,2
PASS: packages; arities=1..8; Item8; ref output; schema; typed/generated/query access; edges; key; bitmap; spatial; maintenance; parallel; consumption.
```

`NumberRelations.cs`、`SpatialRelations.cs` 中的 Number、Body、Hair 及语义属于业务例子。固定数据与公开调用的对应结果如下：

| 业务输入 | 实际公开调用 | 固定结果 |
| --- | --- | --- |
| 八个实体的 Number 为 1..8，分别进入八个带 tag 的查询 | Product 的 1..8 元 Where；八元 Except、Count、Any、ref struct 输出 | 每个 arity 一行；八元加权和 204；Item8 等于第八实体；输出 Count 为 1 并提前结束 |
| Number&lt;int&gt;，NativeAddress&lt;int&gt; 的自动属性保存 nint | 生成 IncreaseNumbers.Schedule、普通 ComponentJobAccess、QueryJobAccess | 首值 1→2；两个普通读取入口按生成字段分类拒绝 NativeAddress |
| Wire&lt;int&gt; 平行边与 Joint&lt;int&gt; 无向唯一边 | OutgoingEdges.Where、DistinctEndpoints、deferred retarget、端点 Job、MaintainRelations、schema 读取 | 两条边身份各自保留；投影一对；维护前旧邻接两条；Job 读取当前 target；维护后新入边一条；A/B 角色保留 |
| 四个相交 Body，a/b 同角色，c 另一角色，d static | Within、键 Except、bitmap Where、空间语义组合 | 普通和键实现均五对，单独 bitmap 六对 |
| 同一空间描述、局部 Scheduler、明确 fill/read/output token | maintenance→FillPairsParallel→几何结果消费，并与 FillPairs 对照 | 首轮五对，接触次数 2,2,3,3，几何穿透累计为正 |
| writer 更新 b 的 Hair 与 c 的类别，沿依赖维护并再次消费 | writer→maintenance→parallel fill→consume，随后 Count/Any | 三对 `(a,b),(a,d),(b,d)`；接触次数 2,2,0,2 |

NativeAOT 发布保留了 `JobStorageTypeShape` 对未知封闭类型的按需反射路径；该路径产生一条 IL2070 字段保留提示。本使用方的 17 种封闭存储在程序集初始化时取得生成分类，实际 typed/generated/query/端点与维护读取均沿已登记数据完成。schema 按原有首次读取时序验证。

## 主要持有处与生成归属

`EcsTypeGenerator` 发出一份 `EcsTypeRegistration.g.cs`。实际 17 个 Job 存储登记中，`NativeAddress<int>` 为 false，其余 16 个为 true。登记包含 `Number<int>`、Body、Hair、CollisionBounds、八个位置 tag、Wire&lt;int&gt;、Joint&lt;int&gt; 以及两种构造后的泛型端点。另有两条 schema 登记：Wire 为 directed/parallel/self-edge；Joint 为 undirected/unique-pair/self-edge=false。

登记直接写入 `JobStorageTypeMetadata<T>.s_aliasFree` 和 `RelationSchemaCache<T>`。`ComponentJobAccess<T>`、生成工厂和普通 `QueryJobAccess` 读取同一分类。`ComponentMetadata<T>` 的操作表记录 `JobStorageTypeMetadata<T>.GetIsAliasFree` 函数指针，ComponentInfo 按组件 ID 读取时通过它回到该持有处。

实际生成文件位于使用方上述 generated 目录下的 `SomeEngine.ECS.SourceGen/<生成器>`。按实际方法定义计数：

| 生成器 | 业务输入数量 | Where/Except 薄方法数量 | 类型归属 |
| --- | --- | --- | --- |
| RelationGenerator | 11 个语义 | 22 | 共享 RelationQueryExtensions partial 类 |
| RelationEdgeGenerator | 4 个二元语义 | 8 | 同一扩展类 |
| RelationImplementationGenerator | 1 个键实现 | 8 | 同一扩展类 |
| BitmapRelationGenerator | 1 个 bitmap 实现 | 2 | 同一扩展类 |
| SpatialRelationGenerator | 4 个二元语义、1 个键、1 个 bitmap | 24 | 同一扩展类 |
| 合计 | 本例全部语义与实现 | 64 | 一份共享扩展类型 |

ECS 元数据另有一个程序集初始化类；IncreaseNumbers 的 Job 生成物含一个扩展类及其 Adapter、Cache 两个嵌套类型。业务组件专属生成类型为 0。

Relations 库的 `obj/Release/net10.0/generated/.../RelationFamilyGenerator` 中有八个 RelationFamily 源码和一份视图源码：IRelation、ComponentRelation、ComponentWhere、ComponentExcept 各八个，Product 八个，RelationQueryViews 一个，共 41 个固定类型。这些类型已经编入实际 Relations 包。

## IL：只读引用、泛型分派和输出状态

实际工具为 `ilspycmd 9.1.0.7988`，读取对象为包缓存中的 DLL 和使用方 Release DLL。公共队列结果分别为：

| 读取对象 | 队列编号 | 证据范围 |
| --- | --- | --- |
| SomeEngine.ECS.Relations.dll | `6c6e5ad66b5544118ecc776252628233` | 八元绑定、constrained 语义和输出、状态转发 |
| SomeEngine.ECS.Relations.AotSmoke.dll | `d9edef26cf2b4cc0bd846b773c1ed68c` | 初始化登记、具体业务方法、Item8 输出 |
| SomeEngine.ECS.dll | `c672d284731344a18a0e488f5255dd67` | 单一分类写入、ComponentInfo 操作表 getter、schema 登记 |
| SomeEngine.ECS.Systems.dll | `3b07b2f483614756a200b39607ac7075` | QueryJobAccess 与实际资源映射 |

命令形状由这些 result.json 保存，例如：

```powershell
& F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1 C:\Users\boqi\.dotnet\tools\ilspycmd.exe -il -t 'SomeEngine.ECS.Relations.ComponentRelation`9' artifacts/ecs-relations/cache/someengine.ecs.relations/0.1.0-relations.231/lib/net10.0/SomeEngine.ECS.Relations.dll
```

``ComponentRelation`9.Matches`` 的实际方法体为 342 字节。IL_0114..IL_0145 分别调用八个 `QueryRow.ReadRef<!T1>()` 至 `ReadRef<!T8>()`，返回类型为 `!!0& modreq(InAttribute)`；IL_014a 为 `constrained. !TRelation`，IL_0150 调用八个只读引用参数的 IRelation.Matches。

`RelationFilterOutput.Add` 在 IL_001b 使用 `constrained. !TOutput`，随后把行只读引用传入 IRelationOutput.Add。筛选器把具体输出作为值保存，枚举结束时按既有 finally 路径把已修改的输出状态写回。描述、身份 tuple 与筛选状态的复制承担值语义；组件输入通过 ReadRef 借用。

使用方 `EighthEntity.Add` 为 46 字节，IL_001d 读取八元 tuple 的 `Rest` 地址，IL_0022 读取 `ValueTuple<Entity>.Item1`，IL_0027 把实体值写入 Span；随后返回 false。该实体值复制交付第八位置身份。

初始化方法为 129 字节，IL_0031 的 false 写入 NativeAddress 分类，IL_0037 的 true 写入 Number 分类；其余登记以相同常量调用共享入口。ECS 的 RegisterJobStorage 直接调用主持有处的 Register，后者通过 Volatile.Write 写入单个整数。

Systems 的 QueryJobAccess.CopyAccesses 在 IL_005a 取得 ComponentInfo，IL_006c 调用 get_IsJobAliasFree；ECS getter 的 IL_000b 用 `calli bool()` 调用操作表函数。随后 Systems 的 IL_00c2/IL_00cc 分别调用既有 WorldStorageJobResources.Read/Write。CollisionMaintenance.ScheduleRead 复用这条公共查询资源链。

## NativeAOT：封闭调用、字段读取与位判断

对象为本次 publish 的 `native/SomeEngine.ECS.Relations.AotSmoke.obj`，格式 coff-x86-64。`llvm-objdump --syms` 的公共队列编号为 `70d19f096d564ca09f35f4e1c6719cb2`。从实际符号名选择目标，再用 `-d -r --disassemble-symbols=<符号>` 读取；完整命令由下列结果目录保存。

| 原生方法 | 队列编号 | 实际指令范围 |
| --- | --- | --- |
| WeightedTotal8.Matches | `2ae069a033724684b6e77e2006f2af0b` | 0x20d70..0x20dce，逐引用读取八个整数并加权比较 |
| 封闭 ComponentRelation_9.Matches | `5f9aaca348574fb49e6bfd9a442f944e` | 0xdc610..0xdc9cf，八个 ReadRef 后直接调用具体语义 |
| 封闭 SpatialBitmapFilter_5.MatchesLeaves | `1090dd2a7a504460856bf14ce416f586` | 0xe2250..0xe232f，读取摘要并执行双向位交集判断 |

业务例子对 WeightedTotal8.Matches 设置 NoInlining，使独立语义方法及其调用指令可直接定位。实际八元绑定尾部为：

```asm
dc95c: movq %rsi, %rdx
dc95f: movq %rdi, %r8
dc962: movq %r14, %r9
; 第四至第八个组件引用按 Windows x64 调用约定进入栈参数
dc985: movq %rbx, %rcx
dc988: callq 0x20d70 <...WeightedTotal8__Matches>
```

具体语义方法从 `%rdx`、`%r8`、`%r9` 和栈中的五个引用逐个 `movl` 读取字段；最后一个组件的读取和 8 倍项为：

```asm
20dbc: movq 0x48(%rsp), %rdx
20dc1: movl (%rdx), %edx
20dc3: leal (%rax,%rdx,8), %eax
20dc6: cmpl (%rcx), %eax
20dc8: setge %al
```

实际 bitmap 叶方法把位交集与零判断合为 `testq`：

```asm
e22e0: movq 0x8(%rbp), %rcx
e22e4: movq 0x18(%rbp), %rdx
e22e8: movq 0x8(%rax), %r8
e22ec: movq 0x18(%rax), %rax
e22f0: testq %rdx, %r8
e22f3: jne 0xe22f9
e22f5: xorl %eax, %eax
e22f7: jmp 0xe2302
e22f9: testq %rax, %rcx
e22fc: setne %al
```

同一原生符号清单包含实际封闭的查询、空间筛选、分区 Job 和输出泛型实例。以上摘录与原生完整 PASS 共同覆盖静态分派、独立只读组件引用、位判断及结果消费。

## 聚焦行为检查

测试沿真实 World 与公共访问执行；自动行为规则均为 0 项违规。

| 检查范围 | 队列编号 | 结果 |
| --- | --- | --- |
| GeneratedMetadataTests：无 attribute 登记 schema、泛型字段分类、端点角色及首次验证 | `ace84246c0124518997a5cc6deed4d5e` | 3/3 |
| 存储别名检查、生成写版本、冷 buffer、只读串并行版本 | `c908017ffaf74f3e85982bc138138d1c` | 13/13 |
| 既有非法 schema 首次读取 | `08716126ff0549c3894891a6043e2bfe` | 1/1 |
| 实际维护、并行填充与手写结果消费 | `42d36db0025b4304baa6641113f45fc6` | 1/1 |

首个新增行为的 red 记录为 `ba3aa4a69bea4f8b8bf3c675139de8e2`（缺少生成登记公共入口），随后沿同一公开路径实现并通过。最终功能检查、实际包运行和原生运行共同对应本页源码与包版本。
