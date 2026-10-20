# ECS 关系包、生成元数据与 NativeAOT 证据

验证日期：2026-10-20。最终八包及使用方验证统一对应 C# 源码 `c0effa290eee225c296f779a85b9d79109d789a5`。工作树为 `F:\.codex\worktrees\ecs-relations-worker-3\SomeEngine.Next`，实际包版本沿用 `0.1.0-relations.231`，feed/cache/Release输出族沿用原目录。性能来源及最终分区实现见[性能记录](ECS-Relations-Performance-2026-10-19.md#几何空工作在分区形成处剔除r45)。

## #243 最终实际包刷新

[实际生产阶段](evidence/ECS-Relations-2026-10-19/final-packages-r45/codegen-producer-stages.json)保存14个原始阶段的argv、UTC和ExitCode。14项全部Exit0，时间范围01:22:50.2645177Z–01:23:52.0444656Z；原始生产命令归为两个公共任务。六个直接引用恢复到八个SomeEngine包，实际包与生成数量见[产物事实](evidence/ECS-Relations-2026-10-19/final-packages-r45/package-facts.json)。

| 公共生产任务 | 原始阶段数 | 实际队列编号 | 阶段结果 |
| --- | ---: | --- | --- |
| Physics依赖build及八包pack | 9 | `b29fc3fd3ed94505adb27bead7eed42c` | 9项Exit0 |
| 使用方restore、Rebuild、JIT、NativeAOT publish、Native执行 | 5 | `0943bc78afa94cbd816e22a2a2350079` | 5项Exit0 |

两次实际执行均通过1–8元、Item8、加权和204、Count/Any/ref输出与提前结束、Number点读取字面值1、schema/Job元数据、边、键/bitmap/空间组合。串并行首轮均为5对，更新后3对，接触累计为2,2,0,2。Body/Hair、Number和相关规则属于业务例子。

八份nuspec的RepositoryCommit均为本节c0源码。实际消费生成27文件、78个薄入口，按普通关系30、边8、键8、bitmap4、空间28登记；库固定族生成9文件、41个类型声明。使用方IL实际读取17项存储及2项schema注册。[产物事实](evidence/ECS-Relations-2026-10-19/final-packages-r45/package-facts.json)与[登记及恢复包](evidence/ECS-Relations-2026-10-19/final-packages-r45/package-registration-and-packages.json)分别保存这些源数据。

本轮使用方DLL为69,120B、UTC01:23:34.4962378Z；Native对象24,930,157B、UTC01:23:50.7250812Z；Native exe为3,997,696B、UTC01:23:51.4308191Z。读取前后对象的时间和尺寸记录在[实际输入事实](evidence/ECS-Relations-2026-10-19/final-packages-r45/package-frozen-inputs.json)。

### 最终使用方IL与原生路径

消费任务附加的初次读者使用r38旧产物时间和对象尺寸，输入门控返回2；[初次读者](evidence/ECS-Relations-2026-10-19/final-packages-r45/package-initial-reader-stage.json)及[父任务回执](evidence/ECS-Relations-2026-10-19/final-packages-r45/package-initial-parent-result.json)保存该实际失败。修正读者按本轮products中的实际路径关联时间和尺寸，继续读取同一批成功产物。

续接公共CPU任务`5bc38d23ffdd4c2fa48b9a115101dbbb`实际01:29:14.3362221Z–01:29:59.5279665Z执行，Exit0。其中r45性能归档01:29:15.2583546Z–01:29:17.8895118Z、IL/四原生函数读取01:29:17.9387633Z–01:29:59.4771984Z均Exit0。生产前置以14个原始阶段全0、两个实际生产QueueId及同一Source为准，原父任务2和续接读者0分别保留。[完整证明](evidence/ECS-Relations-2026-10-19/final-packages-r45/package-codegen-proof.md)、[选定完整IL](evidence/ECS-Relations-2026-10-19/final-packages-r45/package-selected-methods.il)、[四函数原文](evidence/ECS-Relations-2026-10-19/final-packages-r45/package-four-functions.raw.asm)及[读取回执](evidence/ECS-Relations-2026-10-19/final-packages-r45/package-codegen-queue-result.json)关联实际Source和上述产物。

| 原生函数定位 | 完整节内范围 | 指令行数 | 该函数的实际工作 |
| --- | --- | ---: | --- |
| WeightedTotal8.Matches | 0x20dd0–0x20e2f | 32 | 八个Number引用的字段读取及加权判断 |
| ComponentRelation八元绑定 | 0xe0860–0xe15e9 | 907 | 八位置成员和组件绑定，调用具体WeightedTotal8 |
| World.ReadEntityQueryRef&lt;Number&lt;int&gt;&gt; | 0x197ea0–0x198293 | 300 | 读取查询数据及版本并返回组件引用 |
| SpatialTraversal并行PairArrayOutput填充 | 0x1d4180–0x1d502a | 987 | 闭合Default、Mutual许可、角色键与实际实体对输出 |

四行用于定位各自承担的工作；[统计](evidence/ECS-Relations-2026-10-19/final-packages-r45/package-native-statistics.json)与[实际符号](evidence/ECS-Relations-2026-10-19/final-packages-r45/package-native-symbols.json)各记录一次命中。实际IL还保留Item8、17项存储及2项schema注册、QueryJobAccess读取、空间叶判断和并行count/fill，见[登记与恢复包](evidence/ECS-Relations-2026-10-19/final-packages-r45/package-registration-and-packages.json)。

## 51c实际包刷新（历史）

[最终阶段与实际命令](evidence/ECS-Relations-2026-10-19/final-packages-r38/package-stages.json)记录14个真实阶段、7个公共队列任务及逐项退出；[八包子阶段](evidence/ECS-Relations-2026-10-19/final-packages-r38/package-pack-stages.json)保留同一独占任务内8条原始 no-build 打包命令的起止与退出。[实际包与生成事实](evidence/ECS-Relations-2026-10-19/final-packages-r38/package-facts.json)记录六个直接 PackageReference、八个实际恢复包，八份 nuspec 的 RepositoryCommit 全部为 `51c8ce75949cf16d7190e14028c655f526d5697b`。生产控制过程使用显式 SourceCommit 与 session，在公共队列外提交和收集各任务。

| 公共任务 | 真实阶段数 | 队列编号 | 结果 |
| --- | ---: | --- | --- |
| 依赖与 Physics Release build | 1 | `21b8f27309a94c259b38d14f5bd89192` | exit0 |
| Diagnostics、Job.Contracts、Job、ECS.SourceGen、ECS、ECS.Relations、ECS.Systems、Physics pack | 8 | `85b23478035843e0be2e3e8f7a24e0bd` | 8项exit0 |
| 使用方 fresh restore | 1 | `01372c533eef42feab5f7eeac6ce5a4d` | exit0 |
| 使用方 Rebuild | 1 | `0f985a0162d04f169a988fbe404b4838` | exit0，0警告/0错误 |
| 使用方 JIT 执行 | 1 | `37edd2edd37e48b58e4d53d3b4a9eaa5` | exit0，完整PASS |
| NativeAOT publish | 1 | `249d36807dc840139d4efd93d8c52ec8` | exit0，实际IL2070提示保留 |
| NativeAOT 执行 | 1 | `d5af5248f43c4571ad9890a57823e2ba` | exit0，完整PASS |

两次实际运行均验证1–8元、Item8、加权和204、Count/Any/ref输出提前结束、NumberPointValue经公开TryReadRef取得字面值1、生成元数据和查询读取、边维护、键/bitmap/空间组合。空间输出均为 `parallel/serial=5; updated=3; consumed counts=2,2,0,2`。Body/Hair、Number与相关规则是业务例子。

fresh 生成目录实际为27文件、78个薄入口：普通关系30、边8、键8、bitmap4、空间28；存储登记17项、schema登记2项。Relations库的固定生成输出为9文件、41个类型声明。[登记源码](evidence/ECS-Relations-2026-10-19/final-packages-r38/EcsTypeRegistration.g.cs.txt)及[完整生产说明](evidence/ECS-Relations-2026-10-19/final-packages-r38/package-validation.md)与本次实际产物相对应。

消费方DLL为69,120B，UTC `2026-10-19T22:01:17.5611514Z`；Native对象为25,066,602B，UTC `2026-10-19T22:01:35.5801336Z`。同源IL和四角色Native证据与这些冻结产物共同保存于[本次包证据目录](evidence/ECS-Relations-2026-10-19/final-packages-r38/)。

### 同源IL与四条原生路径

公共CPU读者 `cb3aed562ed146e69b3467d2735f174a` 于22:07:43.0262901Z–22:08:25.1513559Z执行，exit0；读取前核对全部7个生产任务及14个实际阶段。[完整证明入口](evidence/ECS-Relations-2026-10-19/final-packages-r38/package-codegen-proof.md)、[选定完整IL](evidence/ECS-Relations-2026-10-19/final-packages-r38/package-selected-methods.il)与[四函数原文](evidence/ECS-Relations-2026-10-19/final-packages-r38/package-four-functions.raw.asm)保存实际调用路径。四个符号各命中一次。

| 实际闭合函数 | 完整节内范围 | 指令行数 | 实际工作 |
| --- | --- | ---: | --- |
| WeightedTotal8.Matches | 0x20c30–0x20c8f | 32 | 八个Number只读引用的字段读取及加权判断 |
| ComponentRelation八元绑定 | 0xe0740–0xe14c9 | 907 | 八位置成员与组件绑定，0xe11c8直接调用0x20c30 |
| World.ReadEntityQueryRef&lt;Number&lt;int&gt;&gt; | 0x19c1a0–0x19c593 | 300 | 从QueryData引用读取World、State及版本，返回组件引用 |
| SpatialTraversal并行PairArrayOutput填充 | 0x1d8560–0x1d9564 | 1057 | 闭合Default、Mutual位许可、角色键筛选及真实实体对输出 |

四行分别描述各自的工作范围。完整IL另保留Item8、17个存储与2个schema登记、QueryJobAccess实际读取、空间叶判断和并行count/fill路径；这些事实共同对应本节51c源码与实际使用方运行。

## 前次7f8/3b包刷新（历史）

[完整阶段回执](evidence/ECS-Relations-2026-10-19/commands-packages-final.json)保留16个实际执行阶段及生成计数任务的result/stdout/stderr；[生成与包元数据](evidence/ECS-Relations-2026-10-19/package-final-metadata.json)记录两份源码提交、实际包依赖、生成数量与产物时间/字节。六个直接PackageReference恢复到八个`type: package`的SomeEngine依赖，版本均为`0.1.0-relations.231`。全部操作沿下文同一feed/cache/Release/native目录完成。

既有脚本的完整成功流程为14阶段。本次在第12阶段的真实JIT消费中发现Number点读取需要显式读取声明，随后给原成员查询追加`Read<Number<int>>`并提交使用方修正；已生成八个包继续复用，再完成Rebuild/JIT/publish/exe四阶段。使用方实际AssemblyInformationalVersion为`1.0.0+3b582fe541d86d7f556b3f9d6854962d292454ef`，发布命令也显式传入该SourceRevisionId。

| 阶段 | 源码 | 公共队列编号 | 实际结果 |
| --- | --- | --- | --- |
| 1 库Release build | 7f8d27eb | `8a28339eb87146f28934d2f8ba851ba3` | exit0，0警告、0错误 |
| 2 Diagnostics pack | 7f8d27eb | `9a54ee5661ff437480a018355c452fca` | exit0 |
| 3 Job.Contracts pack | 7f8d27eb | `0034cf399ad842eeb7aafe567dfb5ece` | exit0 |
| 4 Job pack | 7f8d27eb | `a5c2cbe78ff64c779b4bcd07e95452ca` | exit0 |
| 5 ECS.SourceGen pack | 7f8d27eb | `1cefbda8cd4c41798c96b52d297d3393` | exit0 |
| 6 ECS pack | 7f8d27eb | `a34dd35dc12d41879f3a01a331dd535d` | exit0 |
| 7 ECS.Relations pack | 7f8d27eb | `0346bc2980f641e8a0b26a92388e6f03` | exit0 |
| 8 ECS.Systems pack | 7f8d27eb | `2bb0fe116cc24daba50d8cd82a559f82` | exit0 |
| 9 Physics pack | 7f8d27eb | `d7c2a32f5a8541c08da493032d095f88` | exit0 |
| 10 使用方restore | 7f8d27eb | `e26a9ef4ff9e426a82f2c1d57e2cef0a` | exit0 |
| 11 使用方Rebuild | 7f8d27eb | `e5d40b58343843a49ac997b30b8ccc50` | exit0，0警告、0错误 |
| 12 使用方JIT红回执 | 7f8d27eb | `b53f4a6144b246a4abe969343c7ae4e5` | exit-532462766，Number点读取缺少data access声明 |
| 13 修正后Rebuild | 3b582fe5 | `d625e6340d5d4abf8645e442bf80ced6` | exit0，0警告、0错误 |
| 14 修正后JIT | 3b582fe5 | `56045e5db9384f1ebb05061f986b777a` | exit0，完整PASS |
| 15 NativeAOT publish | 3b582fe5 | `f93b12d118514bf9b1cb4eea94c91419` | exit0，保留既有IL2070提示 |
| 16 原生exe | 3b582fe5 | `539c1c2010d64157a1de083d14361af4` | exit0，完整PASS |

两次成功运行均实际验证1–8元Where、Item8、加权和204、Within的Count/Any/ref输出提前结束、`NumberPointValue`经`QueryCursor.TryReadRef`取得字面值1、生成元数据/普通查询读取、边身份与维护、键/位图组合。空间实际输出为`parallel/serial=5; updated=3; consumed counts=2,2,0,2`。Number的成员与Read声明在同一原句柄中形成，Within实际消费此次单输入只读借用路径。Body/Hair、Number及其规则属于业务例子。

使用方生成计数来自此次重建目录，公共CPU队列为`af2f8f1bba48441f89dab86c95ced5ea`：

| 生成器 | 实际文件数 | Where | Except | 薄入口合计 |
| --- | ---: | ---: | ---: | ---: |
| RelationGenerator | 11 | 15 | 15 | 30 |
| RelationEdgeGenerator | 4 | 4 | 4 | 8 |
| RelationImplementationGenerator | 1 | 4 | 4 | 8 |
| BitmapRelationGenerator | 2 | 2 | 2 | 4 |
| SpatialRelationGenerator | 7 | 14 | 14 | 28 |
| EcsTypeGenerator | 1 | 0 | 0 | 0 |
| JobEntityGenerator | 1 | 0 | 0 | 0 |
| 合计 | 27 | 39 | 39 | 78 |

此次生成17个Job存储登记、2个schema登记。Relations库的9个固定族/视图文件定义41个固定类型。普通IRelationOutput的Span输出继续只实现身份Add；库内查询输出复用借用行，实际组件引用、行版本、许可回调与输出状态沿各自既有主要持有处。

[最终IL与Native完整方法](evidence/ECS-Relations-2026-10-19/commands-packages-code-final.json)保留使用方与实际包DLL的选定IL块，以及冻结原生对象中四个闭合函数的完整输出；四个原生符号均精确匹配一次。IL队列为`60d0895422044ab1ac184826a2c3d6ee`，Native队列为`e29cdab130204f68a7a777dd5fe27522`，均exit0，DLL/obj的实际时间与字节见同一生成元数据文件。

### 前次3b源码的IL与原生四角色（历史）

下表地址为最终AotSmoke原生对象托管代码节内偏移，范围包含最后一条指令及该方法自身冷路径。来源为上节冻结的7f8库包及3b使用方。

| 实际闭合函数 | 完整节内范围 | 指令行数 | 实际字段、调用与输出 |
| --- | --- | ---: | --- |
| WeightedTotal8.Matches | 0x21030..0x2108e | 32 | 三个引用寄存器及五个栈引用传入八个Number字段；0x2107c取得第八引用，0x21081读取其字段 |
| ComponentRelation_9&lt;WeightedTotal8,Number&lt;int&gt;×8&gt;.Matches | 0xe0b00..0xe1727 | 854 | 0xe1410..0xe1424放置五个组件引用参数，0xe142d直接调用0x21030；完整身份成员与列绑定同在此方法 |
| World.ReadEntityQueryRef&lt;Number&lt;int&gt;&gt; | 0x19d760..0x19db52 | 300 | 0x19dab8形成组件地址并由RAX返回，0x19dac6返回；空结果在0x19dad6返回零RAX |
| SpatialTraversal.Enumerate&lt;DefaultContacts＋BodyBitmap/MutualPermission＋HairCharacterIndex,PairArrayOutput&gt; | 0x1da660..0x1db4f7 | 992 | 0x1db246..0x1db252取得类别数据，0x1db256和0x1db25f执行双方位判断，0x1db317调用具体PairArrayOutput.Add |

最终ComponentRelation八元借用行重载在IL_0003..IL_004e取得八个ComponentReference，IL_005f..IL_0090取得八个只读Value引用，IL_0095/IL_009b经constrained调用具体八元语义。身份重载的第八实体沿IL_00a0/IL_00a5读取Rest.Item1。使用方EighthEntity.Add在IL_001d/IL_0022读取Rest.Item1，IL_0027写出实体，IL_002c/IL_002d返回false，实际保留提前结束。

NumberPointValue的IL_000b调用公开TryReadRef，IL_0017取得只读ComponentReference.Value并读取Number.Value。位图IL同时保留身份/借用行的Matches/Evaluate、两个标量读取、具体静态结果分派和ForwardedMutualPermission的自定义Matches回退。SpatialBitmapFilter.MatchesLeaves在IL_0063/IL_0069调用其具体许可；FillContacts.Execute的IL_000c调用FillPairsParallel，CollisionPairBuffer.Fill在IL_00a2调度具体CountJob，在IL_0215沿前缀依赖调度具体FillJob。对应Execute通过具体ICollisionPartitionEnumerable消费PairCount/PairArrayOutput。

最终登记的17个存储和2个schema写入既有JobStorageTypeMetadata与RelationSchemaCache。NativeAddress为false，其余16个存储为true；ComponentMetadata.CreateOperations记录同一类型分类getter，ComponentInfo读取函数指针，QueryJobAccess.CopyAccesses继续消费该属性并形成既有查询资源访问。完整IL块同时保留写入和这些普通读取路径。

完整刷新入口为：

```powershell
& tools/SomeEngine.ECS.Relations.AotSmoke/Build-Consumer.ps1 -QueueScript 'F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1'
```

脚本中的实际build/pack/restore/run/publish全部经公共队列。`QueueScript`默认按仓库tools定位；本次实测显式使用主检出的现有绝对helper。

## #242 初次实际包与重现入口

以下初次证据对应原`codex/ecs-relations-242`、集成基线`093097a0`，保留原阶段与原生成数量。最终刷新采用上节的源码与实际回执。

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

## #242 初次IL：只读引用、泛型分派和输出状态

本节的342B方法及ReadRef调用对应初次#242源码与当时包产物；#243最终方法和组件借用读取由上节独立列出。

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

## #242 初次NativeAOT：封闭调用、字段读取与位判断

本节保留初次#242对象的符号与地址。#243最终3b使用方的四个实际角色地址见上节。

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
